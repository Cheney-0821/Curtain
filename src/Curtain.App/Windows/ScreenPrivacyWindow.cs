using Curtain.App.Interop;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT;
using WinRT.Interop;

namespace Curtain.App.Windows;

/// <summary>One non-activating topmost overlay per display, with an intentional resume button.</summary>
internal sealed class ScreenPrivacyWindow : Window
{
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _configuration;
    private bool _disposed;
    internal nint Handle { get; }
    internal event Action? ResumeRequested;

    internal ScreenPrivacyWindow()
    {
        var root = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        var bar = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Spacing = 10 };
        bar.Children.Add(new TextBlock { Text = "Curtain · 全屏隐私保护",
            VerticalAlignment = VerticalAlignment.Center, FontSize = 15, Foreground = new SolidColorBrush(Colors.White) });
        var button = new Button { Content = "恢复使用", MinWidth = 104 };
        button.Click += (_, _) => ResumeRequested?.Invoke();
        bar.Children.Add(button);
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

        DesktopAcrylicController? candidate = null;
        try
        {
            if (!DesktopAcrylicController.IsSupported()) throw new NotSupportedException();
            DispatcherQueue.EnsureSystemDispatcherQueue();
            var config = new SystemBackdropConfiguration { IsInputActive = true, Theme = SystemBackdropTheme.Light };
            candidate = new DesktopAcrylicController
            {
                Kind = DesktopAcrylicKind.Base,
                TintColor = Colors.White, TintOpacity = 0, LuminosityOpacity = 0,
                FallbackColor = global::Windows.UI.Color.FromArgb(255, 48, 48, 48)
            };
            if (!candidate.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>()))
                throw new InvalidOperationException("系统磨砂控制器未能绑定窗口。");
            candidate.SetSystemBackdropConfiguration(config);
            _configuration = config;
            _acrylic = candidate;
        }
        catch
        {
            candidate?.Dispose(); _acrylic = null; _configuration = null;
            // Fail closed: an opaque layer, never an unprotected transparent window.
            root.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 48, 48, 48));
        }
        Closed += (_, _) => Release();
        AppWindow.Show(false);
        NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);
    }

    internal void Cover(ScreenRect display) => PrivacyNative.PositionFullScreen(Handle, display);
    private void Release()
    {
        if (_disposed) return;
        _disposed = true;
        _acrylic?.Dispose(); _acrylic = null; _configuration = null;
    }
    internal void CloseOverlay() { if (NativeMethods.IsWindow(Handle)) Close(); Release(); }
}
