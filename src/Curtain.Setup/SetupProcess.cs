using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

internal static class SetupProcess
{
    internal static IEnumerable<string> RunningExecutables()
    {
        var paths = new List<string>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(InstallationPaths.MainExe)))
        using (process)
        {
            try { paths.Add(process.MainModule.FileName); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }
        return paths;
    }
    internal static void CloseApp(string root)
    {
        string path = Path.Combine(root, InstallationPaths.Current, InstallationPaths.MainExe);
        var targets = new List<UniqueProcess>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(InstallationPaths.MainExe)))
        using (process)
        {
            try
            {
                if (!InstallationPaths.Equal(process.MainModule.FileName, path)) continue;
                long started = process.StartTime.ToFileTimeUtc();
                targets.Add(new UniqueProcess
                {
                    ProcessId = process.Id,
                    Started = new FILETIME { dwLowDateTime = unchecked((int)started), dwHighDateTime = (int)(started >> 32) }
                });
            }
            catch (InvalidOperationException) { }
        }
        if (targets.Count == 0) return;
        try
        {
            uint session;
            Check(RmStartSession(out session, 0, new StringBuilder(33)));
            try
            {
                Check(RmRegisterResources(session, 0, null, (uint)targets.Count, targets.ToArray(), 0, null));
                Check(RmShutdown(session, 0, IntPtr.Zero));
            }
            finally { RmEndSession(session); }
        }
        catch (Win32Exception)
        {
            throw new InstallProblem("请从托盘退出正在运行的 Curtain 后重试。", "Exit the running copy of Curtain from the system tray, then try again.");
        }
    }

    private static void Check(int result)
    {
        if (result != 0) throw new Win32Exception(result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess
    {
        internal int ProcessId;
        internal FILETIME Started;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint fileCount, string[] files,
        uint processCount, UniqueProcess[] processes, uint serviceCount, string[] services);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmShutdown(uint session, uint flags, IntPtr callback);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);
}
