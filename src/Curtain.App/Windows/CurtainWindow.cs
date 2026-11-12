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
    private readonly string _operationId;
    private readonly string _sessionId;
    private string? _ruleId;
    internal void AssociateRememberedRule(string ruleId) => _ruleId = ruleId;
    private bool _disposed, _desiredVisible, _running, _firstPaintQueued, _firstFramePresented;
    private bool? _lastMaximized;
    private Action? _firstFrameCallback;
    private WindowProtectionSettings _settings = new();
    private double _ramp, _from, _to, _duration;
    private long _started;
    private bool _pendingShow;
    private int _showGeneration, _warmupTicks;
    private int _pendingBackdropGeneration;
    internal event Action? RenderFailed;
    internal event Action? FirstFramePresented;
    internal event Action<bool>? VisibilityTransitionCompleted;
    public nint Handle { get; }
    public bool HasSurfaceCapture => GetCapture() == Handle;
    internal bool IsVisible => NativeMethods.IsWindowVisible(Handle);
    public event Action<PixelPoint>? SurfacePressed;
    public event Action<PixelPoint>? SurfaceMoved;
    public event Action? SurfaceEnded;

    public CurtainWindow(DispatcherQueue dispatcher, long protectedWindowHandle, uint protectedProcessId,
        string operationId, string sessionId, string? ruleId)
    {
        _dispatcher = dispatcher;
        _protectedWindowHandle = protectedWindowHandle;
        _protectedProcessId = protectedProcessId;
        _operationId = operationId;
        _sessionId = sessionId;
        _ruleId = ruleId;
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
        string stage = "host-backdrop-attribute";
        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                int disabled = 0;
                Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(Handle, 17, ref disabled, 4));
            }
            stage = "extend-frame";
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(Handle, ref margins));
            stage = "desktop-target";
            _compositor = new Compositor();
            _compositor.As<ICompositorDesktopInterop>().CreateDesktopWindowTarget(Handle, false, out nint raw);
            try { _target = MarshalInterface<DesktopWindowTarget>.FromAbi(raw); }
            finally { if (raw != 0) Marshal.Release(raw); }
            _visual = _compositor.CreateSpriteVisual(); _visual.Size = Vector2.One;
            stage = "backdrop-brush";
            _backdrop = _compositor.CreateBackdropBrush();
            using var blur = new GaussianBlurEffect { Name = "Blur", BlurAmount = 0,
                BorderMode = EffectBorderMode.Hard, Source = new CompositionEffectSourceParameter("Source") };
            stage = "gaussian-effect";
            _factory = _compositor.CreateEffectFactory(blur, new[] { "Blur.BlurAmount" });
            _effect = _factory.CreateBrush(); _effect.SetSourceParameter("Source", _backdrop);
            _visual.Brush = _effect; _target.Root = _visual;
        CurtainDiagnostics.Write("overlay.render.initialized", operationId: _operationId,
            stage: "composition-backdrop", reasonCode: "Success", hwnd: Handle.ToInt64(),
            ownerHwnd: _protectedWindowHandle, targetProcessId: (int)protectedProcessId,
            sessionId: _sessionId, ruleId: _ruleId);
            _input = new OverlayOpacityFade(Handle, dispatcher);
            _messages = new WindowMessageHost(Handle); _messages.AfterMessage += OnMessage;
            _timer = dispatcher.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(16); _timer.IsRepeating = true;
            _timer.Tick += (_, _) => Tick();
        }
        catch (Exception ex)
        {
            CurtainDiagnostics.WriteException("overlay.render.initialization-failed", ex,
                operationId: _operationId, stage: stage, reasonCode: "CompositionInitializationFailed",
                hwnd: Handle.ToInt64(), ownerHwnd: _protectedWindowHandle,
                targetProcessId: (int)_protectedProcessId, sessionId: _sessionId, ruleId: _ruleId);
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
        bool previousClickThrough = _settings.AllowClickThrough;
        if (settings.AllowClickThrough) ReleaseSurfaceCapture();
        _input.SetPointerPassthrough(settings.AllowClickThrough);
        _settings = settings; ApplyBlur();
        CurtainDiagnostics.Write("overlay.blur.configured", operationId: _operationId,
            stage: "composition-gaussian", reasonCode: "Success", hwnd: Handle.ToInt64(),
            ownerHwnd: _protectedWindowHandle, targetProcessId: (int)_protectedProcessId,
            metricName: "BlurAmount", metricValue: settings.BlurAmount,
            sessionId: _sessionId, ruleId: _ruleId);
        if (previousClickThrough != settings.AllowClickThrough)
            CurtainDiagnostics.Write("overlay.input-mode.applied", operationId: _operationId,
                stage: "overlay-input", reasonCode: settings.AllowClickThrough ? "ClickThroughEnabled" : "ClickThroughDisabled",
                hwnd: Handle.ToInt64(), ownerHwnd: _protectedWindowHandle,
                targetProcessId: (int)_protectedProcessId, metricName: "AllowClickThrough",
                metricValue: settings.AllowClickThrough ? 1 : 0, sessionId: _sessionId, ruleId: _ruleId);
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
        CurtainDiagnostics.Write("overlay.visibility.requested", operationId: _operationId,
            stage: "window-protection",
            reasonCode: visible ? (animate ? "ShowAnimated" : "ShowImmediate") : (animate ? "HideAnimated" : "HideImmediate"),
            hwnd: Handle.ToInt64(), ownerHwnd: _protectedWindowHandle,
            targetProcessId: (int)_protectedProcessId, metricName: "BlurAmount", metricValue: _settings.BlurAmount,
            sessionId: _sessionId, ruleId: _ruleId);
        bool wasAnimating = _running;
        if (_running) Tick();
        if (wasAnimating && _running)
            CurtainDiagnostics.Write("overlay.animation.cancelled", CurtainDiagnosticLevel.Trace,
                operationId: _operationId, stage: "opacity-ramp", reasonCode: "SupersededByVisibilityRequest",
                hwnd: Handle.ToInt64(), ownerHwnd: _protectedWindowHandle,
                targetProcessId: (int)_protectedProcessId, sessionId: _sessionId, ruleId: _ruleId);
        if (_pendingShow) LogBackdropRefreshCancelled(_pendingBackdropGeneration, "SupersededByVisibilityRequest");
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
            _pendingBackdropGeneration = generation;
            CurtainDiagnostics.Write("overlay.backdrop.refresh.started", CurtainDiagnosticLevel.Information,
                operationId: _operationId, stage: "fresh-input-frame", reasonCode: "AwaitCommit",
                hwnd: Handle.ToInt64(), ownerHwnd: _protectedWindowHandle,
                targetProcessId: (int)_protectedProcessId, sessionId: _sessionId, ruleId: _ruleId);
            _ = ShowFreshBackdropAsync(generation, animate);
            return;
        }
        _visual.Opacity = 1;
        if (!animate)
        {
            _ramp = visible ? 255 : 0; ApplyBlur(); NativeMethods.ShowWindow(Handle, visible ? 4 : 0);
            _timer.Stop(); _running = false;
            VisibilityTransitionCompleted?.Invoke(visible);
            CurtainDiagnostics.Write("overlay.visibility.completed", operationId: _operationId,
                stage: "window-protection",
                reasonCode: NativeMethods.IsWindowVisible(Handle) == visible ? "Success" : "VisibilityMismatch",
                hwnd: Handle.ToInt64(), ownerHwnd: _protectedWindowHandle,
                targetProcessId: (int)_protectedProcessId, sessionId: _sessionId, ruleId: _ruleId);
            return;
        }
        _from = _ramp; _to = visible ? 255 : 0;
        _duration = OverlayFadeCurve.DurationFor(_from, _to);
        _started = batchStart ?? Stopwatch.GetTimestamp(); ApplyBlur();
        if (visible) NativeMethods.ShowWindow(Handle, 4);
        _running = true; _timer.Start();
        CurtainDiagnostics.Write("overlay.animation.started", operationId: _operationId,
            stage: "opacity-ramp", reasonCode: visible ? "Opening" : "Closing",
            hwnd: Handle.ToInt64(), ownerHwnd: _protectedWindowHandle,
            targetProcessId: (int)_protectedProcessId, metricName: "DurationMilliseconds", metricValue: _duration,
            sessionId: _sessionId, ruleId: _ruleId);
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
            if (_disposed || generation != _showGeneration || !_desiredVisible)
            {
                LogBackdropRefreshCancelled(generation,
                    _disposed ? "Disposed" : !_desiredVisible ? "VisibilityNoLongerRequested" : "Superseded");
                return;
            }
            _pendingBackdropGeneration = 0;
            CurtainDiagnostics.Write("overlay.backdrop.refresh.completed", operationId: _operationId,
                stage: "fresh-input-frame", reasonCode: "CommitCompleted", hwnd: Handle.ToInt64(),
                ownerHwnd: _protectedWindowHandle, targetProcessId: (int)_protectedProcessId,
                sessionId: _sessionId, ruleId: _ruleId);
            _pendingShow = false;
            if (!animate)
            {
                _ramp = 255; ApplyBlur(); _visual.Opacity = 1; NativeMethods.ShowWindow(Handle, 4);
                VisibilityTransitionCompleted?.Invoke(true);
                CurtainDiagnostics.Write("overlay.visibility.completed", operationId: _operationId,
                    stage: "fresh-input-frame",
                    reasonCode: NativeMethods.IsWindowVisible(Handle) ? "Success" : "VisibilityMismatch",
                    hwnd: Handle.ToInt64(), ownerHwnd: _protectedWindowHandle,
                    targetProcessId: (int)_protectedProcessId, sessionId: _sessionId, ruleId: _ruleId);
                return;
            }
            _from = 0; _to = 255; _duration = OverlayFadeCurve.FullDurationMs;
            _started = Stopwatch.GetTimestamp(); _warmupTicks = 2;
            NativeMethods.ShowWindow(Handle, 4); _running = true; _timer.Start();
            CurtainDiagnostics.Write("overlay.animation.started", operationId: _operationId,
                stage: "fresh-input-frame", reasonCode: "Opening", hwnd: Handle.ToInt64(),
                ownerHwnd: _protectedWindowHandle, targetProcessId: (int)_protectedProcessId,
                metricName: "DurationMilliseconds", metricValue: _duration, sessionId: _sessionId, ruleId: _ruleId);
        }
        catch (Exception ex)
        {
            if (_disposed || generation != _showGeneration)
            {
                LogBackdropRefreshCancelled(generation,
                    _disposed ? "Disposed" : !_desiredVisible ? "VisibilityNoLongerRequested" : "SupersededAfterFailure");
                return;
            }
            bool refreshPending = _pendingBackdropGeneration == generation;
            _pendingBackdropGeneration = 0;
            _pendingShow = false;
            CurtainDiagnostics.WriteException(refreshPending ? "overlay.backdrop.refresh.failed" : "overlay.visibility.failed", ex,
                operationId: _operationId, stage: "fresh-input-frame", reasonCode: "CommitOrShowFailed",
                hwnd: Handle.ToInt64(), ownerHwnd: _protectedWindowHandle,
                targetProcessId: (int)_protectedProcessId, sessionId: _sessionId, ruleId: _ruleId);
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
        VisibilityTransitionCompleted?.Invoke(_desiredVisible);
        CurtainDiagnostics.Write("overlay.animation.completed", operationId: _operationId,
            stage: "opacity-ramp",
            reasonCode: NativeMethods.IsWindowVisible(Handle) == _desiredVisible ? "Success" : "VisibilityMismatch",
            hwnd: Handle.ToInt64(), ownerHwnd: _protectedWindowHandle,
            targetProcessId: (int)_protectedProcessId, metricName: "BlurAmount", metricValue: _settings.BlurAmount,
            sessionId: _sessionId, ruleId: _ruleId);
    }
    internal void CancelAndHide()
    {
        if (_disposed) return;
        bool wasPending = _pendingShow;
        bool wasRunning = _running;
        bool wasActive = wasPending || wasRunning || _desiredVisible || NativeMethods.IsWindowVisible(Handle);
        if (wasPending) LogBackdropRefreshCancelled(_pendingBackdropGeneration, "ImmediateHide");
        if (wasRunning)
            CurtainDiagnostics.Write("overlay.animation.cancelled", CurtainDiagnosticLevel.Trace,
                operationId: _operationId, stage: "opacity-ramp", reasonCode: "ImmediateHide",
                hwnd: Handle.ToInt64(), ownerHwnd: _protectedWindowHandle,
                targetProcessId: (int)_protectedProcessId, sessionId: _sessionId, ruleId: _ruleId);
        ++_showGeneration; _pendingShow = false; _warmupTicks = 0;
        _timer.Stop(); _running = _desiredVisible = false; _ramp = 0;
        NativeMethods.ShowWindow(Handle, 0); _visual.Opacity = 1; ReleaseSurfaceCapture();
        bool stillVisible = NativeMethods.IsWindowVisible(Handle);
        if (wasActive || stillVisible)
            CurtainDiagnostics.Write("overlay.hidden", operationId: _operationId,
                stage: "window-overlay", reasonCode: "Immediate", hwnd: Handle.ToInt64(),
                ownerHwnd: _protectedWindowHandle, targetProcessId: (int)_protectedProcessId,
                errorCode: stillVisible ? Marshal.GetLastPInvokeError() : 0,
                sessionId: _sessionId, ruleId: _ruleId);
    }
    private void LogBackdropRefreshCancelled(int generation, string reasonCode)
    {
        if (generation == 0 || generation != _pendingBackdropGeneration) return;
        _pendingBackdropGeneration = 0;
        CurtainDiagnostics.Write("overlay.backdrop.refresh.cancelled", CurtainDiagnosticLevel.Trace,
            operationId: _operationId, stage: "fresh-input-frame", reasonCode: reasonCode,
            hwnd: Handle.ToInt64(), ownerHwnd: _protectedWindowHandle,
            targetProcessId: (int)_protectedProcessId, sessionId: _sessionId, ruleId: _ruleId);
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
        if (_firstFramePresented) return;
        _firstFramePresented = true;
        FirstFramePresented?.Invoke();
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
