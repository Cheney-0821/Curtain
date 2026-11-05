using System.Collections.ObjectModel;
using Curtain.App.Interop;
using Curtain.Core.Sessions;
using Curtain.Core.Settings;
using Microsoft.UI.Dispatching;

namespace Curtain.App.Services;

internal sealed class CurtainManager : IDisposable
{
    private readonly TargetRegistry _registry = new();
    private readonly Dictionary<long, CurtainSession> _sessions = new();
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _watchdog;
    private bool _disposed;
    private bool _idleForcedClosed;
    internal WindowProtectionSettings ProtectionDefaults { get; private set; } = new();
    internal void SetProtectionDefaults(WindowProtectionSettings settings)
    {
        ProtectionDefaults = settings;
        foreach (var session in _sessions.Values.ToArray()) session.SetProtectionDefaults(settings);
    }
    internal bool HasWindows => _sessions.Count != 0;
    public ObservableCollection<ProtectedWindowItem> Windows { get; } = new();
    public event Action<string>? Notice;
    public event Action? CollectionChanged;
    internal CurtainManager(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _watchdog = dispatcher.CreateTimer();
        _watchdog.Interval = TimeSpan.FromMilliseconds(300);
        _watchdog.IsRepeating = true;
        _watchdog.Tick += OnTick;
        _watchdog.Start();
    }
    internal ProtectedWindowItem Attach(nint hwnd, bool initiallyClosed = true,
        WindowProtectionSettings? customProtection = null, string? operationId = null, string? ruleId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        operationId ??= CurtainDiagnostics.NewOperationId();
        WindowSelectionResult selection = WindowSelectionEvaluator.Evaluate(hwnd);
        if (!selection.IsSelectable)
        {
            CurtainDiagnostics.Write("window.attach.rejected", CurtainDiagnosticLevel.Warning,
                operationId: operationId, stage: "selection-evaluate", reasonCode: selection.ReasonCode,
                hwnd: hwnd.ToInt64(), errorCode: selection.Win32Error != 0 ? selection.Win32Error : selection.HResult,
                ruleId: ruleId);
            throw new InvalidOperationException(selection.ReasonCode);
        }
        if (TargetWindow.Read(hwnd) is not { } identity)
        {
            CurtainDiagnostics.Write("window.attach.rejected", CurtainDiagnosticLevel.Warning,
                operationId: operationId, stage: "target-identity", reasonCode: "TargetIdentityUnavailable",
                hwnd: hwnd.ToInt64(), ruleId: ruleId);
            throw new InvalidOperationException("TargetIdentityUnavailable");
        }
        CurtainDiagnostics.Write("window.attach.started", operationId: operationId,
            stage: "manager-attach", reasonCode: "Started", hwnd: hwnd.ToInt64(),
            targetProcessId: (int)identity.ProcessId, ruleId: ruleId);
        if (_sessions.TryGetValue(identity.Handle, out var previous))
        {
            if (!previous.IsDestroyed && previous.Target.Identity == identity)
            {
                CurtainDiagnostics.Write("window.attach.reused", operationId: operationId,
                    stage: "manager-attach", reasonCode: "ExistingSession", hwnd: hwnd.ToInt64(),
                    targetProcessId: (int)identity.ProcessId, sessionId: previous.SessionId, ruleId: ruleId);
                return previous.Item;
            }
            Remove(previous, "IdentityReplaced", out _);
        }
        var target = _registry.Attach(identity);
        CurtainSession? session = null;
        try
        {
            session = new(target, _dispatcher, ProtectionDefaults, initiallyClosed, customProtection,
                operationId, ruleId);
            if (_idleForcedClosed) session.SetIdleForcedClosed(true);
            session.Ended += OnEnded;
            session.Notice += OnNotice;
            _sessions.Add(identity.Handle, session);
            Windows.Add(session.Item);
            UpdateRuntimeWindowCounts();
            CollectionChanged?.Invoke();
            CurtainDiagnostics.Write("window.attach.completed", operationId: operationId,
                stage: "manager-attach", reasonCode: "Success", hwnd: hwnd.ToInt64(),
                targetProcessId: (int)identity.ProcessId, sessionId: session.SessionId, ruleId: ruleId);
            return session.Item;
        }
        catch (Exception ex)
        {
            CurtainDiagnostics.WriteException("window.attach.failed", ex, operationId: operationId,
                stage: "session-create", reasonCode: "SessionCreateFailed", hwnd: hwnd.ToInt64(),
                targetProcessId: (int)identity.ProcessId, sessionId: session?.SessionId, ruleId: ruleId);
            session?.Dispose(); _registry.Remove(target); throw;
        }
    }
    internal nint ResolveProtectedWindow(nint hwnd)
    {
        foreach (var session in _sessions.Values)
            if (session.OwnsOverlay(hwnd)) return (nint)session.Target.Identity.Handle;
        return hwnd;
    }
    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        foreach (var session in _sessions.Values.ToArray()) session.RefreshSafely();
        UpdateRuntimeWindowCounts();
    }
    internal void SetIdleForcedClosed(bool forced, bool animate = false)
    {
        if (_disposed || _idleForcedClosed == forced) return;
        _idleForcedClosed = forced;
        foreach (var session in _sessions.Values.ToArray()) session.SetIdleForcedClosed(forced, animate);
    }
    internal void ReapplyIdleForcedClosed()
    {
        if (_disposed || !_idleForcedClosed) return;
        foreach (var session in _sessions.Values.ToArray()) session.ReapplyIdleForcedClosed();
    }
    internal void SetAllClosed(bool closed)
    {
        if (_disposed) return;
        // During idle protection, bulk actions are temporary as well.
        // Keep the baseline manual state intact for "End automatic protection".
        if (!_idleForcedClosed) _registry.SetAllClosed(closed);
        foreach (var session in _sessions.Values.ToArray()) session.SetClosed(closed);
    }
    internal void Detach(ProtectedWindowItem item)
    {
        CurtainSession session = item.Session;
        string operationId = CurtainDiagnostics.NewOperationId();
        CurtainDiagnostics.Write("window.detach.started", operationId: operationId,
            stage: "manager-detach", reasonCode: "UserRequested",
            hwnd: session.Target.Identity.Handle, targetProcessId: (int)session.Target.Identity.ProcessId,
            sessionId: session.SessionId, ruleId: session.RuleId);

        bool removed = Remove(session, "UserRequested", out string resultCode, operationId);
        CurtainDiagnostics.Write(removed ? "window.detach.completed" : "window.detach.failed",
            removed ? CurtainDiagnosticLevel.Information : CurtainDiagnosticLevel.Warning,
            operationId: operationId, stage: "manager-detach",
            reasonCode: resultCode,
            hwnd: session.Target.Identity.Handle, targetProcessId: (int)session.Target.Identity.ProcessId,
            sessionId: session.SessionId, ruleId: session.RuleId);
    }

    private void OnNotice(string message) => Notice?.Invoke(message);

    private void OnEnded(CurtainSession session, string message)
    {
        string reasonCode = message switch
        {
            "TargetClosedWindowRemoved" => "TargetClosed",
            "OverlayErrorWindowRemoved" => "OverlayError",
            _ => "SessionEnded"
        };
        Remove(session, reasonCode, out _);
        Notice?.Invoke(message);
    }

    private bool Remove(CurtainSession session, string reasonCode, out string resultCode, string? operationId = null)
    {
        if (!_registry.Remove(session.Target))
        {
            resultCode = "SessionNotRegistered";
            return false;
        }
        _sessions.Remove(session.Target.Identity.Handle);
        session.Ended -= OnEnded;
        session.Notice -= OnNotice;
        bool cleanupSucceeded = true;
        try
        {
            session.Dispose();
        }
        catch (Exception ex)
        {
            cleanupSucceeded = false;
            CurtainDiagnostics.WriteException("session.cleanup.failed", ex,
                operationId: operationId ?? session.OperationId, stage: "manager-remove",
                reasonCode: "SessionDisposeFailed", hwnd: session.Target.Identity.Handle,
                targetProcessId: (int)session.Target.Identity.ProcessId,
                sessionId: session.SessionId, ruleId: session.RuleId);
        }
        Windows.Remove(session.Item);
        resultCode = cleanupSucceeded ? "Success" : "CleanupIncomplete";
        CurtainDiagnostics.Write(cleanupSucceeded ? "session.ended" : "session.removed.cleanup-incomplete",
            cleanupSucceeded ? CurtainDiagnosticLevel.Information : CurtainDiagnosticLevel.Warning,
            operationId: operationId ?? session.OperationId, stage: "manager-remove", reasonCode: reasonCode,
            hwnd: session.Target.Identity.Handle, targetProcessId: (int)session.Target.Identity.ProcessId,
            sessionId: session.SessionId, ruleId: session.RuleId);
        UpdateRuntimeWindowCounts();
        CollectionChanged?.Invoke();
        return cleanupSucceeded;
    }

    private void UpdateRuntimeWindowCounts() => CurtainRuntimeStatus.SetWindowCounts(
        _sessions.Count, _sessions.Values.Count(session => session.Target.IsClosed));
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watchdog.Stop(); _watchdog.Tick -= OnTick;
        foreach (var session in _sessions.Values.ToArray()) Remove(session, "AppShutdown", out _);
    }
}
