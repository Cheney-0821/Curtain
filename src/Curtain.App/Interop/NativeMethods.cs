using System.Runtime.InteropServices;
using System.Text;
using Curtain.App.Localization;
using Curtain.Core.Geometry;

namespace Curtain.App.Interop;

internal static class NativeMethods
{
    public const int GA_ROOT = 2;
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int GWLP_HWNDPARENT = -8;
    public const int GW_OWNER = 4;
    public const long WS_CAPTION = 0x00C00000L;
    public const long WS_THICKFRAME = 0x00040000L;
    public const long WS_MINIMIZEBOX = 0x00020000L;
    public const long WS_MAXIMIZEBOX = 0x00010000L;
    public const long WS_SYSMENU = 0x00080000L;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_APPWINDOW = 0x00040000L;
    public const long WS_EX_NOACTIVATE = 0x08000000L;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const int SW_HIDE = 0;
    public const int SW_RESTORE = 9;
    public const uint WM_SIZE = 0x0005;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_APP = 0x8000;
    public const uint SIZE_MINIMIZED = 1;
    public const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWCP_ROUND = 2;
    public const int DWMWCP_ROUNDSMALL = 3;
    public const int GW_HWNDPREV = 3;
    public const uint WINEVENT_OUTOFCONTEXT = 0;
    public const uint WINEVENT_SKIPOWNPROCESS = 2;
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;

    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect
    {
        public int Left, Top, Right, Bottom;
        public PixelRect ToPixelRect() => new(Left, Top, Right, Bottom);
    }

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] public static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsHungAppWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder name, int count);
    [DllImport("user32.dll")] public static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo
    { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    public static (PixelRect Work, PixelRect Monitor) GetMonitorBounds(nint hwnd, PixelRect fallback)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfoW(MonitorFromWindow(hwnd, 2), ref info)
            ? (info.Work.ToPixelRect(), info.Monitor.ToPixelRect()) : (fallback, fallback);
    }
    public static PixelRect GetWorkAreaAt(PixelPoint point, PixelRect fallback)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfoW(MonitorFromPoint(new Point { X = point.X, Y = point.Y }, 2), ref info)
            ? info.Work.ToPixelRect() : fallback;
    }
    public static string ClassOf(nint hwnd)
    {
        var name = new StringBuilder(256);
        GetClassNameW(hwnd, name, name.Capacity);
        return name.ToString();
    }
    public static bool IsSelectableWindow(nint hwnd)
    {
        if (!IsExternalWindow(hwnd) || IsIconic(hwnd) || !IsWindowEnabled(hwnd) || IsHungAppWindow(hwnd) ||
            GetAncestor(hwnd, GA_ROOT) != hwnd || hwnd == GetShellWindow() || hwnd == GetDesktopWindow()) return false;
        string cls = ClassOf(hwnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" ||
            cls.Contains("IME", StringComparison.OrdinalIgnoreCase)) return false;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        return (ex & (WS_EX_TOOLWINDOW | 8)) == 0 && GetVisibleBounds(hwnd) is not null;
    }
    [DllImport("user32.dll")] public static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(nint hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hwnd, out Rect bounds);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect bounds, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] public static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessageW(string message);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint BeginDeferWindowPos(int count);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint DeferWindowPos(nint batch, nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EndDeferWindowPos(nint batch);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] public static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint subclassId, nuint referenceData);
    [DllImport("comctl32.dll", SetLastError = true)] public static extern bool SetWindowSubclass(nint hwnd, SubclassProc procedure, nuint subclassId, nuint referenceData);
    [DllImport("comctl32.dll", SetLastError = true)] public static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc procedure, nuint subclassId);
    [DllImport("comctl32.dll")] public static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint thread, uint time);

    [DllImport("user32.dll")] public static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module,
        WinEventProc callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(nint hook);

    public static nint PointedRootWindow()
    {
        if (!GetCursorPos(out var p)) return nint.Zero;
        nint child = WindowFromPoint(p);
        return child == nint.Zero ? nint.Zero : GetAncestor(child, GA_ROOT);
    }

    public static bool IsExternalWindow(nint hwnd)
    {
        if (hwnd == nint.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid != (uint)Environment.ProcessId && pid != 0;
    }

    public static string TitleOf(nint hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.Length == 0
            ? LocalizationService.Get("WindowHandleFallback", hwnd.ToInt64().ToString("X"))
            : sb.ToString();
    }

    public static PixelRect? GetVisibleBounds(nint hwnd)
    {
        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return null;
        var result = DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var bounds, Marshal.SizeOf<Rect>());
        if (result != 0 && !GetWindowRect(hwnd, out bounds)) return null;
        var rect = bounds.ToPixelRect();
        return rect.IsValid ? rect : null;
    }

    public static void RemoveWindowChrome(nint hwnd)
    {
        long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
        SetWindowLongPtr(hwnd, GWL_STYLE, (nint)style);
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        ex &= ~WS_EX_APPWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)ex);
        SetWindowPos(hwnd, nint.Zero, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_FRAMECHANGED | 0x0001 | 0x0002 | 0x0004);
    }

    public static bool SetOverlayOwner(nint overlay, nint target)
    {
        // Change only our top-level overlay, never the external target's parent/style.
        SetWindowLongPtr(overlay, GWLP_HWNDPARENT, target);
        return GetWindow(overlay, GW_OWNER) == target;
    }

    public static bool MoveProtectedWindows(nint target, PixelRect rawTarget, nint curtain,
        PixelRect visibleTarget, nint pin, PixelRect pinBounds)
    {
        // All three are top-level windows with the desktop as parent. Ownership is separate.
        nint batch = BeginDeferWindowPos(3);
        if (batch == 0) return false;
        uint overlayFlags = SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW;
        batch = DeferWindowPos(batch, curtain, 0, visibleTarget.Left, visibleTarget.Top,
            visibleTarget.Width, visibleTarget.Height, overlayFlags);
        if (batch == 0) return false; // Failed DeferWindowPos must not be ended.
        batch = DeferWindowPos(batch, pin, 0, pinBounds.Left, pinBounds.Top,
            pinBounds.Width, pinBounds.Height, overlayFlags);
        if (batch == 0) return false;
        batch = DeferWindowPos(batch, target, 0, rawTarget.Left, rawTarget.Top, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        return batch != 0 && EndDeferWindowPos(batch);
    }

    /// <summary>
    /// Win11 DWM corner preference is a hint, not a hard crop. Win10 keeps its
    /// native square shape; any unsupported HRESULT is non-fatal.
    /// </summary>
    public static bool TrySetCornerPreference(nint hwnd, int preference)
    {
        if (Environment.OSVersion.Version.Build < 22000) return false;
        int hr = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE,
            ref preference, sizeof(int));
        if (hr != 0)
            System.Diagnostics.Debug.WriteLine($"Curtain DWM corner hint failed: 0x{hr:X8}");
        return hr == 0;
    }

    /// <summary>
    /// Update geometry independently of relative Z-order. The previous combined call
    /// could fail to move the overlay when the preceding HWND was the overlay itself.
    /// </summary>
    public static bool PositionAbove(nint window, nint anchor, PixelRect bounds, bool showWindow = true)
    {
        if (window == nint.Zero || anchor == nint.Zero || window == anchor ||
            !IsWindow(window) || !IsWindow(anchor) || !bounds.IsValid)
            return false;

        // Geometry must still follow the target even when a Z-order adjustment fails.
        uint geometryFlags = SWP_NOZORDER | SWP_NOACTIVATE;
        if (showWindow) geometryFlags |= SWP_SHOWWINDOW;
        if (!SetWindowPos(window, nint.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                geometryFlags))
        {
            System.Diagnostics.Debug.WriteLine(
                $"Curtain geometry SetWindowPos failed: {Marshal.GetLastPInvokeError()}");
            return false;
        }

        nint prev = GetWindow(anchor, GW_HWNDPREV);
        // An already-adjacent overlay can be the anchor's immediately preceding window.
        // Never insert a HWND directly after itself.
        for (int i = 0; prev == window && i < 4; i++)
            prev = GetWindow(prev, GW_HWNDPREV);
        if (prev == window) return false;

        bool reordered = SetWindowPos(window, prev, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        if (!reordered)
            System.Diagnostics.Debug.WriteLine(
                $"Curtain Z-order SetWindowPos failed: {Marshal.GetLastPInvokeError()}");
        return reordered;
    }
}
