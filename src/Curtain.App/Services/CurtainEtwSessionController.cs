using System.Diagnostics;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Curtain.App.Services;

/// <summary>
/// Starts a bounded system ETW session for the Curtain EventSource provider via inbox logman.
/// Creating a system trace session is subject to Windows permissions; the session remains local.
/// </summary>
internal sealed class CurtainEtwSessionController : IDisposable
{
    internal const int MaximumSessionDurationSeconds = 24 * 60 * 60;
    internal const string ProviderGuid = "{A4E7E3F1-6E7D-4C21-9B9C-2E47A4F1B620}";
    private readonly string _runToken = Guid.NewGuid().ToString("N")[..8];
    internal string SessionName { get; } = "Curtain-Diagnostics-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("UserSidUnavailable"))))[..16] +
        "-" + Process.GetCurrentProcess().SessionId;
    private string ControlDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Curtain", "EtwControl");
    private string StatePath => Path.Combine(ControlDirectory, SessionName + ".json");
    private FileStream? _ownerLease;
    private FileStream AcquireGuard()
    {
        Directory.CreateDirectory(ControlDirectory);
        return new FileStream(Path.Combine(ControlDirectory, SessionName + ".guard"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }
    private bool ConfirmOwned(string path)
    {
        if (!File.Exists(StatePath)) return false;
        using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
        return doc.RootElement.TryGetProperty("SessionName", out var name) && name.ValueKind == JsonValueKind.String &&
            name.GetString() == SessionName && doc.RootElement.TryGetProperty("OutputPath", out var output) &&
            output.ValueKind == JsonValueKind.String && string.Equals(output.GetString(), path, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(path).StartsWith("curtain-etw-", StringComparison.OrdinalIgnoreCase);
    }
    private void SaveOwnership() => File.WriteAllText(StatePath, JsonSerializer.Serialize(new
        { SessionName, OutputPath, RunToken = _runToken, ParentProcessId = Environment.ProcessId }));
    private void ResetStopped(string reason)
    {
        _running = false; StartedAtUtc = null;
        CurtainDiagnostics.SetActiveEtwPath(null);
        StopWatchdog(); _ownerLease?.Dispose(); _ownerLease = null;
        CurtainRuntimeStatus.SetEtwState(reason);
    }
    private bool _running;
    private Process? _watchdogProcess;

    internal bool IsRunning => _running;
    internal bool NeedsReconciliation => _running && _watchdogProcess?.HasExited == true;
    internal void Reconcile()
    {
        if (NeedsReconciliation) Stop(out _);
    }
    internal DateTimeOffset? StartedAtUtc { get; private set; }
    internal string? OutputPath { get; private set; }

    internal bool Start(out string reasonCode)
    {
        reasonCode = "StartFailed";
        try
        {
            using var guard = AcquireGuard();
            _ownerLease ??= new FileStream(Path.Combine(ControlDirectory, SessionName + ".owner"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var existing = NativeEtwSession.Query(SessionName);
            if (existing.Code == 0)
            {
                if (!ConfirmOwned(existing.OutputPath)) { reasonCode = "SessionOwnershipUnverified"; return false; }
                if (_running && string.Equals(OutputPath, existing.OutputPath, StringComparison.OrdinalIgnoreCase))
                { reasonCode = "AlreadyRunning"; return true; }
                if (!RunLogman(["stop", SessionName, "-ets"], out _, out reasonCode, out _)) return false;
                CurtainDiagnostics.Write("etw.orphan.recovered", stage: "startup-recovery", reasonCode: "OwnedSessionStopped");
            }
            else if (existing.Code != 4201 && existing.Code != 5)
            { reasonCode = "SessionQueryFailed"; return false; }
            _running = false;
            StopWatchdog();
            Directory.CreateDirectory(CurtainDiagnostics.DirectoryPath);
            CurtainDiagnostics.Prune();
            OutputPath = Path.Combine(CurtainDiagnostics.DirectoryPath,
                $"curtain-etw-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{_runToken}.etl");
            SaveOwnership();
            if (!RunLogman(["create", "trace", "-n", SessionName, "-p", ProviderGuid, "0xFFFFFFFF", "5",
                "-o", OutputPath, "-f", "bincirc", "-max", "64", "-y", "-ets"],
                out int error, out reasonCode, out _))
            {
                CurtainDiagnostics.Write("etw.start.failed", CurtainDiagnosticLevel.Warning,
                    stage: "logman-create-ets", reasonCode: reasonCode, errorCode: error);
                return false;
            }
            _running = true;
            StartedAtUtc = DateTimeOffset.UtcNow;
            CurtainDiagnostics.SetActiveEtwPath(OutputPath);
            if (!StartWatchdog(out reasonCode))
            {
                if (RunLogman(["stop", SessionName, "-ets"], out _, out _, out _)) ResetStopped("WatchdogUnavailable");
                return false;
            }
            CurtainRuntimeStatus.SetEtwState("Running");
            CurtainDiagnostics.WriteEtwCaptureState("started", Environment.ProcessId, 0);
            CurtainDiagnostics.Write("etw.started", stage: "logman-create-ets", reasonCode: "Started");
            reasonCode = "Started";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            System.ComponentModel.Win32Exception or System.Security.SecurityException or JsonException)
        {
            reasonCode = ex is IOException ? "SessionLeaseUnavailable" : "SessionControlFailed";
            CurtainDiagnostics.WriteException("etw.start.failed", ex, stage: "session-control", reasonCode: reasonCode);
            return false;
        }
        finally
        {
            if (!_running) { _ownerLease?.Dispose(); _ownerLease = null; CurtainRuntimeStatus.SetEtwState(reasonCode); }
        }
    }

    internal bool Stop(out string reasonCode)
    {
        reasonCode = "NotRunning";
        if (!_running) return true;
        try
        {
            using var guard = AcquireGuard();
            var current = NativeEtwSession.Query(SessionName);
            if (current.Code == 4201)
            { ResetStopped("AlreadyStopped"); reasonCode = "AlreadyStopped"; return true; }
            if (current.Code != 0 || !ConfirmOwned(current.OutputPath))
            {
                reasonCode = "SessionQueryOrOwnershipFailed";
                CurtainDiagnostics.Write("etw.stop.failed", CurtainDiagnosticLevel.Warning,
                    stage: "session-ownership-query", reasonCode: reasonCode, errorCode: (int)current.Code);
                return false;
            }
            CurtainDiagnostics.WriteEtwCaptureState("stopping", Environment.ProcessId, 0);
            if (!RunLogman(["stop", SessionName, "-ets"], out int code, out reasonCode, out _))
            {
                if (NativeEtwSession.Query(SessionName).Code == 4201)
                { ResetStopped("AlreadyStopped"); reasonCode = "AlreadyStopped"; return true; }
                CurtainDiagnostics.Write("etw.stop.failed", CurtainDiagnosticLevel.Warning,
                    stage: "logman-stop-ets", reasonCode: reasonCode, errorCode: code);
                return false;
            }
            ResetStopped("Stopped"); reasonCode = "Stopped";
            CurtainDiagnostics.Write("etw.stopped", stage: "logman-stop-ets", reasonCode: reasonCode);
            if (OutputPath is not null && File.Exists(OutputPath) && new FileInfo(OutputPath).Length > 0) return true;
            reasonCode = "TraceFileMissing";
            CurtainRuntimeStatus.SetEtwState("StoppedTraceMissing");
            CurtainDiagnostics.Write("etw.stop.failed", CurtainDiagnosticLevel.Warning,
                stage: "trace-seal", reasonCode: reasonCode);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or System.Security.SecurityException)
        {
            reasonCode = "StopFailed";
            CurtainDiagnostics.WriteException("etw.stop.failed", ex, stage: "session-control", reasonCode: reasonCode);
            return false;
        }
    }

    private static bool RunLogman(
        string[] arguments,
        out int exitCode,
        out string reasonCode,
        out string output)
    {
        exitCode = -1;
        reasonCode = "LogmanProcessUnavailable";
        output = string.Empty;

        string logman = Path.Combine(Environment.SystemDirectory, "logman.exe");
        var start = new ProcessStartInfo
        {
            FileName = logman,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        var result = BoundedDiagnosticProcess.Run(start, 15_000, 64 * 1024);
        exitCode = result.ExitCode;
        output = result.Output;
        if (result.ReasonCode != "Success") { reasonCode = "Logman" + result.ReasonCode; return false; }
        if (exitCode == 0) { reasonCode = "Success"; return true; }

        reasonCode = exitCode == 5 || exitCode == unchecked((int)0x80070005) ||
                     output.Contains("access is denied", StringComparison.OrdinalIgnoreCase) ||
                     output.Contains("access denied", StringComparison.OrdinalIgnoreCase)
            ? "AdministratorRequired"
            : "LogmanFailed";
        return false;
    }

    private bool StartWatchdog(out string reasonCode)
    {
        try
        {
            string scriptPath = Path.Combine(AppContext.BaseDirectory, "Curtain.EtwWatchdog.ps1");
            if (!File.Exists(scriptPath))
            {
                reasonCode = "WatchdogScriptMissing";
                return false;
            }

            string powershell = Path.Combine(Environment.SystemDirectory,
                "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershell))
            {
                reasonCode = "WatchdogRuntimeUnavailable";
                return false;
            }

            var start = new ProcessStartInfo
            {
                FileName = powershell,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-WindowStyle");
            start.ArgumentList.Add("Hidden");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(scriptPath);
            start.ArgumentList.Add("-ParentProcessId");
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("-SessionName");
            start.ArgumentList.Add(SessionName);
            start.ArgumentList.Add("-StatePath"); start.ArgumentList.Add(StatePath);
            start.ArgumentList.Add("-RunToken"); start.ArgumentList.Add(_runToken);
            start.ArgumentList.Add("-ParentStartTicks");
            start.ArgumentList.Add(Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString());
            start.ArgumentList.Add("-TimeoutSeconds");
            start.ArgumentList.Add((MaximumSessionDurationSeconds + 120).ToString(System.Globalization.CultureInfo.InvariantCulture));

            _watchdogProcess = Process.Start(start);
            if (_watchdogProcess is null)
            {
                reasonCode = "WatchdogStartFailed";
                return false;
            }
            if (_watchdogProcess.WaitForExit(250))
            {
                reasonCode = "WatchdogExitedDuringStartup";
                _watchdogProcess.Dispose();
                _watchdogProcess = null;
                return false;
            }

            reasonCode = "Started";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidOperationException or System.ComponentModel.Win32Exception or
                                   System.Security.SecurityException)
        {
            CurtainDiagnostics.WriteException("etw.watchdog.start-exception", ex,
                stage: "watchdog-start", reasonCode: "WatchdogStartFailed");
            reasonCode = ex.GetType().Name;
            return false;
        }
    }

    private void StopWatchdog()
    {
        Process? watchdog = _watchdogProcess;
        _watchdogProcess = null;
        if (watchdog is null) return;
        try
        {
            if (!watchdog.HasExited) watchdog.Kill(entireProcessTree: true);
            _ = watchdog.WaitForExit(2_000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            CurtainDiagnostics.WriteException("etw.watchdog.stop-exception", ex,
                stage: "watchdog-stop", reasonCode: "WatchdogStopFailed");
        }
        finally { watchdog.Dispose(); }
    }

    public void Dispose()
    {
        if (_running) _ = Stop(out _);
        else StopWatchdog();
    }
}
