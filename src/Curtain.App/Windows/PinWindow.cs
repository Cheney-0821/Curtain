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
    private const double InwardSlideMs = 210;
    private static readonly HandleNative.WindowProc Procedure = OnMessage;
    private static readonly Dictionary<nint, PinWindow> Windows = new();
    private static bool _registered;

    private bool _closed = true, _hovered, _pressed;
    private int _width, _height, _dpi = 96;
    private bool _inside;
    private readonly bool _fullScreen;
    private readonly bool _areaControl;
    private double _opacityScale = 1;
    private double _hoverAmount, _pressAmount;
    private double _hoverStartValue, _pressStartValue;
    private long _hoverStartTicks, _pressStartTicks;
    private bool _inwardRevealed;
    private double _inwardAmount, _inwardStartValue;
    private long _inwardStartTicks;
    private bool _timerRunning;
    private nint _paintDc, _bitmap, _previousBitmap, _bits;
    private int[] _pixels = [];

    public nint Handle { get; }
    internal bool IsVisible => NativeMethods.IsWindowVisible(Handle);
    internal bool IsPressed => _pressed;
    public event Action? ToggleRequested;
    internal event Action? AreaPressed, AreaReleased;
    internal event Action<nint>? AreaPointerMoved;
    public event Action<PixelPoint>? ContextMenuRequested;

    internal bool AreaDragging { get; set; }
    internal void ReleaseAreaPress() => BeginPress(false);
    private bool HasWideHitTarget => !_areaControl && !_inside;

    private void ResetPointer()
    {
        BeginPress(false);
        BeginHover(false);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetTimer(nint hwnd, nint idEvent, uint interval, nint callback);
    [DllImport("user32.dll")]
    private static extern bool KillTimer(nint hwnd, nint idEvent);

    public PinWindow(bool fullScreen = false, bool areaControl = false)
    {
        _fullScreen = fullScreen;
        _areaControl = areaControl;
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
    }

    internal void SetOpacity(double opacity)
    {
        opacity = Math.Clamp(opacity, 0, 1);
        if (_opacityScale == opacity) return;
        _opacityScale = opacity;
        if (_width > 0) Render();
    }

    internal void SetInwardReveal(bool reveal, bool animate)
    {
        if (animate && _inwardRevealed == reveal) return;
        TickAnimation(false);
        _inwardRevealed = reveal;
        double goal = reveal ? 1 : 0;
        if (animate && _inwardAmount != goal)
        {
            _inwardStartValue = _inwardAmount;
            _inwardStartTicks = Stopwatch.GetTimestamp();
            StartTimer();
        }
        else
        {
            _inwardStartTicks = 0;
            _inwardAmount = goal;
            if (reveal) Render();
        }
        if (reveal && !IsVisible)
            NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
        else if (!reveal && _inwardStartTicks == 0 && IsVisible)
            NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);
    }

    internal bool ContainsPoint(NativeMethods.Point point)
    {
        if (!NativeMethods.GetWindowRect(Handle, out var bounds)) return false;
        var (width, height, centerY) = CapsuleDimensions();
        double x = point.X - bounds.Left - _width / 2.0;
        double y = point.Y - bounds.Top - centerY;
        double dx = Math.Max(0, Math.Abs(x) - (width - height) / 2);
        return dx * dx + y * y <= height * height / 4;
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
        if ((_width, _height) != (b.Width, b.Height)) ReleaseSurface();
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
        if (!_timerRunning)
        {
            _timerRunning = SetTimer(Handle, (nint)AnimationTimer, _inside ? 10u : 16u, 0) != 0;
            // Timer creation failure must not break the handle: settle immediately.
            if (!_timerRunning)
            {
                _hoverAmount = _hovered ? 1 : 0;
                _pressAmount = _pressed ? 1 : 0;
                _inwardAmount = _inwardRevealed ? 1 : 0;
                _inwardStartTicks = 0;
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
        bool sliding = _inwardStartTicks != 0;
        if (sliding)
        {
            _inwardAmount = Transition(_inwardStartValue, _inwardRevealed ? 1 : 0, _inwardStartTicks, InwardSlideMs);
            if (Stopwatch.GetElapsedTime(_inwardStartTicks).TotalMilliseconds >= InwardSlideMs)
            {
                _inwardAmount = _inwardRevealed ? 1 : 0;
                if (paint) _inwardStartTicks = 0;
            }
        }

        bool hoverFinished = _hoverStartTicks == 0 || Stopwatch.GetElapsedTime(_hoverStartTicks).TotalMilliseconds >= 170;
        bool pressFinished = _pressStartTicks == 0 || Stopwatch.GetElapsedTime(_pressStartTicks).TotalMilliseconds >= 105;
        if (_timerRunning && hoverFinished && pressFinished && _inwardStartTicks == 0)
        {
            KillTimer(Handle, (nint)AnimationTimer);
            _timerRunning = false;
        }
        if (paint) Render();
        if (paint && sliding && _inwardStartTicks == 0 && !_inwardRevealed && _inside && IsVisible)
            NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);
    }

    private (double Width, double Height, double Y) CapsuleDimensions()
    {
        double scale = _dpi / 96.0;
        double barWidth = Math.Max(1, Math.Min((_fullScreen ? 104 + 12 * _hoverAmount - 6 * _pressAmount : 60 + 8 * _hoverAmount - 5 * _pressAmount) * scale, _width - 4 * scale));
        double barHeight = Math.Max(1, Math.Min((_fullScreen ? 18 + 2 * _hoverAmount - _pressAmount : 8 + 2 * _hoverAmount - 1.5 * _pressAmount) * scale, _height - 2 * scale));
        double radius = barHeight / 2;
        double gap = PinPlacement.WindowPinGap * scale;
        double centerY = _fullScreen ? _height / 2.0 : _inside ? Math.Min(_height - radius, Math.Round(gap) + gap + radius)
            : Math.Max(_height / 2.0, _height - gap - radius);
        if (_inside) centerY -= (1 - _inwardAmount) * (centerY + radius + 1);
        return (barWidth, barHeight, centerY);
    }

    private void Render()
    {
        if (!NativeMethods.IsWindow(Handle) || _width <= 0 || _height <= 0) return;
        double scale = _dpi / 96.0;
        var (barWidth, barHeight, centerY) = CapsuleDimensions();
        double radius = barHeight / 2;
        EnsureSurface();
        // An inward control sits above the target's own UI. Only its capsule can receive input.
        int minimumAlpha = HasWideHitTarget ? 1 : 0;
        double opacity = Math.Clamp((_closed ? 240 : 210) + 15 * _hoverAmount - 24 * _pressAmount, 0, 255) * _opacityScale;
        for (int y = 0; y < _height; y++)
        for (int x = 0; x < _width; x++)
        {
            double dx = Math.Max(0, Math.Abs(x + .5 - _width / 2.0) - (barWidth / 2 - radius));
            double dy = y + .5 - centerY;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            double coverage = Math.Clamp(radius + .5 - distance, 0, 1);
            int alpha = Math.Max(minimumAlpha, (int)Math.Round(coverage * opacity));
            double inner = Math.Clamp(radius - scale + .5 - distance, 0, 1);
            int shade = (int)Math.Round(70 + 185 * inner);
            int premultiplied = shade * alpha / 255;
            _pixels[y * _width + x] = (alpha << 24) | (premultiplied << 16) | (premultiplied << 8) | premultiplied;
        }
        Marshal.Copy(_pixels, 0, _bits, _pixels.Length);
        var size = new HandleNative.Size { Width = _width, Height = _height };
        var point = new NativeMethods.Point();
        var blend = new HandleNative.Blend { Alpha = 255, Format = 1 };
        if (!HandleNative.UpdateLayeredWindow(Handle, 0, 0, ref size, _paintDc,
            ref point, 0, ref blend, 2)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private void EnsureSurface()
    {
        if (_bitmap != 0) return;
        _pixels = new int[_width * _height];
        _paintDc = HandleNative.CreateCompatibleDC(0);
        if (_paintDc == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var info = new HandleNative.BitmapInfo { HeaderSize = 40, Width = _width,
            Height = -_height, Planes = 1, Bits = 32 };
        _bitmap = HandleNative.CreateDIBSection(_paintDc, ref info, 0, out _bits, 0, 0);
        if (_bitmap != 0) _previousBitmap = HandleNative.SelectObject(_paintDc, _bitmap);
        if (_previousBitmap != 0 && _previousBitmap != -1) return;
        var error = new Win32Exception(Marshal.GetLastPInvokeError());
        ReleaseSurface();
        throw error;
    }

    private void ReleaseSurface()
    {
        if (_previousBitmap != 0 && _previousBitmap != -1) HandleNative.SelectObject(_paintDc, _previousBitmap);
        if (_bitmap != 0) HandleNative.DeleteObject(_bitmap);
        if (_paintDc != 0) HandleNative.DeleteDC(_paintDc);
        _paintDc = _bitmap = _previousBitmap = _bits = 0;
        _pixels = [];
    }

    private static nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (Windows.TryGetValue(hwnd, out var window))
        {
            try
            {
                if (message == 0x18 && wParam == 0 && !window._fullScreen && !window._areaControl)
                    window.ResetPointer();
                if (message == 0x21) return 3; // MA_NOACTIVATE
                if (message == 0x20 && window.AreaDragging)
                {
                    ClearAreaResizeWindow.UseCursor(AreaResizeEdge.None);
                    return 1;
                }
                if (message == 0x84 && (window._fullScreen || !window.HasWideHitTarget) &&
                    NativeMethods.GetCursorPos(out var hitPoint) && !window.ContainsPoint(hitPoint))
                    return -1; // HTTRANSPARENT outside the visible capsule.
                if (message == 0x200 && !window._hovered)
                {
                    window.BeginHover(true);
                    var track = new HandleNative.Track { Size = (uint)Marshal.SizeOf<HandleNative.Track>(), Flags = 2, Window = hwnd };
                    HandleNative.TrackMouseEvent(ref track);
                }
                if (message == 0x200 && window._areaControl && window._pressed)
                    window.AreaPointerMoved?.Invoke(hwnd);
                if (message == 0x2A3)
                {
                    if (!window._areaControl) window.BeginPress(false);
                    window.BeginHover(false);
                }
                if (message == 0x201)
                {
                    window.BeginPress(true);
                    if (window._areaControl) window.AreaPressed?.Invoke();
                    return 0;
                }
                if (message == 0x202)
                {
                    bool pressed = window._pressed;
                    window.BeginPress(false);
                    if (pressed && window._areaControl) window.AreaReleased?.Invoke();
                    else if (pressed && (!window._fullScreen ||
                        NativeMethods.GetCursorPos(out var point) && window.ContainsPoint(point)))
                        window.ToggleRequested?.Invoke();
                    return 0;
                }
                if (message == 0x205 && window.ContextMenuRequested is { } requested &&
                    NativeMethods.GetCursorPos(out var contextPoint))
                {
                    requested(new(contextPoint.X, contextPoint.Y));
                    return 0;
                }
                if (message == WM_TIMER && wParam == (nint)AnimationTimer)
                { window.TickAnimation(true); return 0; }
                if (message == 0x82)
                {
                    if (window._timerRunning) KillTimer(hwnd, (nint)AnimationTimer);
                    window._timerRunning = false;
                    window.ReleaseSurface();
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
        ReleaseSurface();
        Windows.Remove(Handle);
    }
}
