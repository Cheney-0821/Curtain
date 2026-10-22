using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Curtain.App.Interop;
using Curtain.Core.Geometry;

namespace Curtain.App.Windows;

/// <summary>
/// Native, non-activating layered handle. Only the pixels inside the existing HWND
/// animate; the host HWND keeps the geometry established by CurtainSession.
/// </summary>
internal sealed class PinWindow
{
    private const string ClassName = "Curtain.A03.Handle";
    private const uint WM_TIMER = 0x0113, AnimationTimer = 1;
    private static readonly HandleNative.WindowProc Procedure = OnMessage;
    private static readonly Dictionary<nint, PinWindow> Windows = new();
    private static bool _registered;

    private bool _closed = true, _hovered, _pressed;
    private int _width, _height, _dpi = 96;
    private bool _inside;
    private readonly bool _animationsEnabled;
    private double _hoverAmount, _pressAmount;
    private double _hoverStartValue, _pressStartValue;
    private long _hoverStartTicks, _pressStartTicks;
    private bool _timerRunning;

    public nint Handle { get; }
    public event Action? ToggleRequested;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetTimer(nint hwnd, nint idEvent, uint interval, nint callback);
    [DllImport("user32.dll")]
    private static extern bool KillTimer(nint hwnd, nint idEvent);

