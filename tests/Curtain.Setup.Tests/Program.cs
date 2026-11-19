using System;
using System.IO;

internal static class SetupTests
{
    private static void Main(string[] args)
    {
        string workspace = InstallationPaths.NormalizeRoot(args[0]);
        string root = Path.Combine(workspace, "setup-path-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string old = Path.Combine(root, "formerly installed");
            string target = Path.Combine(root, "Curtain 新目录");
            string settings = Path.Combine(root, "settings");
            Directory.CreateDirectory(settings);
            File.WriteAllText(Path.Combine(settings, "settings.json"), "keep settings");
            CreateInstallation(old, "0.3.1");
            Equal("0.3.1", InstallationPaths.ReadVersion(old, InstallationPaths.Current, true));
            Equal(old, InstallationPaths.RootFromLauncher(Path.Combine(old, InstallationPaths.Current, InstallationPaths.MainExe)));
            Equal(old, InstallationPaths.RootFromLauncher(Path.Combine(old, InstallationPaths.Launcher)));
            Equal(old, InstallationPaths.VerifiedRoot(old));
            Equal(target, InstallationPaths.NormalizeRoot(target));
            InstallationPaths.ValidateDestination(old, old, settings);
            InstallationPaths.ValidateDestination(target, old, settings);
            Reject(() => InstallationPaths.ValidateDestination(Path.Combine(old, "nested"), old, settings));
            Reject(() => InstallationPaths.ValidateDestination(root, old, settings));
            Reject(() => InstallationPaths.ValidateDestination(settings, old, settings));
            Reject(() => InstallationPaths.NormalizeRoot(Path.GetPathRoot(root)));
            Reject(() => InstallationPaths.NormalizeRoot(@"relative\path"));

            string userFile = Path.Combine(old, "personal.txt");
            File.WriteAllText(userFile, "keep personal file");
            Reject(() => InstallationPaths.ValidateDestination(old, old, settings));
            CreateInstallation(target, "0.4.0");
            InstallationPaths.ValidateDestination(target, old, settings);

            string lockedPackage = Path.Combine(old, InstallationPaths.Packages, "old.nupkg");
            using (File.Open(lockedPackage, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try
                {
                    InstallationPaths.RetirePrevious(old);
                    throw new Exception("Expected a locked file to interrupt retirement.");
                }
                catch (IOException) { }
                Equal(null, InstallationPaths.ReadVersion(old, InstallationPaths.Current, true));
                Equal("0.3.1", InstallationPaths.ReadVersion(old, InstallationPaths.Retired, false));
            }
            InstallationPaths.RetirePrevious(old);
            InstallationPaths.RetirePrevious(old);
            Equal("keep personal file", File.ReadAllText(userFile));
            Equal("keep settings", File.ReadAllText(Path.Combine(settings, "settings.json")));
            Equal("0.4.0", InstallationPaths.ReadVersion(target, InstallationPaths.Current, true));
            if (Directory.GetFileSystemEntries(old).Length != 1) throw new Exception("Old program files remain.");

            string unrelated = Path.Combine(root, "unrelated");
            CreateInstallation(unrelated, "0.3.1");
            string manifest = Path.Combine(unrelated, InstallationPaths.Current, "sq.version");
            File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("CurtainPrivacy", "OtherApplication"));
            Equal(null, InstallationPaths.VerifiedRoot(unrelated));
            Reject(() => InstallationPaths.ValidateDestination(unrelated, old, settings));
            try
            {
                InstallationPaths.RetirePrevious(unrelated);
                throw new Exception("Expected an unrelated installation to be rejected.");
            }
            catch (IOException) { }
            if (!File.Exists(manifest)) throw new Exception("Unrelated files were modified.");

            string incomplete = Path.Combine(root, "failed installation");
            Directory.CreateDirectory(Path.Combine(incomplete, InstallationPaths.Current));
            File.WriteAllText(Path.Combine(incomplete, InstallationPaths.Current, InstallationPaths.MainExe), "incomplete");
            File.WriteAllText(Path.Combine(incomplete, "another-file.txt"), "keep");
            InstallationPaths.DiscardIncompleteInstall(incomplete, true);
            if (Directory.GetFileSystemEntries(incomplete).Length != 1 ||
                !File.Exists(Path.Combine(incomplete, "another-file.txt")))
                throw new Exception("Failed-install cleanup did not preserve unrelated files.");
            Console.WriteLine("PASS: path selection, ownership checks, locked-file retry, failed-install cleanup, and user-file preservation.");
        }
        finally
        {
            if (InstallationPaths.Equal(Path.GetDirectoryName(root), workspace))
                Directory.Delete(root, true);
        }
    }

    private static void CreateInstallation(string root, string version)
    {
        string current = Path.Combine(root, InstallationPaths.Current);
        string packages = Path.Combine(root, InstallationPaths.Packages);
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(packages);
        File.WriteAllText(Path.Combine(current, "sq.version"),
            "<package xmlns=\"http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd\"><metadata>" +
            "<id>CurtainPrivacy</id><mainExe>Curtain.App.exe</mainExe><version>" + version + "</version></metadata></package>");
        File.WriteAllText(Path.Combine(current, InstallationPaths.MainExe), "app");
        File.WriteAllText(Path.Combine(root, InstallationPaths.Launcher), "launcher");
        File.WriteAllText(Path.Combine(root, InstallationPaths.Updater), "updater");
        File.WriteAllText(Path.Combine(packages, "old.nupkg"), "update payload");
    }

    private static void Equal(string expected, string actual)
    {
        if (expected != actual) throw new Exception("Unexpected result: " + actual);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InstallProblem) { return; }
        throw new Exception("The unsafe destination was accepted.");
    }
}
