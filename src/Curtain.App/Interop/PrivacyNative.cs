using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Curtain.App.Interop;

internal readonly record struct ScreenRect(nint Monitor, int Left, int Top, int Width, int Height,
    bool IsPrimary = false, string? DeviceId = null, string? DeviceName = null, int Number = 0);

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
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    {
        public uint Size;
        public NativeMethods.Rect Monitor;
        public NativeMethods.Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }
    private delegate bool MonitorProc(nint monitor, nint hdc, nint rect, nint data);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clipRect, MonitorProc callback, nint data);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool EnumDisplayDevicesW(string device, uint index, ref DisplayDevice info, uint flags);

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
                {
                    var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
                    string? id = null, name = null;
                    // A cloned desktop can map several physical panels to one HMONITOR.
                    // Store the interface identity only for an individually addressable display.
                    int active = 0;
                    for (uint index = 0; EnumDisplayDevicesW(info.Device, index, ref device, 1); index++)
                    {
                        if ((device.Flags & 1) == 0) continue;
                        active++;
                        id = device.Id;
                        name = device.Description;
                    }
                    int marker = info.Device.LastIndexOf("DISPLAY", StringComparison.Ordinal);
                    int number = marker >= 0 && int.TryParse(info.Device.AsSpan(marker + 7), out int value) ? value : 0;
                    found.Add(new ScreenRect(monitor, info.Monitor.Left, info.Monitor.Top, width, height,
                        (info.Flags & 1) != 0, active == 1 && !string.IsNullOrWhiteSpace(id) ? id : null, name, number));
                }
            }
            return true;
        };
        if (!EnumDisplayMonitors(0, 0, callback, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法枚举显示器。");
        if (found.Count == 0) throw new InvalidOperationException("没有可用显示器。");
        return found;
    }

    internal static void PositionFullScreen(nint hwnd, ScreenRect area, bool showWindow = true)
    {
        uint flags = SwpNoActivate;
        if (showWindow) flags |= SwpShowWindow;
        if (!NativeMethods.SetWindowPos(hwnd, (nint)HwndTopmost, area.Left, area.Top,
                area.Width, area.Height, flags))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法将隐私遮罩覆盖屏幕。");
    }

    internal static nint FindFullScreenOccluder(nint overlay, ScreenRect area, nint restoreControl = 0)
    {
        // Topmost is a group, not a permanent ordering within that group.
        nint window = NativeMethods.GetWindow(overlay, NativeMethods.GW_HWNDPREV);
        for (int count = 0; window != 0 && count < 512; count++)
        {
            if (window != restoreControl && NativeMethods.IsWindowVisible(window) && !NativeMethods.IsIconic(window) &&
                NativeMethods.GetWindowRect(window, out var bounds) &&
                bounds.Right - bounds.Left > 1 && bounds.Bottom - bounds.Top > 1 &&
                bounds.Right > area.Left && bounds.Left < area.Left + area.Width &&
                bounds.Bottom > area.Top && bounds.Top < area.Top + area.Height)
                return window; // Ignore the shell's invisible-content 1x1 helper HWNDs.
            window = NativeMethods.GetWindow(window, NativeMethods.GW_HWNDPREV);
        }
        return 0;
    }

    internal static bool RaiseFullScreen(nint overlay) => NativeMethods.SetWindowPos(overlay,
        (nint)HwndTopmost, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
        NativeMethods.SWP_NOACTIVATE | 0x0200); // SWP_NOOWNERZORDER; never show a hidden HWND.
}
