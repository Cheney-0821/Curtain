using Curtain.App.Interop;
using Curtain.Core.Protection;
using Curtain.Core.Sessions;
using Curtain.Core.Settings;
using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;

namespace Curtain.App.Services;

/// <summary>
/// Coordinates persisted application rules with live HWND instances. UI integration supplies
/// the attach delegate so CurtainManager remains the single owner of active overlay sessions.
/// </summary>
internal sealed class ProtectedAppRuleAutoBinder : IDisposable
{
    private readonly ProtectedAppRuleStore _store;
    private readonly ProtectedAppRuleWatcher _watcher;
    private readonly Func<nint, ProtectedAppRule, string, ProtectedWindowItem?> _attach;
    private readonly Func<IEnumerable<nint>> _activeWindowHandles;
    private readonly HashSet<long> _boundHandles = new();
    private readonly HashSet<long> _suppressedHandles = new();
    private readonly Dictionary<long, Guid> _boundRuleByHandle = new();
    private readonly Dictionary<long, WindowIdentity> _boundIdentityByHandle = new();
    private readonly Dictionary<long, WindowIdentity> _suppressedIdentityByHandle = new();
    private readonly Dictionary<long, string> _lastFailureByHandle = new();
    private readonly Dictionary<long, (WindowIdentity Identity, long RetryAfter)> _attachRetryByHandle = new();
    private bool _disposed;

    internal event Action? RulesChanged;
    public ObservableCollection<ProtectedAppRuleListItem> RuleItems { get; } = new();
    internal IReadOnlyList<ProtectedAppRule> Rules => _store.Snapshot();

    internal ProtectedAppRuleAutoBinder(
        DispatcherQueue dispatcher,
        ProtectedAppRuleStore store,
        Func<nint, ProtectedAppRule, string, ProtectedWindowItem?> attach,
        Func<IEnumerable<nint>>? activeWindowHandles = null)
    {
        _store = store;
        _attach = attach;
        _activeWindowHandles = activeWindowHandles ?? (() => Array.Empty<nint>());
        _watcher = new ProtectedAppRuleWatcher(dispatcher, OnWindowEvent);
    }

