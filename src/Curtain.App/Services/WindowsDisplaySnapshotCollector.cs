using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Curtain.App.Services;

internal sealed record DisplayMonitorSnapshot(
    string DeviceName,
    int Left,
    int Top,
    int Right,
    int Bottom,
    int WorkLeft,
    int WorkTop,
    int WorkRight,
    int WorkBottom,
    uint? DpiX,
    uint? DpiY,
    bool IsPrimary);

internal sealed record DisplayAdapterSnapshot(
    string DeviceName,
    string Description,
    string? DriverProvider,
    string? DriverVersion,
    uint StateFlags,
    bool IsAttachedToDesktop,
    bool IsPrimary);

internal sealed record WindowsDisplaySnapshot(
    IReadOnlyList<DisplayMonitorSnapshot> Monitors,
    IReadOnlyList<DisplayAdapterSnapshot> Adapters,
    string CollectionStatus);

/// <summary>Collects monitor geometry/DPI and display adapter names without reading EDID or device IDs.</summary>
internal static class WindowsDisplaySnapshotCollector
{
    private const uint MonitorInfoPrimary = 0x00000001;
    private const uint DisplayDeviceAttachedToDesktop = 0x00000001;
    private const uint DisplayDevicePrimaryDevice = 0x00000004;
    private const int MonitorDpiTypeEffective = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clipRect, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfoEx info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "EnumDisplayDevicesW")]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice displayDevice, uint flags);

    internal static WindowsDisplaySnapshot Collect()
    {
        var monitors = new List<DisplayMonitorSnapshot>();
        var adapters = new List<DisplayAdapterSnapshot>();
        try
        {
            MonitorEnumProc callback = (monitor, _, _, _) =>
            {
                var info = new MonitorInfoEx
                {
                    Size = (uint)Marshal.SizeOf<MonitorInfoEx>(),
                    DeviceName = string.Empty
                };
                if (!GetMonitorInfoW(monitor, ref info)) return true;

                uint? dpiX = null;
                uint? dpiY = null;
                try
                {
                    if (GetDpiForMonitor(monitor, MonitorDpiTypeEffective, out uint x, out uint y) == 0)
                    {
                        dpiX = x;
                        dpiY = y;
                    }
                }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }

                monitors.Add(new DisplayMonitorSnapshot(
                    info.DeviceName ?? string.Empty,
                    info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom,
                    info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom,
                    dpiX, dpiY, (info.Flags & MonitorInfoPrimary) != 0));
                return true;
            };

            if (!EnumDisplayMonitors(nint.Zero, nint.Zero, callback, nint.Zero))
                return new(monitors, adapters, "MonitorEnumerationFailed");

            for (uint index = 0; index < 32; index++)
            {
                var device = new DisplayDevice
                {
                    Size = (uint)Marshal.SizeOf<DisplayDevice>(),
                    DeviceName = string.Empty,
                    DeviceString = string.Empty,
                    DeviceId = string.Empty,
                    DeviceKey = string.Empty
                };
                if (!EnumDisplayDevices(null, index, ref device, 0)) break;
                (string? provider, string? version) = ReadDriverMetadata(device.DeviceKey);
                adapters.Add(new DisplayAdapterSnapshot(
                    device.DeviceName ?? string.Empty,
                    device.DeviceString ?? string.Empty,
                    provider,
                    version,
                    device.StateFlags,
                    (device.StateFlags & DisplayDeviceAttachedToDesktop) != 0,
                    (device.StateFlags & DisplayDevicePrimaryDevice) != 0));
            }

            return new(monitors, adapters, "Success");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or
                                   MarshalDirectiveException or System.ComponentModel.Win32Exception)
        {
            return new(monitors, adapters, ex.GetType().Name);
        }
    }

    private static (string? Provider, string? Version) ReadDriverMetadata(string? deviceKey)
    {
        const string machinePrefix = @"\Registry\Machine\";
        if (string.IsNullOrWhiteSpace(deviceKey) ||
            !deviceKey.StartsWith(machinePrefix, StringComparison.OrdinalIgnoreCase)) return (null, null);

        string registryPath = deviceKey[machinePrefix.Length..];
        if (!registryPath.StartsWith(@"SYSTEM\CurrentControlSet\Control\Video\", StringComparison.OrdinalIgnoreCase) ||
            registryPath.Contains("..", StringComparison.Ordinal)) return (null, null);
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(registryPath, writable: false);
            return (SafeDriverValue(key?.GetValue("ProviderName") as string),
                SafeDriverValue(key?.GetValue("DriverVersion") as string));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (null, null);
        }
    }

    private static string? SafeDriverValue(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl) ? null : value;
}
