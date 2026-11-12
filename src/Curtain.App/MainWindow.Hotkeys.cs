using Curtain.App.Interop;
using Curtain.App.Localization;
using Curtain.App.Services;
using Curtain.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Curtain.App;

public sealed partial class MainWindow
{
    private bool _editingHotkey;
    private Action<GlobalHotkeySettings>? _captureRegisteredHotkey;

    private void InitializeSettingDescriptions()
    {
        foreach (var (target, key) in new[]
        {
            (AddWindowHintText, "AddWindowHint"), (StartupHintText, "StartupHint"),
            (LanguageHintText, "LanguageHint"), (AutoUpdateHintText, "AutoUpdateHint"),
            (IdleProtectHintText, "IdleProtectHint"), (IdleMinutesHintText, "IdleMinutesHint"),
            (IdleScopeHintText, "IdleScopeHint"), (ScreenHotkeyHintText, "ScreenHotkeyHint"),
            (HotkeyHintText, "HotkeyHint")
        }) Localized.SetText(target, key);
        Localized.SetContent(WebsiteLink, "WebsiteLink");
        AutomationProperties.SetName(EditHotkeyButton, LocalizationService.Get("EditHotkey"));
        ToolTipService.SetToolTip(EditHotkeyButton, LocalizationService.Get("EditHotkey"));
        AutomationProperties.SetName(EditPrivacyHotkeyButton, LocalizationService.Get("EditPrivacyHotkey"));
        ToolTipService.SetToolTip(EditPrivacyHotkeyButton, LocalizationService.Get("EditPrivacyHotkey"));
    }

