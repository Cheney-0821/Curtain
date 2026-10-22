using System.ComponentModel;
using System.Runtime.InteropServices;
using Curtain.App.Interop;
using Curtain.Core.Geometry;

namespace Curtain.App.Windows;

/// <summary>
/// One temporary, click-through desktop dimmer with a transparent region around
/// the hovered target. Keeping the dimmer in one HWND avoids panel z-order gaps.
/// </summary>
internal sealed class SelectionSpotlightWindow : IDisposable
{
    private const string WindowClassName = "Curtain.B35.SelectionSpotlight";
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint LWA_ALPHA = 0x00000002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int RGN_DIFF = 4;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const byte DimAlpha = 138;

    private static readonly HandleNative.WindowProc WindowProc = OnMessage;
    private static bool _registered;
    private readonly nint _window;
    private PixelRect? _lastCandidate;
    private int _lastDpi;
    private bool _shown;
    private bool _disposed;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int CombineRgn(nint destination, nint source1, nint source2, int mode);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int type);

    internal SelectionSpotlightWindow()
    {
        EnsureClass();
        _window = HandleNative.CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            WindowClassName, "Curtain window selection dimmer", WS_POPUP,
            0, 0, 1, 1, 0, 0, HandleNative.GetModuleHandleW(null), 0);
        if (_window == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!SetLayeredWindowAttributes(_window, 0, DimAlpha, LWA_ALPHA))
        {
            int error = Marshal.GetLastPInvokeError();
            Dispose();
            throw new Win32Exception(error);
        }
    }

    private static void EnsureClass()
    {
        if (_registered) return;
        var cls = new HandleNative.WindowClass
        {
            Size = (uint)Marshal.SizeOf<HandleNative.WindowClass>(),
            Name = WindowClassName,
            Instance = HandleNative.GetModuleHandleW(null),
            Proc = Marshal.GetFunctionPointerForDelegate(WindowProc),
            Background = GetStockObject(4)
        };
        if (HandleNative.RegisterClassExW(ref cls) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        _registered = true;
    }

    private static nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam) =>
        message == 0x84 ? (nint)(-1) : HandleNative.DefWindowProcW(hwnd, message, wParam, lParam);

    internal void Update(PixelRect? candidate, int dpi = 96)
    {
        if (_disposed) return;
        dpi = Math.Max(96, dpi);
        if (_shown && candidate == _lastCandidate && dpi == _lastDpi) return;

        int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("无法读取虚拟桌面尺寸，选窗已停止。");

        nint region = CreateRectRgn(0, 0, width, height);
        if (region == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            if (candidate is { IsValid: true } bounds)
            {
                int pad = Math.Max(4, (int)Math.Ceiling(4.0 * dpi / 96) + 2);
                int holeLeft = Math.Clamp(bounds.Left - pad, left, left + width) - left;
                int holeTop = Math.Clamp(bounds.Top - pad, top, top + height) - top;
                int holeRight = Math.Clamp(bounds.Right + pad, left, left + width) - left;
                int holeBottom = Math.Clamp(bounds.Bottom + pad, top, top + height) - top;
                if (holeRight > holeLeft && holeBottom > holeTop)
                {
                    nint hole = CreateRectRgn(holeLeft, holeTop, holeRight, holeBottom);
                    if (hole == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                    try
                    {
                        if (CombineRgn(region, region, hole, RGN_DIFF) == 0)
                            throw new Win32Exception(Marshal.GetLastPInvokeError());
                    }
                    finally { HandleNative.DeleteObject(hole); }
                }
            }

            if (!_shown && !NativeMethods.SetWindowPos(_window, 0, left, top, width, height,
                    SWP_NOACTIVATE | SWP_NOZORDER))
                throw new Win32Exception(Marshal.GetLastPInvokeError());

            if (!SetWindowRgn(_window, region, true))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            region = 0; // Ownership transfers to the system after SetWindowRgn succeeds.

            if (!_shown && !NativeMethods.SetWindowPos(_window, (nint)(-1), left, top, width, height,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW))
                throw new Win32Exception(Marshal.GetLastPInvokeError());

            _shown = true;
            _lastCandidate = candidate;
            _lastDpi = dpi;
        }
        finally
        {
            if (region != 0) HandleNative.DeleteObject(region);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        HandleNative.DestroyWindow(_window);
        GC.KeepAlive(WindowProc);
    }
}
