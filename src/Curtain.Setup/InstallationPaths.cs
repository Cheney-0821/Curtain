using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;

internal static class InstallationPaths
{
    internal const string PackageId = "CurtainPrivacy";
    internal const string MainExe = "Curtain.App.exe";
    internal const string Launcher = "Curtain.exe";
    internal const string Updater = "Update.exe";
    internal const string Current = "current";
    internal const string Packages = "packages";
    internal const string Retired = ".curtain-previous-current";
    private static readonly string[] ManagedEntries = { Launcher, Updater, Current, Packages };
    internal static readonly string LocalData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    internal static readonly string DefaultRoot = Path.Combine(LocalData, PackageId);

    internal static string NormalizeRoot(string input)
    {
        string raw = (input ?? "").Trim().Replace('/', '\\');
        if (raw.Length < 4 || !char.IsLetter(raw[0]) || raw[1] != ':' || raw[2] != '\\' ||
            raw.Substring(2).IndexOf(':') >= 0 || raw.Contains("\""))
            throw new InstallProblem("请选择本机磁盘上的安装文件夹。", "Choose an installation folder on a local drive.");

        string result = Path.GetFullPath(raw).TrimEnd('\\');
        if (result.Length <= 2 || result.Substring(3).Split('\\').Any(part => part.EndsWith(".") || part.EndsWith(" ")))
            throw new InstallProblem("请选择专用于 Curtain 的文件夹。", "Choose a folder dedicated to Curtain.");
        var drive = new DriveInfo(Path.GetPathRoot(result));
        if (drive.DriveType != DriveType.Fixed)
            throw new InstallProblem("请选择本机固定磁盘上的目录。", "Choose a directory on a local fixed drive.");

        // Resolve existing path components, including short names. A directory alias
        // must never make two paths to the same installation look like a migration.
        string ancestor = result;
        var missing = new Stack<string>();
        while (!Directory.Exists(ancestor))
        {
            if (Exists(ancestor))
                throw new InstallProblem("安装路径已被文件占用。", "A file already occupies the installation path.");
            missing.Push(Path.GetFileName(ancestor));
            ancestor = Path.GetDirectoryName(ancestor);
            if (ancestor == null) throw new IOException("Installation drive is unavailable.");
        }
        for (var parent = new DirectoryInfo(ancestor); parent != null; parent = parent.Parent)
            EnsureOrdinary(parent.FullName);

        var longName = new StringBuilder(32768);
        uint length = GetLongPathName(ancestor, longName, (uint)longName.Capacity);
        if (length == 0 || length >= longName.Capacity) throw new IOException("Could not resolve the installation path.");
        result = longName.ToString();
        foreach (string part in missing) result = Path.Combine(result, part);
        return result.TrimEnd('\\');
    }

