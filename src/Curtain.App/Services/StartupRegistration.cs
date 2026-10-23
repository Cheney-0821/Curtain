using Microsoft.Win32;

namespace Curtain.App.Services;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Curtain";

    internal static string? SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return "StartupRunKeyUnavailable";
            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return null;
            }

            string? executable = GetStableLaunchPath();
            if (string.IsNullOrWhiteSpace(executable)) return "StartupPathUnavailable";
            string command = $"\"{executable}\" --background";
            if (command.Length > 260) return "StartupPathTooLong";
            key.SetValue(ValueName, command, RegistryValueKind.String);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
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