    public PinWindow()
    {
        if (!_registered)
        {
            var cls = new HandleNative.WindowClass
            {
                Size = (uint)Marshal.SizeOf<HandleNative.WindowClass>(),
                Name = ClassName,
                Instance = HandleNative.GetModuleHandleW(null),
                Proc = Marshal.GetFunctionPointerForDelegate(Procedure),
                Cursor = HandleNative.LoadCursorW(0, (nint)32649)
            };
            if (HandleNative.RegisterClassExW(ref cls) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            _registered = true;
        }
        Handle = HandleNative.CreateWindowExW(0x00080000 | 0x08000000 | 0x80,
            ClassName, "Curtain Handle", 0x80000000, 0, 0, 72, 26, 0, 0,
            HandleNative.GetModuleHandleW(null), 0);
        if (Handle == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        Windows.Add(Handle, this);
        try { _animationsEnabled = new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled; }
        catch { _animationsEnabled = true; }
    }

    public void UpdateState(bool closed)
    {
        _closed = closed;
        if (_width > 0) Render();
    }

    public void UpdatePlacement(DockedPin placement, int dpi)
    {
        var b = placement.Bounds;
        if ((_width, _height, _dpi, _inside) == (b.Width, b.Height, dpi, placement.IsInside)) return;
        (_width, _height, _dpi, _inside) = (b.Width, b.Height, dpi, placement.IsInside);
        Render();
    }

    private static double Ease(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t * t * (3 - 2 * t);
    }

    private static double Transition(double origin, double goal, long ticks, double durationMs)
    {
        double elapsed = Stopwatch.GetElapsedTime(ticks).TotalMilliseconds;
        return origin + (goal - origin) * Ease(elapsed / durationMs);
    }

    private void BeginHover(bool hovered)
    {
        if (_hovered == hovered) return;
        TickAnimation(false);
        _hoverStartValue = _hoverAmount;
        _hovered = hovered;
        _hoverStartTicks = Stopwatch.GetTimestamp();
        StartTimer();
    }

    private void BeginPress(bool pressed)
    {
        if (_pressed == pressed) return;
        TickAnimation(false);
        _pressStartValue = _pressAmount;
        _pressed = pressed;
        _pressStartTicks = Stopwatch.GetTimestamp();
        StartTimer();
    }

    private void StartTimer()
    {
        if (!_animationsEnabled)
        {
            _hoverAmount = _hovered ? 1 : 0;
            _pressAmount = _pressed ? 1 : 0;
            Render();
            return;
        }
        if (!_timerRunning)
        {
            _timerRunning = SetTimer(Handle, (nint)AnimationTimer, 16, 0) != 0;
            // Timer creation failure must not break the handle: settle immediately.
            if (!_timerRunning)
            {
                _hoverAmount = _hovered ? 1 : 0;
                _pressAmount = _pressed ? 1 : 0;
            }
        }
        Render();
    }

    private void TickAnimation(bool paint)
    {
        if (_hoverStartTicks != 0)
            _hoverAmount = Transition(_hoverStartValue, _hovered ? 1 : 0, _hoverStartTicks, 170);
        if (_pressStartTicks != 0)
            _pressAmount = Transition(_pressStartValue, _pressed ? 1 : 0, _pressStartTicks, 105);

        bool hoverFinished = _hoverStartTicks == 0 || Stopwatch.GetElapsedTime(_hoverStartTicks).TotalMilliseconds >= 170;
        bool pressFinished = _pressStartTicks == 0 || Stopwatch.GetElapsedTime(_pressStartTicks).TotalMilliseconds >= 105;
        if (_timerRunning && hoverFinished && pressFinished)
        {
            KillTimer(Handle, (nint)AnimationTimer);
            _timerRunning = false;
        }
        if (paint) Render();
    }

    private void Render()
    {
        if (!NativeMethods.IsWindow(Handle) || _width <= 0 || _height <= 0) return;
        double scale = _dpi / 96.0;
        double barWidth = Math.Max(1, Math.Min((60 + 8 * _hoverAmount - 5 * _pressAmount) * scale, _width - 4 * scale));
        double barHeight = Math.Max(1, Math.Min((8 + 2 * _hoverAmount - 1.5 * _pressAmount) * scale, _height - 2 * scale));
        double radius = barHeight / 2;
        double centerY = _inside ? Math.Min(_height / 2.0, 4 * scale + radius)
            : Math.Max(_height / 2.0, _height - 4 * scale - radius);
        var pixels = new int[_width * _height];
        // Keep the wider existing click target, while its host stays visually invisible.
        Array.Fill(pixels, 1 << 24);
        double opacity = Math.Clamp((_closed ? 240 : 210) + 15 * _hoverAmount - 24 * _pressAmount, 0, 255);
        for (int y = 0; y < _height; y++)
        for (int x = 0; x < _width; x++)
        {
            double dx = Math.Max(0, Math.Abs(x + .5 - _width / 2.0) - (barWidth / 2 - radius));
            double dy = y + .5 - centerY;
            double coverage = Math.Clamp(radius + .5 - Math.Sqrt(dx * dx + dy * dy), 0, 1);
            int alpha = Math.Max(1, (int)Math.Round(coverage * opacity));
            double inner = Math.Clamp(radius - 1 * scale + .5 - Math.Sqrt(dx * dx + dy * dy), 0, 1);
            int shade = (int)Math.Round(70 + 185 * inner);
            int premultiplied = shade * alpha / 255;
            pixels[y * _width + x] = (alpha << 24) | (premultiplied << 16) | (premultiplied << 8) | premultiplied;
        }
        nint screen = HandleNative.GetDC(0), memory = HandleNative.CreateCompatibleDC(screen);
        var info = new HandleNative.BitmapInfo { HeaderSize = 40, Width = _width,
            Height = -_height, Planes = 1, Bits = 32 };
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
            var size = new HandleNative.Size { Width = _width, Height = _height };
            var point = new NativeMethods.Point();
            var blend = new HandleNative.Blend { Alpha = 255, Format = 1 };
            if (!HandleNative.UpdateLayeredWindow(Handle, screen, 0, ref size, memory,
                ref point, 0, ref blend, 2)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally
        {
            HandleNative.SelectObject(memory, old); HandleNative.DeleteObject(bitmap);
            HandleNative.DeleteDC(memory); HandleNative.ReleaseDC(0, screen);
        }
    }

    private static nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (Windows.TryGetValue(hwnd, out var window))
        {
            try
            {
                if (message == 0x21) return 3; // MA_NOACTIVATE
                if (message == 0x200 && !window._hovered)
                {
                    window.BeginHover(true);
                    var track = new HandleNative.Track { Size = (uint)Marshal.SizeOf<HandleNative.Track>(), Flags = 2, Window = hwnd };
                    HandleNative.TrackMouseEvent(ref track);
                }
                if (message == 0x2A3)
                {
                    window.BeginPress(false);
                    window.BeginHover(false);
                }
                if (message == 0x201) { window.BeginPress(true); return 0; }
                if (message == 0x202)
                {
                    bool pressed = window._pressed;
                    window.BeginPress(false);
                    if (pressed) window.ToggleRequested?.Invoke();
                    return 0;
                }
                if (message == WM_TIMER && wParam == (nint)AnimationTimer)
                { window.TickAnimation(true); return 0; }
                if (message == 0x82)
                {
                    if (window._timerRunning) KillTimer(hwnd, (nint)AnimationTimer);
                    window._timerRunning = false;
                    Windows.Remove(hwnd);
                }
            }
            catch (Exception ex) { Debug.WriteLine($"Curtain Handle: {ex}"); }
        }
        return HandleNative.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    public void Close()
    {
        if (_timerRunning) KillTimer(Handle, (nint)AnimationTimer);
        _timerRunning = false;
        HandleNative.DestroyWindow(Handle);
        Windows.Remove(Handle);
    }
}
