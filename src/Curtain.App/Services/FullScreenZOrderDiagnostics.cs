using System.Diagnostics;
#if DEBUG
using System.Runtime.InteropServices;
using System.Text.Json;
using Curtain.App.Interop;
#endif

namespace Curtain.App.Services;

internal static class FullScreenZOrderDiagnostics
{
#if DEBUG
    private static readonly Dictionary<nint, string> LastSamples = new();
    private delegate bool EnumWindowProc(nint hwnd, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, nint data);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(
        nint hwnd, out uint color, out byte alpha, out uint flags);

    private static object Describe(nint hwnd)
    {
        uint thread = NativeMethods.GetWindowThreadProcessId(hwnd, out uint process);
        NativeMethods.GetWindowRect(hwnd, out var bounds);
        bool layered = GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out uint flags);
        return new { hwnd = hwnd.ToInt64(), thread, process, className = NativeMethods.ClassOf(hwnd),
            visible = NativeMethods.IsWindowVisible(hwnd),
            topmost = (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64() & 8) != 0,
            owner = NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER).ToInt64(),
            previous = NativeMethods.GetWindow(hwnd, 3).ToInt64(),
            next = NativeMethods.GetWindow(hwnd, 2).ToInt64(),
            bounds = new { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom },
            alpha = layered && (flags & 2) != 0 ? (byte?)alpha : null };
    }
#endif

    [Conditional("DEBUG")]
    internal static void Sample(nint overlay, int left, int top, int width, int height)
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("CURTAIN_FULLSCREEN_DIAGNOSTICS") != "1") return;
        try
        {
            var windows = new List<nint>();
            EnumWindows((hwnd, _) => { windows.Add(hwnd); return windows.Count < 512; }, 0);
            int overlayIndex = windows.IndexOf(overlay);
            var above = windows.Take(Math.Max(0, overlayIndex)).Where(hwnd =>
                NativeMethods.IsWindowVisible(hwnd) && NativeMethods.GetWindowRect(hwnd, out var r) &&
                r.Right > left && r.Left < left + width && r.Bottom > top && r.Top < top + height)
                .Select(Describe).ToArray();
            var taskbars = windows.Where(hwnd => NativeMethods.ClassOf(hwnd) is
                "Shell_TrayWnd" or "Shell_SecondaryTrayWnd").Select(Describe).ToArray();
            string sample = JsonSerializer.Serialize(new { overlay = Describe(overlay),
                monitor = new { left, top, width, height }, overlayIndex,
                foreground = NativeMethods.GetForegroundWindow().ToInt64(), above, taskbars });
            if (LastSamples.TryGetValue(overlay, out string? last) && last == sample) return;
            LastSamples[overlay] = sample;
            string directory = Environment.GetEnvironmentVariable("CURTAIN_FULLSCREEN_DIAGNOSTICS_DIR")
                ?? DevelopmentDiagnostics.DirectoryPath;
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, $"fullscreen-{Environment.ProcessId}.jsonl"),
                $"{{\"time\":\"{DateTimeOffset.Now:O}\",\"sample\":{sample}}}" + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Debug.WriteLine(ex.Message); }
#endif
    }

    [Conditional("DEBUG")]
    internal static void Reset()
    {
#if DEBUG
        LastSamples.Clear();
#endif
    }
}
