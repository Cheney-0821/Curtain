using Microsoft.Win32;

namespace Curtain.App.Services;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Curtain";

    internal static object GetDiagnosticStatus()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            string? command = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            bool exists = !string.IsNullOrWhiteSpace(command);
            string? launcher = GetStableLaunchPath();
            bool stableLauncher = exists && launcher is not null &&
                command!.StartsWith($"\"{launcher}\"", StringComparison.OrdinalIgnoreCase);
            bool backgroundArgument = exists && command!.Contains("--background", StringComparison.OrdinalIgnoreCase);
            return new
            {
                state = "Readable",
                valuePresent = exists,
                stableLauncher,
                backgroundArgument,
                valueMatchesCurrentLauncher = exists && launcher is not null &&
                    string.Equals(command, $"\"{launcher}\" --background", StringComparison.OrdinalIgnoreCase)
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return new { state = "ReadFailed", valuePresent = (bool?)null,
                stableLauncher = (bool?)null, backgroundArgument = (bool?)null,
                valueMatchesCurrentLauncher = (bool?)null };
        }
    }

    internal static string? SetEnabled(bool enabled, string? operationId = null)
    {
        operationId ??= CurtainDiagnostics.NewOperationId();
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
            {
                CurtainDiagnostics.Write("startup.registration.failed", CurtainDiagnosticLevel.Warning,
                    operationId: operationId, stage: "open-run-key", reasonCode: "StartupRunKeyUnavailable");
                return "StartupRunKeyUnavailable";
            }
            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                CurtainDiagnostics.Write("startup.registration.changed", operationId: operationId,
                    stage: "run-key", reasonCode: "Disabled");
                CurtainRuntimeStatus.SetStartupRegistrationState("Disabled");
                return null;
            }

            string? executable = GetStableLaunchPath();
            if (string.IsNullOrWhiteSpace(executable))
            {
                CurtainDiagnostics.Write("startup.registration.failed", CurtainDiagnosticLevel.Warning,
                    operationId: operationId, stage: "resolve-launcher", reasonCode: "StartupPathUnavailable");
                return "StartupPathUnavailable";
            }
            string command = $"\"{executable}\" --background";
            if (command.Length > 260)
            {
                CurtainDiagnostics.Write("startup.registration.failed", CurtainDiagnosticLevel.Warning,
                    operationId: operationId, stage: "validate-command", reasonCode: "StartupPathTooLong");
                return "StartupPathTooLong";
            }
            key.SetValue(ValueName, command, RegistryValueKind.String);
            CurtainDiagnostics.Write("startup.registration.changed", operationId: operationId,
                stage: "run-key", reasonCode: "EnabledBackgroundArgument");
            CurtainRuntimeStatus.SetStartupRegistrationState("EnabledBackgroundArgument");
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            CurtainDiagnostics.WriteException("startup.registration.failed", ex,
                operationId: operationId, stage: "run-key", reasonCode: "StartupUpdateFailed");
            CurtainRuntimeStatus.SetStartupRegistrationState("Failed");
            return "StartupUpdateFailed";
        }
    }

    // Velopack installs a stable launcher next to the versioned 'current' directory.
    // We must NOT register ...\current\Curtain.App.exe because that path may change.
    private static string? GetStableLaunchPath()
    {
        string? currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe)) return null;

        var currentDir = Directory.GetParent(currentExe);
        if (currentDir is not null &&
            string.Equals(currentDir.Name, "current", StringComparison.OrdinalIgnoreCase) &&
            currentDir.Parent is not null)
        {
            string launcher = Path.Combine(currentDir.Parent.FullName, Path.GetFileName(currentExe));
            if (File.Exists(launcher)) return launcher;
        }
        return currentExe;
    }

}
