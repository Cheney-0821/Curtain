using Curtain.App.Interop;
using Curtain.App.Localization;
using Curtain.App.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using WinRT;
using WinRT.Interop;
using Windows.UI.ViewManagement;

namespace Curtain.App.Windows;

/// <summary>One non-activating topmost overlay per display, with an intentional resume button.</summary>
internal sealed class ScreenPrivacyWindow : Window
{
    private const double RestoreHandleTop = 32;
    private readonly Grid _surface;
    private readonly Button _resumeButton;
    private readonly DispatcherQueueTimer _controlFadeTimer;
    private readonly DispatcherQueueTimer _handleMotionTimer;
    private readonly long _pressedChangedToken;
    private bool _handleHovered;
    private double _handleFromWidth, _handleFromHeight;
    private double _handleToWidth = 104, _handleToHeight = 18, _handleMotionDuration;
    private long _handleMotionStarted;
    private bool _restorePointerSequence, _restorePressInside;
    private int _pointerSequenceGeneration;
    private double _controlFrom, _controlTo;
    private long _controlStarted;
    private double _controlDuration;
    private ScreenRect _display;
    private OverlayOpacityFade? _fade;
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private bool _disposed;
    internal nint Handle { get; }
    internal event Action? ResumeRequested;
    internal event Action<ScreenPrivacyWindow, bool>? VisibilityTransitionCompleted;