    internal static void ValidateDestination(string root, string previous, string settingsDirectory)
    {
        bool same = Equal(root, previous);
        ValidateSeparation(root, same ? null : previous, settingsDirectory);
        if (!Directory.Exists(root)) return;

        var names = Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).ToArray();
        if (names.Length == 0) return;
        if (ReadVersion(root, Current, true) == null ||
            names.Any(name => !ManagedEntries.Contains(name, StringComparer.OrdinalIgnoreCase)))
            throw new InstallProblem("请选择空文件夹，或使用没有其他文件的现有 Curtain 安装目录。",
                "Choose an empty folder, or use an existing Curtain installation folder that contains only app files.");
        foreach (string path in Directory.EnumerateFileSystemEntries(root)) EnsureTreeOrdinary(path);
    }

    internal static bool HasProgramFiles(string root)
    {
        return ManagedEntries.Any(name => Exists(Path.Combine(root, name))) || Exists(Path.Combine(root, Retired));
    }

    internal static string RootFromLauncher(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathRooted(executable)) return null;
        string parent = Path.GetDirectoryName(executable);
        string name = Path.GetFileName(executable);
        if (Equal(name, Launcher)) return parent;
        if (Equal(name, MainExe) && Equal(Path.GetFileName(parent), Current))
            return Path.GetDirectoryName(parent);
        return null;
    }

    internal static string VerifiedRoot(string hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return null;
        try
        {
            string root = NormalizeRoot(hint);
            return ReadVersion(root, Current, true) != null ? root : null;
        }
        catch (InstallProblem) { return null; }
        catch (ArgumentException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (XmlException) { return null; }
    }

    internal static void ValidateSeparation(string root, string otherInstallation, string settingsDirectory)
    {
        if (Overlaps(root, settingsDirectory))
            throw new InstallProblem("安装目录和 Curtain 的设置目录不能重叠。", "The installation folder must be separate from Curtain settings.");
        if (otherInstallation != null && Overlaps(root, otherInstallation))
            throw new InstallProblem("请选择和当前安装目录互不包含的文件夹。", "Choose a folder outside the current installation and its parents.");
    }

    internal static string ReadVersion(string root, string currentName, bool requireExecutables)
    {
        if (root == null) return null;
        string bin = Path.Combine(root, currentName);
        string manifest = Path.Combine(bin, "sq.version");
        if (!File.Exists(manifest)) return null;
        EnsureOrdinary(root);
        EnsureOrdinary(bin);
        EnsureOrdinary(manifest);
        if (requireExecutables)
        {
            foreach (string executable in new[] { Path.Combine(bin, MainExe), Path.Combine(root, Updater) })
            {
                if (!File.Exists(executable)) return null;
                EnsureOrdinary(executable);
            }
        }
        using (var reader = XmlReader.Create(manifest, new XmlReaderSettings { MaxCharactersInDocument = 32768, DtdProcessing = DtdProcessing.Prohibit }))
        {
            XElement package = XDocument.Load(reader).Root;
            if (package == null) return null;
            XElement metadata = package.Element(package.Name.Namespace + "metadata");
            if (metadata == null || (string)metadata.Element(metadata.Name.Namespace + "id") != PackageId ||
                (string)metadata.Element(metadata.Name.Namespace + "mainExe") != MainExe) return null;
            return (string)metadata.Element(metadata.Name.Namespace + "version");
        }
    }

    internal static void RetirePrevious(string previous)
    {
        if (!Directory.Exists(previous)) return;
        EnsureOrdinary(previous);
        string current = Path.Combine(previous, Current);
        string retired = Path.Combine(previous, Retired);
        bool hasCurrent = Directory.Exists(current);
        bool hasRetired = Directory.Exists(retired);
        if (hasCurrent)
        {
            if (hasRetired || ReadVersion(previous, Current, true) == null)
                throw new IOException("The old Curtain installation could not be verified.");
            EnsureTreeOrdinary(current);
            Directory.Move(current, retired);
            hasRetired = true;
        }
        if (!hasRetired) return;
        if (ReadVersion(previous, Retired, false) == null)
        {
            if (Directory.EnumerateFileSystemEntries(retired).Any())
                throw new IOException("The retained Curtain migration folder could not be verified.");
            Directory.Delete(retired);
        }
        else
        {
            foreach (string name in new[] { Launcher, Updater })
            {
                string path = Path.Combine(previous, name);
                if (Exists(path)) { EnsureOrdinary(path); File.Delete(path); }
            }
            DeleteTree(Path.Combine(previous, Packages));
            // Keep the manifest as an ownership marker until all other entries are gone.
            EnsureOrdinary(retired);
            foreach (string entry in Directory.EnumerateFileSystemEntries(retired))
                if (!Equal(Path.GetFileName(entry), "sq.version")) DeleteTree(entry);
            File.Delete(Path.Combine(retired, "sq.version"));
            Directory.Delete(retired);
        }
        if (!Directory.EnumerateFileSystemEntries(previous).Any()) Directory.Delete(previous);
    }

    internal static void DiscardIncompleteInstall(string root, bool directoryExisted)
    {
        if (!Directory.Exists(root)) return;
        if (!Equal(NormalizeRoot(root), root)) throw new IOException("The failed installation path has changed.");
        foreach (string name in ManagedEntries) DeleteTree(Path.Combine(root, name));
        if (!directoryExisted && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
    }

    private static void EnsureTreeOrdinary(string path)
    {
        FileAttributes attrs = EnsureOrdinary(path);
        if ((attrs & FileAttributes.Directory) != 0)
            foreach (string entry in Directory.EnumerateFileSystemEntries(path)) EnsureTreeOrdinary(entry);
    }

    private static void DeleteTree(string path)
    {
        if (!Exists(path)) return;
        if ((EnsureOrdinary(path) & FileAttributes.Directory) == 0) { File.Delete(path); return; }
        foreach (string entry in Directory.GetFileSystemEntries(path)) DeleteTree(entry);
        Directory.Delete(path);
    }

    private static FileAttributes EnsureOrdinary(string path)
    {
        FileAttributes attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.ReparsePoint) != 0)
            throw new InstallProblem("安装目录包含目录链接，请选择普通文件夹。", "The installation path contains a link. Choose a regular folder.");
        return attrs;
    }

    private static bool Exists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static bool Equal(string left, string right) { return StringComparer.OrdinalIgnoreCase.Equals(left, right); }
    private static bool Overlaps(string left, string right)
    {
        return Equal(left, right) || left.StartsWith(right + "\\", StringComparison.OrdinalIgnoreCase) ||
            right.StartsWith(left + "\\", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint length);
}