    private static string KeyName(uint key) => key switch
    {
        0x20 => "Space",
        0x25 => "←",
        0x26 => "↑",
        0x27 => "→",
        0x28 => "↓",
        >= 0x70 and <= 0x87 => $"F{key - 0x6F}",
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)key).ToString(),
        >= 0x60 and <= 0x69 => $"Num {key - 0x60}",
        _ => ((VirtualKey)key).ToString()
    };

    private static StackPanel CreateKeycaps(GlobalHotkeySettings hotkey)
    {
        var caps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (string part in DescribeHotkey(hotkey).Split('+'))
        {
            var label = new TextBlock { Text = part, FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"] };
            caps.Children.Add(new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 5, 7, 5),
                Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"], Child = label });
        }
        AutomationProperties.SetName(caps, DescribeHotkey(hotkey));
        return caps;
    }

    private void RefreshHotkeyKeycaps()
    {
        HotkeyCapsHost.Children.Clear();
        HotkeyCapsHost.Children.Add(CreateKeycaps(_settings.Hotkey));
        HotkeyCapsHost.Opacity = _settings.Hotkey.Enabled ? 1 : 0.55;
    }

    private async void OnEditHotkey(object sender, RoutedEventArgs e) => await EditHotkeyAsync(false);
    private async void OnEditPrivacyHotkey(object sender, RoutedEventArgs e) => await EditHotkeyAsync(true);

    private void ApplyPrivacyHotkey()
    {
        _privacyHotkey.Apply(_settings.PrivacyHotkey); RefreshPrivacyHotkeyStatus();
    }

    private void ApplyPrivacyMouseButton()
    {
        _privacyMouseHook?.Dispose();
        _privacyMouseHook = null;
        if (_settings.PrivacyMouseButton == Curtain.Core.Protection.PrivacyMouseButton.Off) return;
        _privacyMouseHook = new(DispatcherQueue, _settings.PrivacyMouseButton, () => TogglePrivacyProtection("SideKey"));
        if (!_privacyMouseHook.IsInstalled)
            Localized.SetText(StatusText, "PrivacyMouseFailed");
    }

    private void OnPrivacyMouseChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || PrivacyMouseCombo.SelectedIndex < 0) return;
        var before = _settings;
        var selected = (Curtain.Core.Protection.PrivacyMouseButton)PrivacyMouseCombo.SelectedIndex;
        PrivacyMouseHook? candidate = selected == Curtain.Core.Protection.PrivacyMouseButton.Off
            ? null : new(DispatcherQueue, selected, () => TogglePrivacyProtection("SideKey"));
        string operationId = CurtainDiagnostics.NewOperationId();
        _settings = before with { PrivacyMouseButton = selected };
        if (candidate is { IsInstalled: false } || !SaveSettings(operationId))
        {
            candidate?.Dispose();
            _settings = before;
            _loadingSettings = true;
            PrivacyMouseCombo.SelectedIndex = (int)before.PrivacyMouseButton;
            _loadingSettings = false;
            Localized.SetText(StatusText, "PrivacyMouseFailed");
            return;
        }
        _privacyMouseHook?.Dispose();
        _privacyMouseHook = candidate;
        CurtainDiagnostics.Write("settings.preference.changed", operationId: operationId,
            stage: "PrivacyMouseButton", reasonCode: selected.ToString());
    }
    private void RefreshPrivacyHotkeyStatus()
    {
        PrivacyHotkeyCapsHost.Children.Clear();
        PrivacyHotkeyCapsHost.Children.Add(CreateKeycaps(_settings.PrivacyHotkey));
        PrivacyHotkeyStatusText.Visibility = _privacyHotkey.IsRegistered ? Visibility.Collapsed : Visibility.Visible;
        if (!_privacyHotkey.IsRegistered)
            PrivacyHotkeyStatusText.Text = LocalizationService.Get("HotkeyRegistrationFailed", DescribeHotkey(_settings.PrivacyHotkey), _privacyHotkey.LastError);
    }
    private async Task EditHotkeyAsync(bool privacy)
    {
        if (_editingHotkey) return;
        _editingHotkey = true;
        var previous = privacy ? _settings.PrivacyHotkey : _settings.Hotkey;
        var registration = privacy ? _privacyHotkey : _hotkey;
        var proposed = previous;
        var capture = new Button { Content = CreateKeycaps(proposed), MinHeight = 100,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        var hint = new TextBlock { Text = LocalizationService.Get("HotkeyCaptureHint"), TextWrapping = TextWrapping.Wrap };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var content = new StackPanel { Spacing = 20, MinWidth = 300 };
        content.Children.Add(hint); content.Children.Add(capture); content.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = LocalizationService.Get(privacy ? "EditPrivacyHotkey" : "EditHotkey"), Content = content,
            PrimaryButtonText = LocalizationService.Get("SaveHotkey"), SecondaryButtonText = LocalizationService.Get("ResetHotkey"),
            CloseButtonText = LocalizationService.Get("CancelHotkey"), DefaultButton = ContentDialogButton.Primary
        };
        _captureRegisteredHotkey = hotkey =>
        {
            proposed = hotkey with { Enabled = previous.Enabled };
            capture.Content = CreateKeycaps(proposed);
            dialog.IsPrimaryButtonEnabled = true;
            error.Visibility = Visibility.Collapsed;
        };
        capture.PreviewKeyDown += (_, args) =>
        {
            uint key = (uint)args.Key;
            if (args.Key is VirtualKey.Escape or VirtualKey.Tab) return;
            args.Handled = true;
            if (key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5)) return;
            uint modifiers = 0;
            if ((NativeMethods.GetAsyncKeyState(0x11) & 0x8000) != 0) modifiers |= HotkeyModifier.Control;
            if ((NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0) modifiers |= HotkeyModifier.Shift;
            if ((NativeMethods.GetAsyncKeyState(0x12) & 0x8000) != 0) modifiers |= HotkeyModifier.Alt;
            if (((NativeMethods.GetAsyncKeyState(0x5B) | NativeMethods.GetAsyncKeyState(0x5C)) & 0x8000) != 0) modifiers |= HotkeyModifier.Windows;
            proposed = new GlobalHotkeySettings(previous.Enabled, modifiers, key);
            capture.Content = CreateKeycaps(proposed);
            bool valid = CurtainUserSettingsStore.IsValidHotkey(proposed);
            dialog.IsPrimaryButtonEnabled = valid;
            error.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
            error.Text = valid ? "" : LocalizationService.Get("HotkeyInvalid");
        };
        dialog.Opened += (_, _) => capture.Focus(FocusState.Programmatic);
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            proposed = (privacy ? new CurtainUserSettings().PrivacyHotkey : new GlobalHotkeySettings()) with { Enabled = previous.Enabled };
            capture.Content = CreateKeycaps(proposed); error.Visibility = Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = true; capture.Focus(FocusState.Programmatic);
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            string operationId = CurtainDiagnostics.NewOperationId();
            string? problem = null;
            var other = privacy ? _settings.Hotkey : _settings.PrivacyHotkey;
            if (!CurtainUserSettingsStore.IsValidHotkey(proposed)) problem = LocalizationService.Get("HotkeyInvalid");
            else if (CurtainUserSettingsStore.IsSameShortcut(proposed, other)) problem = LocalizationService.Get("DuplicateHotkeys");
            var before = _settings;
            bool saved = false;
            if (problem is null && !registration.TryApply(proposed, () =>
            {
                _settings = privacy ? before with { PrivacyHotkey = proposed } : before with { Hotkey = proposed };
                saved = SaveSettings(operationId);
                if (!saved) _settings = before;
                return saved;
            }))
            {
                CurtainDiagnostics.Write("hotkey.registration.failed", CurtainDiagnosticLevel.Warning,
                    operationId: operationId, stage: privacy ? "privacy-hotkey" : "picker-hotkey",
                    reasonCode: "RegistrationRejected", errorCode: registration.LastError,
                    metricName: "VirtualKey", metricValue: proposed.VirtualKey);
                problem = registration.LastError != 0
                    ? LocalizationService.Get("HotkeyRegistrationFailed", DescribeHotkey(proposed), registration.LastError)
                    : LocalizationService.Get("SettingsSaveFailed");
            }
            if (problem is not null)
            {
                args.Cancel = true; error.Text = problem; error.Visibility = Visibility.Visible; return;
            }
            if (saved)
            {
                string settingPrefix = privacy ? "PrivacyHotkey" : "Hotkey";
                CurtainDiagnostics.Write("settings.preference.changed", operationId: operationId,
                    stage: settingPrefix + ".Modifiers", reasonCode: "Updated",
                    metricName: "Modifiers", metricValue: proposed.Modifiers);
                CurtainDiagnostics.Write("settings.preference.changed", operationId: operationId,
                    stage: settingPrefix + ".VirtualKey", reasonCode: "Updated",
                    metricName: "VirtualKey", metricValue: proposed.VirtualKey);
                return;
            }
        };
        try { await dialog.ShowAsync(); }
        finally
        {
            _editingHotkey = false;
            _captureRegisteredHotkey = null;
            if (!_disposed) { ApplyHotkey(); ApplyPrivacyHotkey(); }
        }
    }
}