    internal ScreenPrivacyWindow()
    {
        var root = new Grid();
        _surface = new Grid { Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 48, 48, 48)) };
        root.Children.Add(_surface);
        _resumeButton = new Button
        {
            Style = (Style)Application.Current.Resources["FullscreenRestoreHandleStyle"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, RestoreHandleTop, 0, 0),
            Opacity = 0,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        Localized.SetAutomationNameKey(_resumeButton, "ResumeUse");
        Localized.SetToolTipKey(_resumeButton, "ResumeUse");
        _resumeButton.Click += OnRestoreClick;
        _resumeButton.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            _pointerSequenceGeneration++;
            _restorePointerSequence = true;
            var point = args.GetCurrentPoint(_resumeButton).Position;
            _restorePressInside = InsideHandle(point.X, point.Y);
        }), true);
        _resumeButton.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => ClearPointerSequence()), true);
        _resumeButton.PointerCanceled += (_, _) => ClearPointerSequence();
        _resumeButton.PointerCaptureLost += (_, _) => ClearPointerSequence();
        root.Children.Add(_resumeButton);
        Content = root;
        Handle = WindowNative.GetWindowHandle(this);
        NativeMethods.RemoveWindowChrome(Handle);
        NativeMethods.TrySetCornerPreference(Handle, NativeMethods.DWMWCP_DONOTROUND);
        if (InitializeBackdrop()) _surface.Background = new SolidColorBrush(Colors.Transparent);
        _fade = new OverlayOpacityFade(Handle, DispatcherQueue);
        _fade.VisibilityTransitionCompleted += OnVisibilityTransitionCompleted;
        _controlFadeTimer = DispatcherQueue.CreateTimer();
        _controlFadeTimer.Interval = TimeSpan.FromMilliseconds(16);
        _controlFadeTimer.IsRepeating = true;
        _controlFadeTimer.Tick += OnControlFade;
        _handleMotionTimer = DispatcherQueue.CreateTimer();
        _handleMotionTimer.Interval = TimeSpan.FromMilliseconds(16);
        _handleMotionTimer.IsRepeating = true;
        _handleMotionTimer.Tick += OnHandleMotion;
        _resumeButton.PointerEntered += (_, _) => { _handleHovered = true; AnimateHandle(); };
        _resumeButton.PointerExited += (_, _) => { _handleHovered = false; AnimateHandle(); };
        _pressedChangedToken = _resumeButton.RegisterPropertyChangedCallback(Button.IsPressedProperty, (_, _) => AnimateHandle());
        Title = LocalizationService.Get("ScreenPrivacyTitle");
        Closed += (_, _) => Release();
    }

    internal void Cover(ScreenRect display)
    {
        PrivacyNative.PositionFullScreen(Handle, display, showWindow: false);
        _display = display;
    }
    internal double DisplayScale => Math.Max(96, NativeMethods.GetDpiForWindow(Handle)) / 96d;
    internal bool ControlPressed => _resumeButton.IsPressed;
    internal bool IsOverControl(NativeMethods.Point point)
    {
        double scale = DisplayScale;
        return _resumeButton.Visibility == Visibility.Visible && InsideHandle(
            (point.X - _display.Left) / scale - (_display.Width / scale - _resumeButton.ActualWidth) / 2,
            (point.Y - _display.Top) / scale - RestoreHandleTop);
    }

    private bool InsideHandle(double x, double y)
    {
        double width = _resumeButton.ActualWidth, height = _resumeButton.ActualHeight;
        if (x < 0 || x >= width || y < 0 || y >= height) return false;
        double radius = height / 2;
        double dx = x - Math.Clamp(x, radius, width - radius), dy = y - radius;
        return dx * dx + dy * dy <= radius * radius;
    }

    private void OnRestoreClick(object sender, RoutedEventArgs args)
    {
        bool allowed = !_restorePointerSequence || (_restorePressInside &&
            NativeMethods.GetCursorPos(out var point) && IsOverControl(point));
        _restorePointerSequence = false;
        if (allowed) ResumeRequested?.Invoke();
    }

    private void ClearPointerSequence()
    {
        int generation = _pointerSequenceGeneration;
        // Button raises Click during release; retain the exact down/up boundary check until then.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (generation == _pointerSequenceGeneration) _restorePointerSequence = false;
        });
    }

    internal void SetControlVisible(bool visible, bool animate = true)
    {
        if (_disposed) return;
        double target = visible ? 1 : 0;
        if (animate && _controlTo == target) return;
        _controlFadeTimer.Stop();
        _controlTo = target;
        _resumeButton.IsHitTestVisible = visible;
        if (!animate || !SystemEffectsAllowAnimation())
        {
            _resumeButton.Opacity = target;
            _resumeButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible) ResetHandleMotion();
            return;
        }
        _controlFrom = _resumeButton.Opacity;
        _controlDuration = Curtain.Core.Rendering.OverlayFadeCurve.DurationFor(_controlFrom * 255, target * 255);
        _controlStarted = Environment.TickCount64;
        _resumeButton.Visibility = Visibility.Visible;
        _controlFadeTimer.Start();
    }

    private void OnControlFade(DispatcherQueueTimer sender, object args)
    {
        double elapsed = Environment.TickCount64 - _controlStarted;
        _resumeButton.Opacity = Curtain.Core.Rendering.OverlayFadeCurve.Sample(_controlFrom, _controlTo, elapsed, _controlDuration);
        if (elapsed < _controlDuration) return;
        _controlFadeTimer.Stop();
        if (_controlTo == 0) { _resumeButton.Visibility = Visibility.Collapsed; ResetHandleMotion(); }
    }

    private void AnimateHandle()
    {
        if (_disposed) return;
        // Animate the actual Button bounds: no invisible padding outside the visible bar.
        _handleMotionTimer.Stop();
        _handleFromWidth = _resumeButton.Width;
        _handleFromHeight = _resumeButton.Height;
        bool pressed = _resumeButton.IsPressed;
        _handleToWidth = pressed ? 110 : _handleHovered ? 116 : 104;
        _handleToHeight = pressed ? 19 : _handleHovered ? 20 : 18;
        _handleMotionDuration = pressed ? 105 : 170;
        if (!SystemEffectsAllowAnimation())
        {
            _resumeButton.Width = _handleToWidth;
            _resumeButton.Height = _handleToHeight;
            _resumeButton.CornerRadius = new CornerRadius(_handleToHeight / 2);
            return;
        }
        _handleMotionStarted = Environment.TickCount64;
        _handleMotionTimer.Start();
    }

    private void OnHandleMotion(DispatcherQueueTimer sender, object args)
    {
        double elapsed = Environment.TickCount64 - _handleMotionStarted;
        _resumeButton.Width = Curtain.Core.Rendering.OverlayFadeCurve.Sample(_handleFromWidth, _handleToWidth, elapsed, _handleMotionDuration);
        _resumeButton.Height = Curtain.Core.Rendering.OverlayFadeCurve.Sample(_handleFromHeight, _handleToHeight, elapsed, _handleMotionDuration);
        _resumeButton.CornerRadius = new CornerRadius(_resumeButton.Height / 2);
        if (elapsed >= _handleMotionDuration) _handleMotionTimer.Stop();
    }

    private void ResetHandleMotion()
    {
        _handleMotionTimer.Stop();
        _handleHovered = false;
        _resumeButton.Width = 104;
        _resumeButton.Height = 18;
        _resumeButton.CornerRadius = new CornerRadius(9);
    }
    internal bool IsVisible => _fade?.IsVisible == true;
    internal void SetProtectedVisible(bool visible, bool animate, long? batchStart = null)
    {
        if (_disposed) return;
        if (!visible) SetControlVisible(false, animate: false);
        bool canAnimate = animate && _acrylic is not null && SystemEffectsAllowAnimation();
        _fade?.SetVisible(visible, canAnimate, batchStart);
    }

    internal void CancelAndHide() { SetControlVisible(false, animate: false); _fade?.CancelAndHide(); }
    private void OnVisibilityTransitionCompleted(bool visible) => VisibilityTransitionCompleted?.Invoke(this, visible);
    internal void RefreshLocalization()
    {
        Title = LocalizationService.Get("ScreenPrivacyTitle");
    }
    private void Release()
    {
        if (_disposed) return;
        _disposed = true;
        _controlFadeTimer.Stop();
        _controlFadeTimer.Tick -= OnControlFade;
        _handleMotionTimer.Stop();
        _handleMotionTimer.Tick -= OnHandleMotion;
        _resumeButton.UnregisterPropertyChangedCallback(Button.IsPressedProperty, _pressedChangedToken);
        if (_fade is not null) _fade.VisibilityTransitionCompleted -= OnVisibilityTransitionCompleted;
        _fade?.Dispose(); _fade = null;
        _acrylic?.Dispose(); _acrylic = null;
        _backdropConfiguration = null;
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
            CurtainDiagnostics.WriteException("fullscreen.backdrop.fallback", ex,
                stage: "system-backdrop", reasonCode: "HostBackdropFallback",
                hwnd: Handle.ToInt64());
            return false;
        }
    }

    internal void CloseOverlay() { if (NativeMethods.IsWindow(Handle)) Close(); Release(); }
}
