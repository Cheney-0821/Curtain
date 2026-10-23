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
using WinRT;
using WinRT.Interop;
using Windows.UI.ViewManagement;

namespace Curtain.App.Windows;

/// <summary>One non-activating topmost overlay per display, with an intentional resume button.</summary>
internal sealed class ScreenPrivacyWindow : Window
{
    private readonly Grid _surface;
    private readonly TextBlock _titleText;
    private readonly Button _resumeButton;
    private OverlayOpacityFade? _fade;
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private bool _disposed;
    internal nint Handle { get; }
    internal event Action? ResumeRequested;

    internal ScreenPrivacyWindow()
    {
        var root = new Grid();
        _surface = new Grid { Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 48, 48, 48)) };
        root.Children.Add(_surface);
        var bar = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Spacing = 10 };
        _titleText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 15,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
        Localized.SetText(_titleText, "ScreenPrivacyTitle");
        _resumeButton = new Button { MinWidth = 104 };
        Localized.SetContent(_resumeButton, "ResumeUse");
        Localized.SetAutomationNameKey(_resumeButton, "ResumeUse");
        _resumeButton.Click += (_, _) => ResumeRequested?.Invoke();
        bar.Children.Add(_titleText);
        bar.Children.Add(_resumeButton);
        var pill = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 24, 0, 0),
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(220, 28, 36, 48)),
            Child = bar
        };
        root.Children.Add(pill);
        Content = root;
        Handle = WindowNative.GetWindowHandle(this);
        NativeMethods.RemoveWindowChrome(Handle);
        NativeMethods.TrySetCornerPreference(Handle, NativeMethods.DWMWCP_DONOTROUND);
        if (InitializeBackdrop()) _surface.Background = new SolidColorBrush(Colors.Transparent);
        _fade = new OverlayOpacityFade(Handle, DispatcherQueue);
        Title = LocalizationService.Get("ScreenPrivacyTitle");
        Closed += (_, _) => Release();
    }

    internal void Cover(ScreenRect display) => PrivacyNative.PositionFullScreen(Handle, display, showWindow: false);
    internal bool IsVisible => _fade?.IsVisible == true;
    internal void SetProtectedVisible(bool visible, bool animate, long? batchStart = null)
    {
        if (_disposed) return;
        bool canAnimate = animate && _acrylic is not null && SystemEffectsAllowAnimation();
        _fade?.SetVisible(visible, canAnimate, batchStart);
    }

    internal void CancelAndHide() => _fade?.CancelAndHide();
    internal void RefreshLocalization()
    {
        Title = LocalizationService.Get("ScreenPrivacyTitle");
    }
    private void Release()
    {
        if (_disposed) return;
        _disposed = true;
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
            DevelopmentDiagnostics.Write("backdrop-host-fallback", new { exception = ex.GetType().Name, ex.HResult });
            return false;
        }
    }

    internal void CloseOverlay() { if (NativeMethods.IsWindow(Handle)) Close(); Release(); }
}
