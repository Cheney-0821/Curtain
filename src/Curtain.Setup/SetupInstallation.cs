using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

internal sealed class InstallProblem : Exception
{
    internal InstallProblem(string chinese, string english) : base(SetupText.Get(chinese, english)) { }
}

internal static class SetupInstallation
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + InstallationPaths.PackageId;
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string SetupKey = @"Software\" + InstallationPaths.PackageId + @"\Setup";
    private const string MigrationValue = "PendingMigration";
    private const string Payload = "Curtain.Setup.Velopack";

    internal static string[] KnownRoots()
    {
        var hints = SetupProcess.RunningExecutables().Concat(ShortcutTargets())
            .Select(InstallationPaths.RootFromLauncher)
            .Concat(new[] { ReadRegisteredPath(), InstallationPaths.DefaultRoot });
        return hints.Select(InstallationPaths.VerifiedRoot).Where(root => root != null)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> ShortcutPaths()
    {
        return new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Programs }
            .Select(Environment.GetFolderPath).Where(folder => !string.IsNullOrEmpty(folder))
            .Select(folder => Path.Combine(folder, "Curtain.lnk"));
    }

    private static IEnumerable<string> ShortcutTargets()
    {
        var targets = new List<string>();
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return targets;
        object shell = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            foreach (string file in ShortcutPaths())
            {
                if (!File.Exists(file)) continue;
                object link = null;
                try
                {
                    link = ((dynamic)shell).CreateShortcut(file);
                    targets.Add((string)((dynamic)link).TargetPath);
                }
                catch (COMException) { }
                finally { if (link != null) Marshal.FinalReleaseComObject(link); }
            }
        }
        catch (COMException) { }
        finally { if (shell != null) Marshal.FinalReleaseComObject(shell); }
        return targets;
    }

    internal static string Install(string requested, string version, out string notice)
    {
        notice = null;
        FinishPendingMigration(false);
        string[] knownRoots = KnownRoots();
        string target = InstallationPaths.NormalizeRoot(requested);
        string settings = InstallationPaths.NormalizeRoot(Path.Combine(InstallationPaths.LocalData, "Curtain"));
        InstallationPaths.ValidateDestination(target, null, settings);
        string[] oldToRemove = knownRoots.Where(root => !InstallationPaths.Equal(root, target)).ToArray();
        bool moving = oldToRemove.Length != 0;
        foreach (string previous in oldToRemove) InstallationPaths.ValidateSeparation(target, previous, settings);
        bool targetExisted = Directory.Exists(target);
        bool targetInstalled = InstallationPaths.ReadVersion(target, InstallationPaths.Current, true) != null;
        Trace("Begin " + version + "; destination=" + target + "; retire=" + string.Join(" | ", oldToRemove));
        var snapshot = new InstallationRegistration();
        foreach (string previous in oldToRemove) SetupProcess.CloseApp(previous);
        if (targetInstalled) SetupProcess.CloseApp(target);
        if (moving) WriteMigration(oldToRemove, target);
        try
        {
            if (InstallPayload(target) != 0 ||
                InstallationPaths.ReadVersion(target, InstallationPaths.Current, true) != version ||
                FileVersionInfo.GetVersionInfo(Path.Combine(target, InstallationPaths.Current, InstallationPaths.MainExe))
                    .ProductVersion.Split('+')[0] != version)
                throw new InstallProblem("安装未完成。请检查安装目录和剩余磁盘空间后重试。",
                    "Installation did not complete. Check the selected folder and available disk space, then try again.");
            EnsureRegistration(target, version);
            Trace("Destination payload and uninstall registration verified.");
        }
        catch (Exception error)
        {
            Trace("Install failed before migration: " + error.GetType().Name + " " + error.Message);
            snapshot.Restore();
            if (!targetInstalled) InstallationPaths.DiscardIncompleteInstall(target, targetExisted);
            if (moving) WriteMigration(null, null);
            throw;
        }
        if (moving)
        {
            try { FinishPendingMigration(true); }
            catch (Exception error)
            {
                if (!(error is IOException || error is UnauthorizedAccessException || error is InstallProblem || error is Win32Exception)) throw;
                Trace("Old installation cleanup still pending: " + error.GetType().Name + " " + error.Message);
                notice = SetupText.Get("新位置已安装。旧目录还有文件未清理，再次运行安装包可重试清理。",
                    "Curtain is installed at the new location. Run Setup again to retry removing the remaining files from the old installation.");
            }
        }
        return Path.Combine(target, InstallationPaths.Current, InstallationPaths.MainExe);
    }

    private static void FinishPendingMigration(bool expected)
    {
        string[] pending;
        using (var key = Registry.CurrentUser.OpenSubKey(SetupKey))
            pending = key == null ? null : key.GetValue(MigrationValue) as string[];
        if (pending == null || pending.Length < 2)
        {
            if (expected) throw new IOException("The pending migration record is missing.");
            return;
        }

        string target = InstallationPaths.NormalizeRoot(pending[pending.Length - 1]);
        if (!RegisteredAt(target) ||
            InstallationPaths.ReadVersion(target, InstallationPaths.Current, true) == null)
        {
            if (expected) throw new IOException("The destination registration no longer matches the installed application.");
            return;
        }
        string settings = InstallationPaths.NormalizeRoot(Path.Combine(InstallationPaths.LocalData, "Curtain"));
        var previousRoots = pending.Take(pending.Length - 1).Select(InstallationPaths.NormalizeRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string previous in previousRoots) InstallationPaths.ValidateSeparation(previous, target, settings);
        foreach (string previous in previousRoots)
        {
            UpdateStartupPath(previous, target);
            SetupProcess.CloseApp(previous);
            InstallationPaths.RetirePrevious(previous);
            if (InstallationPaths.HasProgramFiles(previous))
                throw new IOException("The previous Curtain program files are still present: " + previous);
            Trace("Retired " + previous);
        }
        WriteMigration(null, null);
        Trace("Migration completed; no old Curtain program files remain.");
    }

    private static void UpdateStartupPath(string previous, string target)
    {
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true))
        {
            var command = run == null ? null : run.GetValue("Curtain") as string;
            if (string.IsNullOrEmpty(command)) return;
            foreach (string relative in new[] { InstallationPaths.Launcher, Path.Combine(InstallationPaths.Current, InstallationPaths.MainExe) })
            {
                string oldExe = Path.Combine(previous, relative);
                if (!command.StartsWith("\"" + oldExe + "\"", StringComparison.OrdinalIgnoreCase)) continue;
                string newExe = Path.Combine(target, relative);
                if (!File.Exists(newExe)) newExe = Path.Combine(target, InstallationPaths.Current, InstallationPaths.MainExe);
                string updated = "\"" + newExe + "\"" + command.Substring(oldExe.Length + 2);
                run.SetValue("Curtain", updated, run.GetValueKind("Curtain"));
                run.Flush();
                if (!InstallationPaths.Equal(run.GetValue("Curtain") as string, updated))
                    throw new IOException("The Curtain startup entry could not be updated.");
                return;
            }
        }
    }

    private static string ReadRegisteredPath()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
        {
            return key == null ? null : key.GetValue("InstallLocation") as string;
        }
    }

    private static bool RegisteredAt(string root)
    {
        if (!InstallationPaths.Equal(InstallationPaths.VerifiedRoot(ReadRegisteredPath()), root)) return false;
        using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
        {
            var command = key == null ? null : key.GetValue("UninstallString") as string;
            return InstallationPaths.Equal(command, "\"" + Path.Combine(root, InstallationPaths.Updater) + "\" --uninstall");
        }
    }

    private static void EnsureRegistration(string root, string version)
    {
        if (RegisteredAt(root)) return;
        // The payload and its version have already been verified. Repair an old
        // registration before allowing the files it points to to be removed.
        Trace("Repairing uninstall registration; found=" + ReadRegisteredPath());
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            string command = "\"" + Path.Combine(root, InstallationPaths.Updater) + "\" --uninstall";
            key.SetValue("DisplayName", "Curtain");
            key.SetValue("DisplayVersion", version);
            key.SetValue("InstallLocation", root);
            key.SetValue("DisplayIcon", Path.Combine(root, InstallationPaths.Current, InstallationPaths.MainExe));
            key.SetValue("UninstallString", command);
            key.SetValue("QuietUninstallString", command + " --silent");
            key.Flush();
        }
        if (!RegisteredAt(root)) throw new IOException("The Windows uninstall entry could not be updated.");
    }

    private static void Trace(string message)
    {
        try
        {
            string path = Path.Combine(InstallationPaths.LocalData, "velopack");
            Directory.CreateDirectory(path);
            File.AppendAllText(Path.Combine(path, "curtain-setup.log"),
                DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void WriteMigration(string[] previous, string target)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(SetupKey))
        {
            if (previous == null) key.DeleteValue(MigrationValue, false);
            else key.SetValue(MigrationValue, previous.Concat(new[] { target }).ToArray(), RegistryValueKind.MultiString);
        }
    }

    private static int InstallPayload(string target)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "CurtainSetup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        string installer = Path.Combine(scratch, "Setup.exe");
        try
        {
            using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream(Payload))
            using (var output = new FileStream(installer, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (source == null) throw new IOException("The Curtain installation payload is missing.");
                source.CopyTo(output);
            }
            using (var process = Process.Start(new ProcessStartInfo(installer, "--silent --installto \"" + target + "\"")
            {
                UseShellExecute = false,
                WorkingDirectory = scratch
            }))
            {
                process.WaitForExit();
                return process.ExitCode;
            }
        }
        finally
        {
            try { File.Delete(installer); Directory.Delete(scratch); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // The stock installer updates these links before committing its uninstall entry.
    // Restore them together if it fails while the original installation is still usable.
    private sealed class InstallationRegistration
    {
        private readonly List<Tuple<string, object, RegistryValueKind>> values = new List<Tuple<string, object, RegistryValueKind>>();
        private readonly Dictionary<string, byte[]> shortcuts = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private readonly bool existed;

        internal InstallationRegistration()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                existed = key != null;
                if (existed)
                    foreach (string name in key.GetValueNames())
                        values.Add(Tuple.Create(name, key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
                            key.GetValueKind(name)));
            }
            foreach (string path in ShortcutPaths())
            {
                shortcuts[path] = File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
        }

        internal void Restore()
        {
            if (existed)
            {
                using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    foreach (string name in key.GetValueNames().Except(values.Select(value => value.Item1), StringComparer.OrdinalIgnoreCase))
                        key.DeleteValue(name, false);
                    foreach (var value in values) key.SetValue(value.Item1, value.Item2, value.Item3);
                }
            }
            else Registry.CurrentUser.DeleteSubKey(UninstallKey, false);
            foreach (var item in shortcuts)
            {
                if (item.Value == null) File.Delete(item.Key);
                else File.WriteAllBytes(item.Key, item.Value);
            }
        }
    }
}
