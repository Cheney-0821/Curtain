using System.Runtime.InteropServices;

namespace Curtain.App.Interop;

/// <summary>
/// Explains the current beta.5.5 selection gates without changing their behavior.
/// Topmost and hung-window checks remain product decisions; this class only makes
/// each rejection observable so evidence can guide the next change.
/// </summary>
internal static class WindowSelectionEvaluator
{
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExTopmost = 0x00000008L;

    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();

    internal static WindowSelectionResult Evaluate(nint hwnd)
    {
        if (hwnd == nint.Zero) return WindowSelectionResult.Reject("NullHandle");
        if (!NativeMethods.IsWindow(hwnd)) return WindowSelectionResult.Reject("WindowInvalid");
        if (!NativeMethods.IsWindowVisible(hwnd)) return WindowSelectionResult.Reject("WindowNotVisible");

        uint threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
        if (threadId == 0 || processId == 0 || processId == (uint)Environment.ProcessId)
            return WindowSelectionResult.Reject("WindowNotExternal");
        if (NativeMethods.IsIconic(hwnd)) return WindowSelectionResult.Reject("WindowMinimized");
        if (!NativeMethods.IsWindowEnabled(hwnd)) return WindowSelectionResult.Reject("WindowDisabled");
        if (NativeMethods.IsHungAppWindow(hwnd)) return WindowSelectionResult.Reject("WindowHung");
        if (NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) != hwnd)
            return WindowSelectionResult.Reject("WindowNotRoot");
        if (hwnd == GetShellWindow() || hwnd == GetDesktopWindow())
            return WindowSelectionResult.Reject("ShellOrDesktopWindow");

        string className = NativeMethods.ClassOf(hwnd);
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return WindowSelectionResult.Reject("SystemWindowClass");
        if (className.Contains("IME", StringComparison.OrdinalIgnoreCase))
            return WindowSelectionResult.Reject("ImeWindowClass");

        long extendedStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((extendedStyle & WsExToolWindow) != 0)
            return WindowSelectionResult.Reject("ToolWindowUnsupported");
        if ((extendedStyle & WsExTopmost) != 0)
            return WindowSelectionResult.Reject("TopmostUnsupported");

        int dwmResult = NativeMethods.DwmGetWindowAttribute(hwnd,
            NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out NativeMethods.Rect bounds,
            Marshal.SizeOf<NativeMethods.Rect>());
        if (dwmResult != 0 && !NativeMethods.GetWindowRect(hwnd, out bounds))
            return WindowSelectionResult.Reject("WindowBoundsUnavailable", 0, dwmResult);

        if (!bounds.ToPixelRect().IsValid)
            return WindowSelectionResult.Reject("WindowBoundsInvalid", 0, dwmResult);

        return WindowSelectionResult.Accept();
    }
}
