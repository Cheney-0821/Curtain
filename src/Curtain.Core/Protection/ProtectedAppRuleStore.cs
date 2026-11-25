using System.Text.Json;
using Curtain.Core.Settings;

namespace Curtain.Core.Protection;

/// <summary>
/// Atomic, bounded persistence for remembered application rules.
/// File location: %LOCALAPPDATA%\Curtain\protected-apps.json.
/// This store is independent from settings.json so a rule write cannot corrupt user preferences.
/// </summary>
public sealed class ProtectedAppRuleStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumRules = 1000;
    public const long MaximumFileBytes = 2 * 1024 * 1024;

    private readonly string _path;
    private readonly string _backupPath;
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
    private List<ProtectedAppRule> _rules = new();
    private bool _loaded;
    private bool _preserveBackup;

    /// <param name="directory">Directory override for tests; production uses LocalAppData\Curtain.</param>
    public ProtectedAppRuleStore(string? directory = null)
    {
        directory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Curtain");
        _path = Path.Combine(directory, "protected-apps.json");
        _backupPath = _path + ".bak";
    }

    public ProtectedAppRulesLoadResult Load()
    {
        lock (_gate)
        {
            if (_loaded) return new(_rules.ToArray(), false, false);

            if (!File.Exists(_path))
            {
                if (TryRead(_backupPath, out var backupWhenPrimaryMissing, out bool backupEntriesInvalid))
                {
                    _rules = backupWhenPrimaryMissing;
                    _preserveBackup = true;
                    _loaded = true;
                    return new(_rules.ToArray(), true, backupEntriesInvalid, "PrimaryMissingRecoveredFromBackup");
                }

                _rules = new();
                _loaded = true;
                return new(_rules.ToArray(), false, false);
            }

            if (TryRead(_path, out var primary, out bool invalidEntries))
            {
                _rules = primary;
                _preserveBackup = false;
                _loaded = true;
                return new(_rules.ToArray(), false, invalidEntries,
                    invalidEntries ? "InvalidEntriesSkipped" : null);
            }

            if (TryRead(_backupPath, out var backup, out bool backupInvalidEntries))
            {
                _rules = backup;
                _preserveBackup = true;
                _loaded = true;
                return new(_rules.ToArray(), true, backupInvalidEntries, "PrimaryFileRecoveredFromBackup");
            }

            // Do not overwrite or delete an unreadable primary file during Load.
            _rules = new();
            _preserveBackup = true;
            _loaded = true;
            return new(_rules.ToArray(), false, true, "PrimaryAndBackupUnreadable");
        }
    }

    public IReadOnlyList<ProtectedAppRule> Snapshot()
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            return _rules.ToArray();
        }
    }

    public ProtectedAppRule Upsert(ProtectedAppRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        lock (_gate)
        {
            EnsureLoadedLocked();
            ProtectedAppRule normalized = rule.Normalize();
            if (!normalized.IsValid)
                throw new ArgumentException("A valid stable application identity is required.", nameof(rule));

            var updated = new List<ProtectedAppRule>(_rules);
            int index = _rules.FindIndex(existing =>
                existing.RuleId == normalized.RuleId ||
                string.Equals(existing.StableKey, normalized.StableKey, StringComparison.OrdinalIgnoreCase) ||
                ProtectedAppRuleMatcher.CanUpdateRule(existing.Identity, normalized.Identity));

            if (index >= 0)
            {
                ProtectedAppRule previous = _rules[index];
                ProtectedAppIdentity mergedIdentity = normalized.Identity with
                {
                    PackageFamilyName = string.IsNullOrWhiteSpace(normalized.Identity.PackageFamilyName)
                        ? previous.Identity.PackageFamilyName : normalized.Identity.PackageFamilyName,
                    ApplicationUserModelId = string.IsNullOrWhiteSpace(normalized.Identity.ApplicationUserModelId)
                        ? previous.Identity.ApplicationUserModelId : normalized.Identity.ApplicationUserModelId,
                    ExecutablePath = string.IsNullOrWhiteSpace(normalized.Identity.ExecutablePath)
                        ? previous.Identity.ExecutablePath : normalized.Identity.ExecutablePath
                };
                normalized = normalized with
                {
                    Identity = mergedIdentity.Normalize(),
                    RuleId = previous.RuleId,
                    ClearLayouts = normalized.ClearLayouts ?? previous.ClearLayouts,
                    CreatedAtUtc = previous.CreatedAtUtc,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                updated[index] = normalized;
            }
            else
            {
                if (_rules.Count >= MaximumRules)
                    throw new InvalidOperationException("The maximum number of remembered applications has been reached.");
                updated.Add(normalized);
            }

            SaveLocked(updated);
            return normalized;
        }
    }

    public bool SaveClearLayout(Guid ruleId, WindowClearLayout layout)
    {
        if (layout.Areas.Count != 0 && !layout.IsValid)
            throw new ArgumentException("Invalid clear region layout.", nameof(layout));
        lock (_gate)
        {
            EnsureLoadedLocked();
            int index = _rules.FindIndex(rule => rule.RuleId == ruleId);
            if (index < 0) return false;
            var rule = _rules[index];
            var layouts = (rule.ClearLayouts ?? []).Where(saved => !saved.Matches(layout.WindowClass, layout.Slot)).ToList();
            if (layout.Areas.Count != 0)
            {
                if (layouts.Count >= WindowClearLayout.MaxLayouts)
                    throw new InvalidOperationException("The maximum number of clear region layouts has been reached.");
                layouts.Add(layout);
            }
            var updated = new List<ProtectedAppRule>(_rules);
            updated[index] = rule with { ClearLayouts = layouts.ToArray(), UpdatedAtUtc = DateTimeOffset.UtcNow };
            SaveLocked(updated, refreshBackup: layout.Areas.Count == 0);
            return true;
        }
    }

    public bool Remove(Guid ruleId)
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            var updated = _rules.Where(rule => rule.RuleId != ruleId).ToList();
            if (updated.Count == _rules.Count) return false;
            SaveLocked(updated, refreshBackup: true);
            return true;
        }
    }

    public bool RemoveByIdentity(ProtectedAppIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string key = identity.Normalize().StableKey;
        if (string.IsNullOrWhiteSpace(key)) return false;

        lock (_gate)
        {
            EnsureLoadedLocked();
            var updated = _rules.Where(rule =>
                !string.Equals(rule.StableKey, key, StringComparison.OrdinalIgnoreCase) &&
                !ProtectedAppRuleMatcher.CanUpdateRule(rule.Identity, identity)).ToList();
            if (updated.Count == _rules.Count) return false;
            SaveLocked(updated, refreshBackup: true);
            return true;
        }
    }

    private void EnsureLoadedLocked()
    {
        if (_loaded) return;
        // Reentrant lock is permitted by Monitor; Load uses the same gate.
        _ = Load();
    }

    private bool TryRead(string path, out List<ProtectedAppRule> rules, out bool invalidEntries)
    {
        rules = new();
        invalidEntries = false;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > MaximumFileBytes) return false;
            string json = File.ReadAllText(path);
            var document = JsonSerializer.Deserialize<ProtectedAppRulesDocument>(json, _json);
            if (document is null || document.SchemaVersion != CurrentSchemaVersion || document.Rules is null)
                return false;

            foreach (ProtectedAppRule? source in document.Rules)
            {
                if (source is null)
                {
                    invalidEntries = true;
                    continue;
                }

                ProtectedAppRule normalized = source.Normalize();
                if (!normalized.IsValid)
                {
                    invalidEntries = true;
                    continue;
                }

                if (rules.Any(existing => string.Equals(existing.StableKey, normalized.StableKey,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    invalidEntries = true;
                    continue;
                }

                if (rules.Count >= MaximumRules)
                {
                    invalidEntries = true;
                    break;
                }

                rules.Add(normalized);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                   ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            rules = new();
            invalidEntries = true;
            return false;
        }
    }

    private void SaveLocked(List<ProtectedAppRule> updated, bool refreshBackup = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var document = new ProtectedAppRulesDocument
        {
            SchemaVersion = CurrentSchemaVersion,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Rules = updated
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, _json);
        if (bytes.Length > MaximumFileBytes)
            throw new InvalidOperationException("Remembered application rules exceed the storage limit.");

        string temporary = _path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                       16 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path) && !_preserveBackup && !refreshBackup)
            {
                try { File.Copy(_path, _backupPath, overwrite: true); }
                catch (IOException) { /* Primary remains intact; backup is best-effort. */ }
                catch (UnauthorizedAccessException) { /* Primary remains intact; backup is best-effort. */ }
            }

            File.Move(temporary, _path, overwrite: true);
            _rules = updated;
            _preserveBackup = false;
            _loaded = true;
            if (refreshBackup)
            {
                try { File.Copy(_path, _backupPath, overwrite: true); }
                catch (IOException) { /* Primary was committed. */ }
                catch (UnauthorizedAccessException) { /* Primary was committed. */ }
            }
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
