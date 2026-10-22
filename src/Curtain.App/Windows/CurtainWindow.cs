using Curtain.App.Interop;
using Curtain.App.Services;
using Curtain.Core.Geometry;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT;
using WinRT.Interop;

namespace Curtain.App.Windows;

internal sealed class CurtainWindow : Window
{
    private readonly Grid _surface;
    private readonly global::Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _configuration;
    private bool? _lastMaximized;
    private bool _disposed;
    private Microsoft.UI.Xaml.Input.Pointer? _pointer;
    public nint Handle { get; }
    public bool HasSurfaceCapture => _pointer is not null;
    public event Action<PixelPoint>? SurfacePressed;
    public event Action<PixelPoint>? SurfaceMoved;
    public event Action? SurfaceEnded;

    public CurtainWindow()
    {
        _surface = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        _surface.PointerPressed += OnPointerPressed;
        _surface.PointerMoved += OnPointerMoved;
        _surface.PointerReleased += (_, e) => { e.Handled = true; ReleaseSurfaceCapture(); };
        _surface.PointerCanceled += (_, _) => ReleaseSurfaceCapture();
        _surface.PointerCaptureLost += (_, _) => ReleaseSurfaceCapture();
        Content = _surface;
        Handle = WindowNative.GetWindowHandle(this);
        NativeMethods.RemoveWindowChrome(Handle);
        InitializeAcrylic();
        _uiSettings.AdvancedEffectsEnabledChanged += OnEffectsChanged;
        Closed += (_, _) => ReleaseResources();
        AppWindow.Show(false);
        NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);
    }

    private void ReleaseResources()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseSurfaceCapture();
        _uiSettings.AdvancedEffectsEnabledChanged -= OnEffectsChanged;
        if (_acrylic is not null) _acrylic.StateChanged -= OnMaterialStateChanged;
        _acrylic?.Dispose(); _acrylic = null; _configuration = null;
    }
    internal void CloseOwnedWindow()
    {
        // Windows may already have destroyed this owned HWND with its external owner.
        if (NativeMethods.IsWindow(Handle)) Close();
        ReleaseResources();
    }

    private void InitializeAcrylic()
    {
        DesktopAcrylicController? candidate = null;
        try
        {
            if (!DesktopAcrylicController.IsSupported()) throw new NotSupportedException();
            DispatcherQueue.EnsureSystemDispatcherQueue();
            _configuration = new SystemBackdropConfiguration { IsInputActive = true, Theme = SystemBackdropTheme.Light };
            candidate = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base,
                TintColor = Colors.White, TintOpacity = 0, LuminosityOpacity = 0,
                FallbackColor = global::Windows.UI.Color.FromArgb(255, 48, 48, 48) };
            if (!candidate.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>()))
                throw new InvalidOperationException("Backdrop target rejected");
            candidate.SetSystemBackdropConfiguration(_configuration);
            _acrylic = candidate;
            candidate.StateChanged += OnMaterialStateChanged;
            DevelopmentDiagnostics.Write("material-api", new {
                properties = typeof(DesktopAcrylicController).GetProperties().Select(p => p.Name).ToArray() });
            Log("initial");
        }
        catch (Exception ex)
        {
            candidate?.Dispose(); _acrylic = null; _configuration = null;
            _surface.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 48, 48, 48));
            DevelopmentDiagnostics.Write("material-fallback", new { exception = ex.GetType().Name, ex.HResult });
        }
    }

    private void OnMaterialStateChanged(ISystemBackdropControllerWithTargets sender, object args)
    {
        RefreshMaterialPolicy("state");
    }

    private void OnEffectsChanged(global::Windows.UI.ViewManagement.UISettings sender, object args) =>
        RefreshMaterialPolicy("system-effects");

    private void RefreshMaterialPolicy(string reason) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_disposed) return;
        Log(reason);
    });

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
    private void Log(string reason)
    {
        DevelopmentDiagnostics.Write("material", new { reason,
            requestedTint = 0, requestedLuminosity = 0,
            actualTint = _acrylic?.TintOpacity, actualLuminosity = _acrylic?.LuminosityOpacity,
            color = _acrylic is null ? null : new { _acrylic.TintColor.A, _acrylic.TintColor.R, _acrylic.TintColor.G, _acrylic.TintColor.B },
            state = _acrylic?.State.ToString(), systemEffectsEnabled = _uiSettings.AdvancedEffectsEnabled,
            fallbackExpected = _acrylic is null || !_uiSettings.AdvancedEffectsEnabled ||
                _acrylic.State != SystemBackdropState.Active });
    }

    public void SetTargetMaximized(bool maximized)
    {
        if (_lastMaximized == maximized) return;
        _lastMaximized = maximized;
        NativeMethods.TrySetCornerPreference(Handle, maximized ? NativeMethods.DWMWCP_DONOTROUND : NativeMethods.DWMWCP_ROUND);
    }
}
