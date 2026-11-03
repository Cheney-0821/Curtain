using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Curtain.App.Interop;
using Curtain.App.Services;
using Curtain.Core.Geometry;
using Curtain.Core.Rendering;
using Curtain.Core.Settings;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Dispatching;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;
namespace Curtain.App.Windows;

// An independent desktop backdrop host; no target-window capture.
internal sealed class CurtainWindow
{
    private const string ClassName = "Curtain.Gaussian.Overlay";
    private static readonly HandleNative.WindowProc Procedure = HandleNative.DefWindowProcW;
    private static bool _registered;
    private readonly Compositor _compositor;
    private readonly DesktopWindowTarget _target;
    private readonly SpriteVisual _visual;
    private CompositionBackdropBrush _backdrop;
    private readonly CompositionEffectFactory _factory;
    private CompositionEffectBrush _effect;
    private readonly WindowMessageHost _messages;
    private readonly OverlayOpacityFade _input;
    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueue _dispatcher;
    private readonly long _protectedWindowHandle;
    private readonly uint _protectedProcessId;
    private bool _disposed, _desiredVisible, _running, _firstPaintQueued, _firstFramePresented;
    private bool? _lastMaximized;
    private Action? _firstFrameCallback;
    private WindowProtectionSettings _settings = new();
    private double _ramp, _from, _to, _duration;
    private long _started;
    private bool _pendingShow;
    private int _showGeneration, _warmupTicks;
    internal event Action? RenderFailed;
    public nint Handle { get; }
    public bool HasSurfaceCapture => GetCapture() == Handle;
    internal bool IsVisible => NativeMethods.IsWindowVisible(Handle);
    public event Action<PixelPoint>? SurfacePressed;
    public event Action<PixelPoint>? SurfaceMoved;
    public event Action? SurfaceEnded;

