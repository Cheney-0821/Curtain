using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Curtain.App.Interop;

namespace Curtain.App.Services;

internal static class WindowMetadataDiagnostics
{
    private delegate bool EnumProc(nint hwnd, nint param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    private static string? _last;

    // A development-only, time-limited request; no titles, input, clipboard or screenshots.
    [Conditional("DEBUG")]
    internal static void Poll(nint target)
    {
        string request = Path.Combine(DevelopmentDiagnostics.DirectoryPath, "window-metadata-until.txt");
        if (!File.Exists(request)) return;
        try
        {
            if (!long.TryParse(File.ReadAllText(request), out long until) ||
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > until) return;
            var windows = new List<object>();
            int index = 0;
            EnumWindows((hwnd, _) =>
            {
                int z = index++;
                if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                var cls = new StringBuilder(256);
                GetClassNameW(hwnd, cls, cls.Capacity);
                uint thread = NativeMethods.GetWindowThreadProcessId(hwnd, out uint process);
                string processName;
                try { processName = Process.GetProcessById((int)process).ProcessName; }
                catch (ArgumentException) { processName = "exited"; }
                if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return true;
                windows.Add(new { hwnd = hwnd.ToInt64(), @class = cls.ToString(), process,
                    processName, thread, owner = NativeMethods.GetWindow(hwnd, 4).ToInt64(), z,
                    exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64(),
                    bounds = new { rect.Left, rect.Top, rect.Right, rect.Bottom } });
                return true;
            }, 0);
            var data = new { target = target.ToInt64(), foreground = GetForegroundWindow().ToInt64(), windows };
            string current = JsonSerializer.Serialize(data);
            if (current != _last) { _last = current; DevelopmentDiagnostics.Write("window-metadata", data); }
        }
        catch (IOException ex) { Debug.WriteLine(ex.Message); }
        catch (UnauthorizedAccessException ex) { Debug.WriteLine(ex.Message); }
    }
}
