using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Curtain.App.Interop;

internal readonly record struct ScreenRect(nint Monitor, int Left, int Top, int Width, int Height);

/// <summary>Session-local idle input and physical multi-monitor bounds.</summary>
internal static class PrivacyNative
{
    private const int HwndTopmost = -1;
    private const uint SwpNoActivate = 0x0010, SwpShowWindow = 0x0040;

    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo
    {
        public uint Size;
        public uint Tick;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo
    {
        public uint Size;
        public NativeMethods.Rect Monitor;
        public NativeMethods.Rect Work;
        public uint Flags;
    }
    private delegate bool MonitorProc(nint monitor, nint hdc, nint rect, nint data);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clipRect, MonitorProc callback, nint data);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    internal static TimeSpan? GetIdleDuration()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return null;
        // Both values use the same 32-bit Windows tick base; unsigned subtraction handles wraparound.
        uint elapsed = unchecked((uint)Environment.TickCount - info.Tick);
        return TimeSpan.FromMilliseconds(elapsed);
    }

    internal static IReadOnlyList<ScreenRect> Displays()
    {
        var found = new List<ScreenRect>();
        MonitorProc callback = (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfoW(monitor, ref info))
            {
                int width = info.Monitor.Right - info.Monitor.Left;
                int height = info.Monitor.Bottom - info.Monitor.Top;
                if (width > 0 && height > 0)
                    found.Add(new ScreenRect(monitor, info.Monitor.Left, info.Monitor.Top, width, height));
            }
            return true;
        };
        if (!EnumDisplayMonitors(0, 0, callback, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法枚举显示器。");
        if (found.Count == 0) throw new InvalidOperationException("没有可用显示器。");
        return found;
    }

    internal static void PositionFullScreen(nint hwnd, ScreenRect area)
    {
        if (!NativeMethods.SetWindowPos(hwnd, (nint)HwndTopmost, area.Left, area.Top,
                area.Width, area.Height, SwpNoActivate | SwpShowWindow))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法将隐私遮罩覆盖屏幕。");
    }
}
