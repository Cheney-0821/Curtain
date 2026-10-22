using System.ComponentModel;
using System.Runtime.InteropServices;
using Curtain.App.Interop;
using Curtain.Core.Geometry;

namespace Curtain.App.Windows;

internal sealed class SelectionOutlineWindow : IDisposable
{
    private static readonly HandleNative.WindowProc Procedure = OnMessage;
    private static bool _registered;
    private PixelRect? _last;
    private int _dpi;
    public nint Handle { get; }
    internal SelectionOutlineWindow()
    {
        const string name = "Curtain.B1.SelectionOutline";
        if (!_registered)
        {
            var cls = new HandleNative.WindowClass { Size = (uint)Marshal.SizeOf<HandleNative.WindowClass>(),
                Name = name, Instance = HandleNative.GetModuleHandleW(null), Proc = Marshal.GetFunctionPointerForDelegate(Procedure) };
            if (HandleNative.RegisterClassExW(ref cls) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            _registered = true;
        }
        Handle = HandleNative.CreateWindowExW(0x80000 | 0x8000000 | 0x80 | 0x20,
            name, "Curtain Selection Outline", 0x80000000, 0, 0, 1, 1, 0, 0, HandleNative.GetModuleHandleW(null), 0);
        if (Handle == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
    private static nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam) =>
        message == 0x84 ? -1 : HandleNative.DefWindowProcW(hwnd, message, wParam, lParam);
    internal void Show(PixelRect bounds, int dpi)
    {
        // Selection-only overlay: intentionally topmost while selecting, unlike a Curtain
        // attached to a protected window. Leave a one-pixel gap outside the target.
        int gap = Math.Max(2, (int)Math.Ceiling(4.0 * dpi / 96) + 1);
        var frame = new PixelRect(bounds.Left - gap, bounds.Top - gap,
            bounds.Right + gap, bounds.Bottom + gap);
        if (_last?.Width != frame.Width || _last?.Height != frame.Height || _dpi != dpi)
            Render(frame.Width, frame.Height, dpi);
        _last = frame; _dpi = dpi;
        if (!NativeMethods.SetWindowPos(Handle, (nint)(-1), frame.Left, frame.Top,
            frame.Width, frame.Height, 0x0010 | 0x0040)) // NOACTIVATE | SHOWWINDOW
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
    private void Render(int width, int height, int dpi)
    {
        var pixels = new int[checked(width * height)];
        int stroke = Math.Max(3, (int)Math.Ceiling(4.0 * dpi / 96));
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            if (SelectionOutline.IsBorderPixel(x, y, width, height, stroke)) pixels[y * width + x] = unchecked((int)0xFF3196FF);
        nint screen = HandleNative.GetDC(0), memory = HandleNative.CreateCompatibleDC(screen);
        var info = new HandleNative.BitmapInfo { HeaderSize = 40, Width = width, Height = -height, Planes = 1, Bits = 32 };
        nint bitmap = HandleNative.CreateDIBSection(screen, ref info, 0, out var bits, 0, 0);
        if (bitmap == 0 || memory == 0)
        {
            if (bitmap != 0) HandleNative.DeleteObject(bitmap);
            if (memory != 0) HandleNative.DeleteDC(memory);
            HandleNative.ReleaseDC(0, screen);
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        nint old = HandleNative.SelectObject(memory, bitmap);
        try
        {
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            var size = new HandleNative.Size { Width = width, Height = height };
            var point = new NativeMethods.Point(); var blend = new HandleNative.Blend { Alpha = 255, Format = 1 };
            if (!HandleNative.UpdateLayeredWindow(Handle, screen, 0, ref size, memory, ref point, 0, ref blend, 2))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally
        {
            HandleNative.SelectObject(memory, old); HandleNative.DeleteObject(bitmap);
            HandleNative.DeleteDC(memory); HandleNative.ReleaseDC(0, screen);
        }
    }
    internal void Hide() => NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);
    public void Dispose() => HandleNative.DestroyWindow(Handle);
}
