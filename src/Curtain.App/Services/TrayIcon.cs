using System.ComponentModel;
using System.Runtime.InteropServices;
using Curtain.App.Interop;

namespace Curtain.App.Services;

internal sealed class TrayIcon : IDisposable
{
    private const uint NimAdd = 0, NimDelete = 2;
    private const uint NifMessage = 1, NifIcon = 2, NifTip = 4;
    private const uint WmNull = 0, WmLButtonUp = 0x0202, WmRButtonUp = 0x0205;
    private const uint WmAppTray = NativeMethods.WM_APP + 0x51;
    private const uint WmCommand = 0x0111;
    private const uint TpmRightButton = 0x0002, TpmReturnCommand = 0x0100, TpmNonotify = 0x0080;
    private const uint MfString = 0;
    private const int CommandOpen = 1001, CommandToggleProtection = 1002, CommandExit = 1003;
    private readonly nint _owner;
    private readonly nint _loadedIcon;
    private readonly uint _taskbarCreated;
    private bool _disposed;

    internal event Action? OpenRequested;
    internal event Action? ToggleProtectionRequested;
    internal event Action? ExitRequested;
    internal Func<bool>? AllProtectionEnabled { get; set; }

    internal TrayIcon(nint owner)
    {
        _owner = owner;
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Curtain.ico");
        if (File.Exists(iconPath))
            _loadedIcon = LoadImageW(nint.Zero, iconPath, 1, 0, 0, 0x10 | 0x40); // IMAGE_ICON, LR_LOADFROMFILE|LR_DEFAULTSIZE
        _taskbarCreated = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
        if (!AddIcon()) throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法在系统托盘创建 Curtain 图标。");
    }

    internal bool HandleMessage(uint message, nint wParam, nint lParam)
    {
        if (message == _taskbarCreated) { AddIcon(); return true; }
        if (message != WmAppTray) return false;
        switch (unchecked((uint)lParam.ToInt64()))
        {
            case WmLButtonUp: OpenRequested?.Invoke(); break;
            case WmRButtonUp: ShowMenu(); break;
        }
        return true;
    }

    private bool AddIcon()
    {
        var data = CreateData();
        bool added = Shell_NotifyIconW(NimAdd, ref data);
        if (!added) return false;
        data.uFlags = NifMessage | NifIcon | NifTip;
        Shell_NotifyIconW(1, ref data);
        return true;
    }

    private NOTIFYICONDATA CreateData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _owner,
        uID = 1,
        uFlags = NifMessage | NifIcon | NifTip,
        uCallbackMessage = WmAppTray,
        hIcon = _loadedIcon != 0 ? _loadedIcon : LoadIconW(0, (nint)32512),
        szTip = "Curtain 窗口隐私保护",
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    private void ShowMenu()
    {
        nint menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            AppendMenuW(menu, MfString, CommandOpen, "打开 Curtain");
            AppendMenuW(menu, MfString, CommandToggleProtection,
                AllProtectionEnabled?.Invoke() == true ? "全部暂停保护" : "全部启用保护");
            AppendMenuW(menu, 0x0800, 0, null);
            AppendMenuW(menu, MfString, CommandExit, "退出 Curtain");
            NativeMethods.SetForegroundWindow(_owner);
            if (!NativeMethods.GetCursorPos(out var point)) return;
            uint command = TrackPopupMenu(menu, TpmRightButton | TpmReturnCommand | TpmNonotify,
                point.X, point.Y, 0, _owner, 0);
            NativeMethods.PostMessageW(_owner, WmNull, 0, 0);
            switch (command)
            {
                case CommandOpen: OpenRequested?.Invoke(); break;
                case CommandToggleProtection: ToggleProtectionRequested?.Invoke(); break;
                case CommandExit: ExitRequested?.Invoke(); break;
            }
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = CreateData();
        Shell_NotifyIconW(NimDelete, ref data);
        if (_loadedIcon != 0) DestroyIcon(_loadedIcon);
        OpenRequested = null; ToggleProtectionRequested = null; ExitRequested = null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize; public nint hWnd; public uint uID, uFlags, uCallbackMessage; public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public nint hBalloonIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImageW(nint hinst, string path, uint type, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyIcon(nint hIcon);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadIconW(nint instance, nint iconName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(nint menu, uint flags, int id, string? text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint owner, nint rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
}
