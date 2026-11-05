namespace Curtain.App.Services;

internal sealed record CurtainRuntimeStatusSnapshot(
    string SettingsLoadState,
    string StartupRegistrationState,
    bool AutoProtectionEnabled,
    int IdleThresholdMinutes,
    string AutoProtectionScope,
    bool AutoProtectionActive,
    double IdleDurationSeconds,
    int ProtectedWindowCount,
    int ClosedWindowCount,
    bool FullScreenProtectionActive,
    int FullScreenOverlayCount,
    string UpdateState,
    DateTimeOffset? LastUpdateCheckUtc,
    string EtwState);

/// <summary>Holds only current, low-sensitivity state codes for the opt-in diagnostic bundle.</summary>
internal static class CurtainRuntimeStatus
{
    private static readonly object Gate = new();
    private static string _settingsLoadState = "Unknown";
    private static string _startupRegistrationState = "Unknown";
    private static bool _autoProtectionEnabled;
    private static int _idleThresholdMinutes;
    private static string _autoProtectionScope = "Unknown";
    private static bool _autoProtectionActive;
    private static double _idleDurationSeconds;
    private static int _protectedWindowCount;
    private static int _closedWindowCount;
    private static bool _fullScreenProtectionActive;
    private static int _fullScreenOverlayCount;
    private static string _updateState = "NotChecked";
    private static DateTimeOffset? _lastUpdateCheckUtc;
    private static string _etwState = "Disabled";

    internal static CurtainRuntimeStatusSnapshot Snapshot()
    {
        lock (Gate)
            return new(_settingsLoadState, _startupRegistrationState, _autoProtectionEnabled,
                _idleThresholdMinutes, _autoProtectionScope, _autoProtectionActive,
                _idleDurationSeconds,
                _protectedWindowCount, _closedWindowCount, _fullScreenProtectionActive,
                _fullScreenOverlayCount, _updateState, _lastUpdateCheckUtc, _etwState);
    }

    internal static void SetSettingsLoadState(string value) { lock (Gate) _settingsLoadState = value; }
    internal static void SetStartupRegistrationState(string value) { lock (Gate) _startupRegistrationState = value; }
    internal static void SetAutoProtection(bool enabled, int minutes, string scope, bool active,
        double idleSeconds = 0)
    {
        lock (Gate)
        {
            _autoProtectionEnabled = enabled;
            _idleThresholdMinutes = Math.Clamp(minutes, 0, 1440);
            _autoProtectionScope = SafeCode(scope);
            _autoProtectionActive = active;
            _idleDurationSeconds = double.IsFinite(idleSeconds) ? Math.Clamp(idleSeconds, 0, 86_400) : 0;
        }
    }
    internal static void SetWindowCounts(int total, int closed)
    {
        lock (Gate)
        {
            _protectedWindowCount = Math.Max(0, total);
            _closedWindowCount = Math.Clamp(closed, 0, _protectedWindowCount);
        }
    }
    internal static void SetFullScreen(bool active, int overlayCount)
    {
        lock (Gate)
        {
            _fullScreenProtectionActive = active;
            _fullScreenOverlayCount = Math.Max(0, overlayCount);
        }
    }
    internal static void SetUpdateState(string state, bool checkedNow = false)
    {
        lock (Gate)
        {
            _updateState = SafeCode(state);
            if (checkedNow) _lastUpdateCheckUtc = DateTimeOffset.UtcNow;
        }
    }
    internal static void SetEtwState(string state) { lock (Gate) _etwState = SafeCode(state); }

    private static string SafeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unknown";
        string result = new(value.Where(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
            .Take(96).ToArray());
        return result.Length == 0 ? "Unknown" : result;
    }
}
