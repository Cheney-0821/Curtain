using Curtain.App.Interop;
using Curtain.App.Services;
using Curtain.Core.Geometry;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT;
using WinRT.Interop;
using Windows.UI.ViewManagement;

namespace Curtain.App.Windows;

internal sealed class CurtainWindow : Window
{
    private readonly Grid _surface;
    private OverlayOpacityFade? _fade;
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private bool? _lastMaximized;
    private bool _disposed;
    private Microsoft.UI.Xaml.Input.Pointer? _pointer;
    private WindowMessageHost? _messageHost;
    private bool _firstPaintQueued;
    private bool _firstFramePresented;
    private Action? _firstFrameCallback;
    public nint Handle { get; }
    public bool HasSurfaceCapture => _pointer is not null;
    public event Action<PixelPoint>? SurfacePressed;
    public event Action<PixelPoint>? SurfaceMoved;
    public event Action? SurfaceEnded;

    public CurtainWindow()
    {
        _surface = new Grid { Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 48, 48, 48)) };
        _surface.PointerPressed += OnPointerPressed;
        _surface.PointerMoved += OnPointerMoved;
        _surface.PointerReleased += (_, e) => { e.Handled = true; ReleaseSurfaceCapture(); };
        _surface.PointerCanceled += (_, _) => ReleaseSurfaceCapture();
        _surface.PointerCaptureLost += (_, _) => ReleaseSurfaceCapture();
        Content = _surface;
        Handle = WindowNative.GetWindowHandle(this);
        NativeMethods.RemoveWindowChrome(Handle);
        _messageHost = new WindowMessageHost(Handle);
        _messageHost.AfterMessage += OnMessageAfterDefault;
        if (InitializeBackdrop()) _surface.Background = new SolidColorBrush(Colors.Transparent);
        _fade = new OverlayOpacityFade(Handle, DispatcherQueue);
        Closed += (_, _) => ReleaseResources();
    }

    private void ReleaseResources()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseSurfaceCapture();
        if (_messageHost is not null)
        {
            _messageHost.AfterMessage -= OnMessageAfterDefault;
            _messageHost.Dispose();
            _messageHost = null;
        }
        NotifyFirstFramePresented();
        _fade?.Dispose(); _fade = null;
        _acrylic?.Dispose(); _acrylic = null;
        _backdropConfiguration = null;
    }

    private bool InitializeBackdrop()
    {
        DesktopAcrylicController? candidate = null;
        try
        {
            if (!DesktopAcrylicController.IsSupported()) throw new NotSupportedException();
            DispatcherQueue.EnsureSystemDispatcherQueue();
            _backdropConfiguration = new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = SystemBackdropTheme.Light
            };
            candidate = new DesktopAcrylicController
            {
                Kind = DesktopAcrylicKind.Base,
                TintColor = Colors.White,
                TintOpacity = 0,
                LuminosityOpacity = 0,
                FallbackColor = global::Windows.UI.Color.FromArgb(255, 48, 48, 48)
            };
            if (!candidate.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>()))
                throw new InvalidOperationException("Backdrop target rejected.");
            candidate.SetSystemBackdropConfiguration(_backdropConfiguration);
            _acrylic = candidate;
            return true;
        }
        catch (Exception ex)
        {
            candidate?.Dispose();
            _acrylic = null;
            _backdropConfiguration = null;
            DevelopmentDiagnostics.Write("backdrop-host-fallback", new { exception = ex.GetType().Name, ex.HResult });
            return false;
        }
    }
    internal void CloseOwnedWindow()
    {
        // Windows may already have destroyed this owned HWND with its external owner.
        if (NativeMethods.IsWindow(Handle)) Close();
        ReleaseResources();
    }

    internal bool IsVisible => _fade?.IsVisible == true;

    internal void SetProtectedVisible(bool visible, bool animate, long? batchStart = null)
    {
        if (_disposed) return;
        bool canAnimate = animate && _acrylic is not null && SystemEffectsAllowAnimation();
        _fade?.SetVisible(visible, canAnimate, batchStart);
    }

    internal void CancelAndHide() => _fade?.CancelAndHide();

    internal bool WaitForFirstFrame(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (_disposed || _firstFramePresented) return false;
        _firstFrameCallback += callback;
        return true;
    }

    private void OnMessageAfterDefault(uint message, nint wParam, nint lParam)
    {
        const uint WmPaint = 0x000F;
        if (message != WmPaint || _firstPaintQueued || _disposed || !NativeMethods.IsWindowVisible(Handle)) return;
        _firstPaintQueued = true;
        if (!DispatcherQueue.TryEnqueue(CompleteFirstPaint)) _firstPaintQueued = false;
    }

    private void CompleteFirstPaint()
    {
        if (_disposed)
        {
            NotifyFirstFramePresented();
            return;
        }
        if (!NativeMethods.IsWindowVisible(Handle))
        {
            _firstPaintQueued = false;
            return;
        }
        NotifyFirstFramePresented();
    }

    private void NotifyFirstFramePresented()
    {
        if (_firstFramePresented) return;
        _firstFramePresented = true;
        Action? callback = _firstFrameCallback;
        _firstFrameCallback = null;
        try { callback?.Invoke(); }
        catch (Exception ex)
        {
            DevelopmentDiagnostics.Write("overlay-first-paint-callback-error", new { exception = ex.GetType().Name, ex.HResult });
        }
    }

    private static bool SystemEffectsAllowAnimation()
    {
        try
        {
            var settings = new UISettings();
            return settings.AnimationsEnabled && settings.AdvancedEffectsEnabled;
        }
        catch { return false; }
    }

    private void OnPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (!e.GetCurrentPoint(_surface).Properties.IsLeftButtonPressed || !NativeMethods.GetCursorPos(out var point)) return;
        if (_surface.CapturePointer(e.Pointer)) _pointer = e.Pointer;
        SurfacePressed?.Invoke(new PixelPoint(point.X, point.Y));
    }
    private void OnPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (_pointer is not null && NativeMethods.GetCursorPos(out var point))
            SurfaceMoved?.Invoke(new PixelPoint(point.X, point.Y));
    }
    public void ReleaseSurfaceCapture()
    {
        if (_pointer is not { } pointer) return;
        _pointer = null;
        if (NativeMethods.IsWindow(Handle)) _surface.ReleasePointerCapture(pointer);
        SurfaceEnded?.Invoke();
    }
    public void SetTargetMaximized(bool maximized)
    {
        if (_lastMaximized == maximized) return;
        _lastMaximized = maximized;
        NativeMethods.TrySetCornerPreference(Handle, maximized ? NativeMethods.DWMWCP_DONOTROUND : NativeMethods.DWMWCP_ROUND);
    }
}
