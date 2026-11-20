using System.ComponentModel;
using System.Runtime.InteropServices;
using Curtain.App.Interop;
using Curtain.App.Services;
using Curtain.Core.Geometry;

namespace Curtain.App.Windows;

internal sealed class ClearAreaResizeWindow : IDisposable
{
    private const string ClassName = "Curtain.ClearArea.Resize";
    private static readonly HandleNative.WindowProc DefaultProcedure = HandleNative.DefWindowProcW;
    private static bool _registered;
    private readonly WindowMessageHost _messages;
    private readonly Func<AreaResizeEdge> _hit;
    internal nint Handle { get; }
    internal event Action? Pressed, Released, ContextMenuRequested;
    internal event Action<nint>? PointerMoved;

    internal ClearAreaResizeWindow(nint owner, Func<AreaResizeEdge> hit)
    {
        _hit = hit;
        if (!_registered)
        {
            var cls = new HandleNative.WindowClass
            {
                Size = (uint)Marshal.SizeOf<HandleNative.WindowClass>(), Name = ClassName,
                Instance = HandleNative.GetModuleHandleW(null),
                Proc = Marshal.GetFunctionPointerForDelegate(DefaultProcedure),
                Background = GetStockObject(4) // BLACK_BRUSH
            };
            if (HandleNative.RegisterClassExW(ref cls) == 0) throw new Win32Exception();
            _registered = true;
        }
        Handle = HandleNative.CreateWindowExW(0x00080000 | 0x08000000 | 0x80, ClassName,
            "Curtain Area Resize", 0x80000000, 0, 0, 1, 1, owner, 0, HandleNative.GetModuleHandleW(null), 0);
        if (Handle == 0) throw new Win32Exception();
        try
        {
            if (!SetLayeredWindowAttributes(Handle, 0, 1, 2)) throw new Win32Exception();
            _messages = new WindowMessageHost(Handle, OnMessage);
        }
        catch
        {
            HandleNative.DestroyWindow(Handle);
            throw;
        }
    }

    internal void SetBands(IEnumerable<PixelRect> bands, PixelRect? popup)
    {
        nint region = CreateRectRgn(0, 0, 0, 0);
        if (region == 0) throw new Win32Exception();
        try
        {
            foreach (var band in bands) Combine(band, 2); // RGN_OR
            if (popup is { IsValid: true } cover) Combine(cover, 4); // RGN_DIFF
            if (SetWindowRgn(Handle, region, true) == 0) throw new Win32Exception();
            region = 0; // Windows owns the assigned region.
        }
        finally { if (region != 0) HandleNative.DeleteObject(region); }

        void Combine(PixelRect bounds, int mode)
        {
            nint part = CreateRectRgn(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            if (part == 0) throw new Win32Exception();
            try { if (CombineRgn(region, region, part, mode) == 0) throw new Win32Exception(); }
            finally { HandleNative.DeleteObject(part); }
        }
    }

    internal static void UseCursor(AreaResizeEdge edge)
    {
        int resource = edge switch
        {
            AreaResizeEdge.Left | AreaResizeEdge.Top or AreaResizeEdge.Right | AreaResizeEdge.Bottom => 32642,
            AreaResizeEdge.Right | AreaResizeEdge.Top or AreaResizeEdge.Left | AreaResizeEdge.Bottom => 32643,
            AreaResizeEdge.Left or AreaResizeEdge.Right => 32644,
            AreaResizeEdge.Top or AreaResizeEdge.Bottom => 32645,
            _ => 32646 // SIZEALL
        };
        SetCursor(HandleNative.LoadCursorW(0, resource));
    }

    private nint? OnMessage(uint message)
    {
        switch (message)
        {
            case 0x21: return 3; // MA_NOACTIVATE
            case 0x84: return _hit() == AreaResizeEdge.None ? -1 : 1; // HTTRANSPARENT or HTCLIENT
            case 0x20 when _hit() is var edge && edge != AreaResizeEdge.None:
                UseCursor(edge);
                return 1;
            case 0x200: PointerMoved?.Invoke(Handle); return null;
            case 0x201: Pressed?.Invoke(); return 0;
            case 0x202: Released?.Invoke(); return 0;
            case 0x205: ContextMenuRequested?.Invoke(); return 0;
            default: return null;
        }
    }

    public void Dispose()
    {
        _messages.Dispose();
        HandleNative.DestroyWindow(Handle);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint color, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int CombineRgn(nint destination, nint a, nint b, int mode);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);
}
