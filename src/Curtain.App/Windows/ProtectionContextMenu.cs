using System.Runtime.InteropServices;
using Curtain.App.Interop;
using Curtain.App.Localization;
using Curtain.Core.Geometry;
using Curtain.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinRT.Interop;

namespace Curtain.App.Windows;

internal sealed class ProtectionContextMenu
{
    private const long LayeredWindowStyle = 0x00080000;
    private const uint LayeredAlpha = 0x00000002;
    private readonly Window _window = new() { Title = "Curtain" };
    private readonly Grid _anchor = new();
    private readonly MenuFlyout _menu = new();
    private readonly nint _hwnd;
    private readonly nint _previousForeground;
    private readonly Action _dismissed;
    private Action? _selection;
    private bool _showFine;
    private bool _closed;

    internal ProtectionContextMenu(WindowProtectionSettings settings,
        Action draw, Action<WindowProtectionSettings> configure, Action remove, Action dismissed)
    {
        _dismissed = dismissed;
        _previousForeground = NativeMethods.GetForegroundWindow();
        _window.Content = _anchor;
        _hwnd = WindowNative.GetWindowHandle(_window);
        _window.AppWindow.IsShownInSwitchers = false;
        NativeMethods.RemoveWindowChrome(_hwnd);
        long style = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        style &= ~NativeMethods.WS_EX_NOACTIVATE;
        // The XAML anchor needs a visible HWND. Its pixels are separate from the popup's.
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, (nint)(style | LayeredWindowStyle));
        if (!SetLayeredWindowAttributes(_hwnd, 0, 0, LayeredAlpha))
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, (nint)style);

        _menu.Items.Add(Command("ClearRegionDrawMenu", "\uE7A8", draw));
        var blur = new MenuFlyoutSubItem { Text = LocalizationService.Get("BlurStrength"), Icon = Icon("\uE71C") };
        AddBlurLevel(blur, "BlurFine", "\uE86C", settings.GentleAmount, settings, configure);
        AddBlurLevel(blur, "BlurMedium", "\uE86D", 10, settings, configure);
        AddBlurLevel(blur, "BlurHeavy", "\uE86E", 20, settings, configure);
        blur.Items.Add(new MenuFlyoutSeparator());
        var fine = new MenuFlyoutItem { Text = LocalizationService.Get("FineBlurTitle"), Icon = Icon("\uE70F") };
        fine.Click += (_, _) => _showFine = true;
        blur.Items.Add(fine);
        _menu.Items.Add(blur);

        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(Command("RemoveWindowProtection", "\uE74D", remove));
        _menu.Closed += (_, _) => _window.DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            if (_showFine) ShowFineAdjustment(settings, configure);
            else Finish(_selection);
        });
    }

    internal void Show(nint owner, PixelPoint point, Action opened)
    {
        if (!NativeMethods.SetOverlayOwner(_hwnd, owner) ||
            !NativeMethods.SetWindowPos(_hwnd, 0, point.X, point.Y, 2, 2,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE))
            throw new InvalidOperationException("Could not position the protection menu.");

        _anchor.Loaded += (_, _) =>
        {
            if (_closed) return;
            try
            {
                _menu.ShowAt(_anchor, new global::Windows.Foundation.Point(0, 0));
                opened();
            }
            catch (Exception ex)
            {
                Services.CurtainDiagnostics.WriteException("overlay.protection-menu.failed", ex, stage: "show");
                Dismiss();
            }
        };
        _window.Activate();
    }

    internal void Dismiss() => Finish(null);

    private MenuFlyoutItem Command(string label, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = LocalizationService.Get(label), Icon = Icon(glyph) };
        item.Click += (_, _) => _selection = action;
        return item;
    }

    private void AddBlurLevel(MenuFlyoutSubItem parent, string label, string glyph, double amount,
        WindowProtectionSettings settings, Action<WindowProtectionSettings> configure)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = LocalizationService.Get(label),
            Icon = Icon(glyph),
            IsChecked = amount == settings.BlurAmount,
            GroupName = "BlurStrength"
        };
        item.Click += (_, _) =>
        {
            if (amount != settings.BlurAmount)
                _selection = () => configure(settings with { BlurAmount = amount });
        };
        parent.Items.Add(item);
    }

    private void ShowFineAdjustment(WindowProtectionSettings settings, Action<WindowProtectionSettings> configure)
    {
        var slider = new Slider
        {
            Minimum = 0, Maximum = 3, StepFrequency = 0.01, SmallChange = 0.01,
            Value = settings.GentleAmount, Width = 212, IsThumbToolTipEnabled = false
        };
        string title = LocalizationService.Get("FineBlurTitle");
        AutomationProperties.SetName(slider, title);
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = title });
        panel.Children.Add(slider);
        slider.ValueChanged += (_, _) => configure(settings with { BlurAmount = slider.Value, GentleAmount = slider.Value });
        var flyout = new Flyout { Content = panel, ShouldConstrainToRootBounds = false };
        flyout.Closed += (_, _) => _window.DispatcherQueue.TryEnqueue(Dismiss);
        flyout.ShowAt(_anchor, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
    }

    private void Finish(Action? action)
    {
        if (_closed) return;
        _closed = true;
        try
        {
            // A command can destroy the overlay that owns this window.
            if (action is not null) NativeMethods.SetOverlayOwner(_hwnd, 0);
            _dismissed();
            action?.Invoke();
        }
        finally
        {
            if (NativeMethods.GetForegroundWindow() == _hwnd && NativeMethods.IsWindow(_previousForeground))
                NativeMethods.SetForegroundWindow(_previousForeground);
            _window.Close();
        }
    }

    private static FontIcon Icon(string glyph) => new() { Glyph = glyph };

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);
}