    internal void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ProtectedAppRuleAutoBinder));
        ProtectedAppRulesLoadResult loaded = _store.Load();
        CurtainDiagnostics.Write("rules.loaded", stage: "protected-app-store",
            reasonCode: loaded.RecoveryCode ?? "Success");
        if (loaded.RecoveredFromBackup || loaded.RecoveredFromInvalidEntries)
            CurtainDiagnostics.Write("rules.recovered", CurtainDiagnosticLevel.Warning,
                stage: "protected-app-store", reasonCode: loaded.RecoveryCode ?? "Recovered");
        RefreshRuleItems();
        _watcher.Start();
    }

    /// <summary>Call after a user manually adds a window. Null custom settings means follow global defaults.</summary>
    internal ProtectedAppRule? RememberSelectedWindow(nint hwnd, WindowProtectionSettings? customProtection,
        string? operationId = null, string? sessionId = null)
    {
        if (_disposed) return null;
        operationId ??= CurtainDiagnostics.NewOperationId();
        ApplicationIdentityReadResult read = ApplicationIdentityReader.TryRead(hwnd);
        if (!read.Success || read.Identity is null)
        {
            CurtainDiagnostics.Write("rules.remember.failed", CurtainDiagnosticLevel.Warning,
                operationId: operationId, stage: "read-application-identity", reasonCode: read.ReasonCode,
                hwnd: hwnd.ToInt64(), errorCode: read.Win32Error, processName: read.ProcessName,
                sessionId: sessionId);
            return null;
        }

        try
        {
            ProtectedAppRule rule = _store.Upsert(new ProtectedAppRule
            {
                Identity = read.Identity,
                DisplayName = read.Identity.DisplayName,
                CustomProtection = customProtection,
                Enabled = true
            });
            _suppressedHandles.Remove(hwnd.ToInt64());
            _suppressedIdentityByHandle.Remove(hwnd.ToInt64());
            _attachRetryByHandle.Remove(hwnd.ToInt64());
            if (TargetWindow.Read(hwnd) is { } targetIdentity)
                _boundIdentityByHandle[hwnd.ToInt64()] = targetIdentity;
            _boundHandles.Add(hwnd.ToInt64());
            _boundRuleByHandle[hwnd.ToInt64()] = rule.RuleId;
            CurtainDiagnostics.Write("rules.remembered", operationId: operationId,
                stage: "protected-app-store", reasonCode: "Success", hwnd: hwnd.ToInt64(),
                sessionId: sessionId, ruleId: rule.RuleId.ToString("N"));
            RefreshRuleItems();
            RulesChanged?.Invoke();
            return rule;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   ArgumentException or InvalidOperationException or System.Security.SecurityException)
        {
            CurtainDiagnostics.WriteException("rules.remember.failed", ex,
                operationId: operationId, stage: "protected-app-store", reasonCode: "StoreWriteFailed",
                hwnd: hwnd.ToInt64(), sessionId: sessionId);
            return null;
        }
    }

    /// <summary>
    /// Prevents the watcher from immediately reattaching an HWND the user just removed.
    /// Suppression ends when that HWND is destroyed; a later application launch can bind again.
    /// </summary>
    internal void SuppressWindow(nint hwnd)
    {
        if (_disposed || hwnd == nint.Zero) return;
        long handle = hwnd.ToInt64();
        _suppressedHandles.Add(handle);
        if (TargetWindow.Read(hwnd) is { } identity)
            _suppressedIdentityByHandle[handle] = identity;
        _boundHandles.Remove(handle);
        _boundIdentityByHandle.Remove(handle);
        _boundRuleByHandle.Remove(handle);
        RefreshRuleItems();
    }

    internal void UpdateWindowProtection(nint hwnd, WindowProtectionSettings? customProtection,
        string? operationId = null, string? sessionId = null)
    {
        if (_disposed || hwnd == nint.Zero || TargetWindow.Read(hwnd) is not { } identity) return;
        operationId ??= CurtainDiagnostics.NewOperationId();
        IReadOnlyList<ProtectedAppRule> rules = _store.Snapshot();
        Guid? ruleId = _boundRuleByHandle.TryGetValue(identity.Handle, out Guid id) ? id : null;
        ProtectedAppRule? existing = ruleId is { } known
            ? rules.FirstOrDefault(rule => rule.RuleId == known)
            : ApplicationIdentityReader.TryRead(hwnd) is { Success: true, Identity: { } appIdentity }
                ? ProtectedAppRuleMatcher.FindMatch(rules, appIdentity)
                : null;
        if (existing is null) return;

        try
        {
            _store.Upsert(existing with { CustomProtection = customProtection });
            CurtainDiagnostics.Write("rules.settings-updated", operationId: operationId,
                stage: "protected-app-store", reasonCode: customProtection is null ? "FollowGlobal" : "Custom",
                hwnd: hwnd.ToInt64(), sessionId: sessionId, ruleId: existing.RuleId.ToString("N"));
            RefreshRuleItems();
            RulesChanged?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   ArgumentException or InvalidOperationException or System.Security.SecurityException)
        {
            CurtainDiagnostics.WriteException("rules.settings-update.failed", ex,
                operationId: operationId, stage: "protected-app-store", reasonCode: "StoreWriteFailed",
                hwnd: hwnd.ToInt64(), sessionId: sessionId, ruleId: existing.RuleId.ToString("N"));
        }
    }

    internal bool RemoveRule(Guid ruleId)
    {
        if (_disposed) return false;
        string operationId = CurtainDiagnostics.NewOperationId();
        try
        {
            bool removed = _store.Remove(ruleId);
            if (removed)
            {
                CurtainDiagnostics.Write("rules.removed", operationId: operationId,
                    stage: "protected-app-store", reasonCode: "UserRequested", ruleId: ruleId.ToString("N"));
                RefreshRuleItems();
                RulesChanged?.Invoke();
            }
            return removed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            CurtainDiagnostics.WriteException("rules.remove.failed", ex,
                operationId: operationId, stage: "protected-app-store", reasonCode: "StoreWriteFailed",
                ruleId: ruleId.ToString("N"));
            return false;
        }
    }

    private void OnWindowEvent(nint hwnd, uint eventType)
    {
        if (_disposed || hwnd == nint.Zero) return;

        if (eventType == 0x8001) // EVENT_OBJECT_DESTROY
        {
            _boundHandles.Remove(hwnd.ToInt64());
            _suppressedHandles.Remove(hwnd.ToInt64());
            _boundIdentityByHandle.Remove(hwnd.ToInt64());
            _suppressedIdentityByHandle.Remove(hwnd.ToInt64());
            _boundRuleByHandle.Remove(hwnd.ToInt64());
            _lastFailureByHandle.Remove(hwnd.ToInt64());
            _attachRetryByHandle.Remove(hwnd.ToInt64());
            RefreshRuleItems();
            return;
        }

        long handle = hwnd.ToInt64();
        if (_boundHandles.Contains(handle) || _suppressedHandles.Contains(handle))
        {
            WindowIdentity? currentIdentity = TargetWindow.Read(hwnd);
            bool sameBound = _boundIdentityByHandle.TryGetValue(handle, out WindowIdentity bound) && currentIdentity == bound;
            bool sameSuppressed = _suppressedIdentityByHandle.TryGetValue(handle, out WindowIdentity suppressed) && currentIdentity == suppressed;
            if (sameBound || sameSuppressed) return;
            _boundHandles.Remove(handle);
            _suppressedHandles.Remove(handle);
            _boundIdentityByHandle.Remove(handle);
            _suppressedIdentityByHandle.Remove(handle);
            _boundRuleByHandle.Remove(handle);
        }
        if (!NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd)) return;
        uint threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
        if (threadId == 0 || processId == 0 || processId == (uint)Environment.ProcessId) return;

        ApplicationIdentityReadResult read = ApplicationIdentityReader.TryRead(hwnd);
        if (!read.Success || read.Identity is null)
        {
            string? possibleRuleId = FindPossibleRuleForDiagnostics(read.ProcessName);
            if (possibleRuleId is not null)
                RecordFailureOnce(hwnd, read.ReasonCode, read.Win32Error, possibleRuleId);
            return;
        }

        ProtectedAppRule? rule = ProtectedAppRuleMatcher.FindMatch(_store.Snapshot(), read.Identity);
        if (rule is null)
        {
            _lastFailureByHandle.Remove(hwnd.ToInt64());
            _attachRetryByHandle.Remove(handle);
            return;
        }

        WindowSelectionResult selectable = WindowSelectionEvaluator.Evaluate(hwnd);
        if (!selectable.IsSelectable)
        {
            RecordFailureOnce(hwnd, selectable.ReasonCode, selectable.Win32Error != 0 ? selectable.Win32Error : selectable.HResult, rule.RuleId.ToString("N"));
            return;
        }

        if (TargetWindow.Read(hwnd) is not { } current) return;
        if (_attachRetryByHandle.TryGetValue(handle, out var retry) && retry.Identity == current &&
            Environment.TickCount64 < retry.RetryAfter) return;
        // A failed construction must not recreate two overlay HWNDs on every two-second poll.
        // Keep retrying transient failures, and let reused HWNDs start immediately.
        _attachRetryByHandle[handle] = (current, Environment.TickCount64 + 30_000);
        string operationId = CurtainDiagnostics.NewOperationId();
        try
        {
            ProtectedWindowItem? attached = _attach(hwnd, rule, operationId);
            if (attached is not null)
            {
                _attachRetryByHandle.Remove(handle);
                _boundHandles.Add(hwnd.ToInt64());
                if (TargetWindow.Read(hwnd) is { } attachedIdentity)
                    _boundIdentityByHandle[hwnd.ToInt64()] = attachedIdentity;
                _boundRuleByHandle[hwnd.ToInt64()] = rule.RuleId;
                _lastFailureByHandle.Remove(hwnd.ToInt64());
                CurtainDiagnostics.Write("rules.window-bound", operationId: operationId,
                    stage: "auto-bind", reasonCode: "Success", hwnd: hwnd.ToInt64(),
                    processName: read.ProcessName, sessionId: attached.Session.SessionId,
                    ruleId: rule.RuleId.ToString("N"));
                RefreshRuleItems();
                RulesChanged?.Invoke();
            }
            else
            {
                RecordFailureOnce(hwnd, "AutoAttachRejected", 0, rule.RuleId.ToString("N"), operationId);
            }
        }
        catch (Exception ex)
        {
            RecordFailureOnce(hwnd, "AutoAttachException", ex.HResult, rule.RuleId.ToString("N"), operationId);
            CurtainDiagnostics.WriteException("rules.auto-bind.failed", ex,
                operationId: operationId, stage: "manager-attach", reasonCode: "AutoAttachException",
                hwnd: hwnd.ToInt64(), ruleId: rule.RuleId.ToString("N"));
        }
    }


    internal void RefreshRuleItems()
    {
        if (_disposed) return;
        try
        {
            var activeHandles = _activeWindowHandles().Select(hwnd => hwnd.ToInt64()).ToHashSet();
            foreach (long staleHandle in _boundRuleByHandle.Keys.Where(handle => !activeHandles.Contains(handle)).ToArray())
            {
                _boundRuleByHandle.Remove(staleHandle);
                _boundHandles.Remove(staleHandle);
                _boundIdentityByHandle.Remove(staleHandle);
            }

            var activeCounts = new Dictionary<Guid, int>();
            foreach (Guid ruleId in _boundRuleByHandle.Values)
                activeCounts[ruleId] = activeCounts.GetValueOrDefault(ruleId) + 1;

            var current = _store.Snapshot();
            foreach (ProtectedAppRuleListItem old in RuleItems.ToArray())
                if (!current.Any(rule => rule.RuleId == old.RuleId)) RuleItems.Remove(old);

            foreach (ProtectedAppRule rule in current)
            {
                int count = activeCounts.GetValueOrDefault(rule.RuleId);
                ProtectedAppRuleListItem? item = RuleItems.FirstOrDefault(existing => existing.RuleId == rule.RuleId);
                if (item is null) RuleItems.Add(new(rule, count));
                else
                {
                    item.Update(rule, count);
                    item.RefreshLocalization();
                }
            }
        }
        catch (Exception ex)
        {
            CurtainDiagnostics.WriteException("rules.list-refresh.failed", ex,
                stage: "rule-list-refresh", reasonCode: "RefreshFailed");
        }
    }


    // Process-name comparison is diagnostic-only: it never authorizes an automatic bind.
    // It lets us report an identity-read failure for a likely remembered app without logging
    // every unrelated process inspected by the periodic desktop reconciliation.
    private string? FindPossibleRuleForDiagnostics(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        foreach (ProtectedAppRule rule in _store.Snapshot())
        {
            string expected = Path.GetFileNameWithoutExtension(rule.Identity.ExecutablePath);
            if (expected.Length > 0 && string.Equals(expected, processName, StringComparison.OrdinalIgnoreCase))
                return rule.RuleId.ToString("N");
        }
        return null;
    }

    private void RecordFailureOnce(nint hwnd, string reasonCode, int errorCode,
        string? ruleId = null, string? operationId = null)
    {
        long handle = hwnd.ToInt64();
        if (_lastFailureByHandle.TryGetValue(handle, out string? previous) && previous == reasonCode) return;
        _lastFailureByHandle[handle] = reasonCode;
        CurtainDiagnostics.Write("rules.window-not-bound", CurtainDiagnosticLevel.Warning,
            operationId: operationId ?? CurtainDiagnostics.NewOperationId(),
            stage: "auto-bind", reasonCode: reasonCode, hwnd: handle, errorCode: errorCode, ruleId: ruleId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher.Dispose();
        _boundHandles.Clear();
        _suppressedHandles.Clear();
        _boundRuleByHandle.Clear();
        _boundIdentityByHandle.Clear();
        _suppressedIdentityByHandle.Clear();
        _lastFailureByHandle.Clear();
        _attachRetryByHandle.Clear();
    }
}