    public CurtainWindow(DispatcherQueue dispatcher, long protectedWindowHandle, uint protectedProcessId)
    {
        _dispatcher = dispatcher;
        _protectedWindowHandle = protectedWindowHandle;
        _protectedProcessId = protectedProcessId;
        dispatcher.EnsureSystemDispatcherQueue();
        if (!_registered)
        {
            var cls = new HandleNative.WindowClass { Size = (uint)Marshal.SizeOf<HandleNative.WindowClass>(),
                Name = ClassName, Instance = HandleNative.GetModuleHandleW(null),
                Proc = Marshal.GetFunctionPointerForDelegate(Procedure), Cursor = HandleNative.LoadCursorW(0, (nint)32512) };
            if (HandleNative.RegisterClassExW(ref cls) == 0) throw new System.ComponentModel.Win32Exception();
            _registered = true;
        }
        Handle = HandleNative.CreateWindowExW(0x00200000 | 0x08000000 | 0x80, ClassName,
            "Curtain privacy overlay", 0x80000000, 0, 0, 1, 1, 0, 0, HandleNative.GetModuleHandleW(null), 0);
        if (Handle == 0) throw new System.ComponentModel.Win32Exception();
        try
        {
            int disabled = 0;
            Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(Handle, 17, ref disabled, 4));
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(Handle, ref margins));
            _compositor = new Compositor();
            _compositor.As<ICompositorDesktopInterop>().CreateDesktopWindowTarget(Handle, false, out nint raw);
            try { _target = MarshalInterface<DesktopWindowTarget>.FromAbi(raw); }
            finally { if (raw != 0) Marshal.Release(raw); }
            _visual = _compositor.CreateSpriteVisual(); _visual.Size = Vector2.One;
            _backdrop = _compositor.CreateBackdropBrush();
            using var blur = new GaussianBlurEffect { Name = "Blur", BlurAmount = 0,
                BorderMode = EffectBorderMode.Hard, Source = new CompositionEffectSourceParameter("Source") };
            _factory = _compositor.CreateEffectFactory(blur, new[] { "Blur.BlurAmount" });
            _effect = _factory.CreateBrush(); _effect.SetSourceParameter("Source", _backdrop);
            _visual.Brush = _effect; _target.Root = _visual;
            _input = new OverlayOpacityFade(Handle, dispatcher);
            _messages = new WindowMessageHost(Handle); _messages.AfterMessage += OnMessage;
            _timer = dispatcher.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(16); _timer.IsRepeating = true;
            _timer.Tick += (_, _) => Tick();
        }
        catch
        {
            _messages?.Dispose(); _input?.Dispose(); _timer?.Stop();
            if (_target is not null) _target.Root = null;
            _effect?.Dispose(); _factory?.Dispose(); _backdrop?.Dispose(); _visual?.Dispose();
            _target?.Dispose(); _compositor?.Dispose();
            HandleNative.DestroyWindow(Handle); throw;
        }
    }
    internal bool ConfigureWorkingProtection(WindowProtectionSettings settings)
    {
        if (_disposed) return false;
        if (settings == _settings) return true;
        double previousAmount = _settings.BlurAmount;
        if (settings.AllowClickThrough) ReleaseSurfaceCapture();
        _input.SetPointerPassthrough(settings.AllowClickThrough);
        _settings = settings; ApplyBlur();
        if (IsVisible && !_running && Math.Abs(previousAmount - settings.BlurAmount) >= 2)
        {
            using var transition = _compositor.CreateScalarKeyFrameAnimation();
            transition.Duration = TimeSpan.FromMilliseconds(120);
            transition.InsertKeyFrame(0, (float)(previousAmount * _ramp / 255));
            transition.InsertKeyFrame(1, (float)(settings.BlurAmount * _ramp / 255));
            _effect.StartAnimation("Blur.BlurAmount", transition);
        }
        return true;
    }
    private void ApplyBlur()
    {
        _effect.StopAnimation("Blur.BlurAmount");
        _effect.Properties.InsertScalar("Blur.BlurAmount", (float)(_settings.BlurAmount * _ramp / 255));
    }
    internal void SetProtectedVisible(bool visible, bool animate, long? batchStart = null)
    {
        if (_disposed || (_desiredVisible == visible && (_running || _pendingShow || IsVisible == visible))) return;
        if (_running) Tick();
        _desiredVisible = visible;
        int generation = ++_showGeneration;
        _pendingShow = false; _warmupTicks = 0;
        if (!visible && _ramp == 0) { CancelAndHide(); return; }
        if (visible && !IsVisible && _firstFramePresented)
        {
            _pendingShow = true;
            _ramp = 0; _visual.Opacity = 0.001f;
            RefreshBackdropInput();
            ApplyBlur();
            _ = ShowFreshBackdropAsync(generation, animate);
            return;
        }
        _visual.Opacity = 1;
        if (!animate)
        {
            _ramp = visible ? 255 : 0; ApplyBlur(); NativeMethods.ShowWindow(Handle, visible ? 4 : 0);
            _timer.Stop(); _running = false; return;
        }
        _from = _ramp; _to = visible ? 255 : 0;
        _duration = OverlayFadeCurve.DurationFor(_from, _to);
        _started = batchStart ?? Stopwatch.GetTimestamp(); ApplyBlur();
        if (visible) NativeMethods.ShowWindow(Handle, 4);
        _running = true; _timer.Start();
    }
    private void RefreshBackdropInput()
    {
        // Preserve the compiled effect factory, but discard hidden-window sampling state.
        var source = _compositor.CreateBackdropBrush();
        var effect = _factory.CreateBrush();
        effect.SetSourceParameter("Source", source);
        var oldEffect = _effect; var oldSource = _backdrop;
        _effect = effect; _backdrop = source; _visual.Brush = effect;
        oldEffect.Dispose(); oldSource.Dispose();
    }
    private async Task ShowFreshBackdropAsync(int generation, bool animate)
    {
        try
        {
            // Submit the new input/hidden visual before exposing the native HWND.
            await _compositor.RequestCommitAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(250));
            if (_disposed || generation != _showGeneration || !_desiredVisible) return;
            _pendingShow = false;
            if (!animate)
            {
                _ramp = 255; ApplyBlur(); _visual.Opacity = 1; NativeMethods.ShowWindow(Handle, 4);
                return;
            }
            _from = 0; _to = 255; _duration = OverlayFadeCurve.FullDurationMs;
            _started = Stopwatch.GetTimestamp(); _warmupTicks = 2;
            NativeMethods.ShowWindow(Handle, 4); _running = true; _timer.Start();
            DevelopmentDiagnostics.Write("backdrop-input-refreshed", new { generation });
        }
        catch (Exception ex)
        {
            if (_disposed || generation != _showGeneration) return;
            _pendingShow = false;
            DevelopmentDiagnostics.Write("backdrop-refresh-failed", new
            {
                targetWindow = _protectedWindowHandle,
                targetProcessId = _protectedProcessId,
                overlayWindow = Handle.ToInt64(),
                exception = ex.GetType().Name,
                ex.HResult
            });
            RenderFailed?.Invoke();
        }
    }
    private void Tick()
    {
        if (_disposed || !_running) return;
        if (_warmupTicks > 0)
        {
            if (--_warmupTicks == 0) { _visual.Opacity = 1; _started = Stopwatch.GetTimestamp(); }
            return;
        }
        double elapsed = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        _ramp = OverlayFadeCurve.Sample(_from, _to, elapsed, _duration); ApplyBlur();
        if (elapsed < _duration) return;
        _running = false; _timer.Stop();
        if (!_desiredVisible) NativeMethods.ShowWindow(Handle, 0);
    }
    internal void CancelAndHide()
    {
        if (_disposed) return;
        ++_showGeneration; _pendingShow = false; _warmupTicks = 0;
        _timer.Stop(); _running = _desiredVisible = false; _ramp = 0;
        NativeMethods.ShowWindow(Handle, 0); _visual.Opacity = 1; ReleaseSurfaceCapture();
    }
    private void OnMessage(uint message, nint wParam, nint lParam)
    {
        if (_disposed) return;
        if (message == 0x0005 && GetClientRect(Handle, out var client))
            _visual.Size = new Vector2(Math.Max(1, client.Right - client.Left), Math.Max(1, client.Bottom - client.Top));
        else if (message == 0x0201 && !_settings.AllowClickThrough)
        {
            SetCapture(Handle);
            if (NativeMethods.GetCursorPos(out var point)) SurfacePressed?.Invoke(new(point.X, point.Y));
        }
        else if (message == 0x0200 && HasSurfaceCapture && NativeMethods.GetCursorPos(out var point))
            SurfaceMoved?.Invoke(new(point.X, point.Y));
        else if (message is 0x0202 or 0x001F or 0x0215) ReleaseSurfaceCapture();
        else if (message == 0x000F && !_firstPaintQueued && IsVisible)
        { _firstPaintQueued = true; _dispatcher.TryEnqueue(NotifyFirstFrame); }
    }
    internal bool WaitForFirstFrame(Action callback)
    {
        if (_disposed || _firstFramePresented) return false;
        _firstFrameCallback += callback; return true;
    }
    private void NotifyFirstFrame()
    {
        _firstFramePresented = true;
        var callback = _firstFrameCallback; _firstFrameCallback = null; callback?.Invoke();
    }
    public void ReleaseSurfaceCapture()
    {
        if (!HasSurfaceCapture) return;
        ReleaseCapture(); SurfaceEnded?.Invoke();
    }
    public void SetTargetMaximized(bool maximized)
    {
        if (_lastMaximized == maximized) return;
        _lastMaximized = maximized;
        NativeMethods.TrySetCornerPreference(Handle, maximized ? NativeMethods.DWMWCP_DONOTROUND : NativeMethods.DWMWCP_ROUND);
    }
    internal void CloseOwnedWindow()
    {
        if (_disposed) return;
        CancelAndHide(); _disposed = true;
        _timer.Stop(); _messages.Dispose(); _input.Dispose(); NotifyFirstFrame();
        _target.Root = null; _visual.Brush = null;
        _effect.Dispose(); _factory.Dispose(); _backdrop.Dispose(); _visual.Dispose(); _target.Dispose(); _compositor.Dispose();
        if (NativeMethods.IsWindow(Handle)) HandleNative.DestroyWindow(Handle);
    }
    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    { void CreateDesktopWindowTarget(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost, out nint result); }
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);
    [DllImport("user32.dll")] private static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out NativeMethods.Rect rect);
}
