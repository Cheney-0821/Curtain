using CommunityToolkit.WinUI.Controls;
using Curtain.App.Localization;
using Curtain.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Curtain.App.Controls;

internal sealed class WindowProtectionControl : StackPanel
{
    private readonly Slider _fine = new()
    { Minimum = 0, Maximum = 3, StepFrequency = 0.01, SmallChange = 0.01, Width = 240, IsThumbToolTipEnabled = false };
    private readonly ToggleSwitch _passthrough = new() { MinWidth = 0 };
    private readonly SettingsCard _strengthCard;
    private readonly SettingsCard _inputCard;
    private readonly Flyout _fineFlyout;
    private readonly ComboBox _levels = new() { MinWidth = 104, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _fineButton = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<ComboBoxItem, string> _options = new();
    private readonly TextBlock _fineTitle = new();
    private readonly TextBlock _clearLabel = new();
    private readonly TextBlock _blurLabel = new();
    private readonly TextBlock _fineHint = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 240, Opacity = 0.72 };
    private bool _updating;
    internal WindowProtectionSettings Settings { get; private set; }
    internal event Action<WindowProtectionSettings>? Changed;

    internal WindowProtectionControl(WindowProtectionSettings settings)
    {
        Settings = WindowBlurScale.Normalize(settings);
        Spacing = 4;
        var finePanel = new StackPanel { Spacing = 12 };
        _fineTitle.Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"];
        finePanel.Children.Add(_fineTitle); finePanel.Children.Add(_fine);
        var endpoints = new Grid();
        endpoints.Children.Add(_clearLabel);
        _blurLabel.HorizontalAlignment = HorizontalAlignment.Right; endpoints.Children.Add(_blurLabel);
        finePanel.Children.Add(endpoints); finePanel.Children.Add(_fineHint);
        _fineFlyout = new Flyout { Content = finePanel, Placement = FlyoutPlacementMode.Bottom };
        var selector = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var key in new[] { "BlurFine", "BlurMedium", "BlurHeavy" })
        {
            var item = new ComboBoxItem();
            _options.Add(item, key); _levels.Items.Add(item);
        }
        selector.Children.Add(_fineButton); selector.Children.Add(_levels);
        _levels.SelectionChanged += (_, _) =>
        {
            if (!_updating) Publish(Settings with { BlurAmount = _levels.SelectedIndex == 0 ? Settings.GentleAmount : _levels.SelectedIndex == 1 ? 10 : 20 });
        };
        _fineButton.Click += (_, _) => _fineFlyout.ShowAt(_fineButton);
        _strengthCard = new SettingsCard { Content = selector, HeaderIcon = new FontIcon { Glyph = "\uE71C", FontSize = 20 } };
        _inputCard = new SettingsCard { Content = _passthrough, HeaderIcon = new FontIcon { Glyph = "\uE962", FontSize = 20 } };
        Children.Add(_strengthCard); Children.Add(_inputCard);
        _fine.ValueChanged += (_, _) =>
        {
            if (!_updating) Publish(Settings with { BlurAmount = _fine.Value, GentleAmount = _fine.Value });
        };
        _passthrough.Toggled += (_, _) => { if (!_updating) Publish(Settings with { AllowClickThrough = _passthrough.IsOn }); };
        RefreshLanguage(); SetSettings(settings);
    }
    internal void RefreshLanguage()
    {
        foreach (var pair in _options)
        {
            pair.Key.Content = LocalizationService.Get(pair.Value);
            AutomationProperties.SetName(pair.Key, LocalizationService.Get(pair.Value));
        }
        _fineButton.Content = LocalizationService.Get("FineTuneBlur");
        AutomationProperties.SetName(_fineButton, LocalizationService.Get("FineBlurTitle"));
        _strengthCard.Header = LocalizationService.Get("BlurStrength");
        _inputCard.Header = LocalizationService.Get("WorkingPassthrough");
        _strengthCard.Description = LocalizationService.Get("BlurStrengthDescription");
        _inputCard.Description = LocalizationService.Get("ClickThroughDescription");
        AutomationProperties.SetName(_levels, LocalizationService.Get("BlurStrength"));
        _passthrough.OnContent = LocalizationService.Get("ToggleOn");
        _passthrough.OffContent = LocalizationService.Get("ToggleOff");
        AutomationProperties.SetName(_passthrough, LocalizationService.Get("WorkingPassthrough"));
        AutomationProperties.SetName(_fine, LocalizationService.Get("FineBlurTitle"));
        _fineTitle.Text = LocalizationService.Get("FineBlurTitle");
        _clearLabel.Text = LocalizationService.Get("FineBlurClear");
        _blurLabel.Text = LocalizationService.Get("FineBlurMore");
        _fineHint.Text = LocalizationService.Get("FineBlurHint");
        _updating = true; UpdateSelection(); _updating = false;
    }
    internal IReadOnlyList<SettingsCard> TakeSettingsCards()
    {
        Children.Clear();
        return new[] { _strengthCard, _inputCard };
    }
    internal void SetEditable(bool editable)
    {
        _strengthCard.IsEnabled = _inputCard.IsEnabled = editable;
        if (!editable) _fineFlyout.Hide();
    }
    internal void SetSettings(WindowProtectionSettings settings)
    {
        _updating = true;
        Settings = WindowBlurScale.Normalize(settings);
        _fine.Value = Settings.GentleAmount;
        _passthrough.IsOn = Settings.AllowClickThrough;
        UpdateSelection(); _updating = false;
    }
    private void UpdateSelection()
    {
        bool gentle = Settings.BlurAmount <= 3;
        _levels.SelectedIndex = gentle ? 0 : Settings.BlurAmount == 10 ? 1 : 2;
        _fineButton.Visibility = gentle ? Visibility.Visible : Visibility.Collapsed;
        if (!gentle) _fineFlyout.Hide();
    }
    private void Publish(WindowProtectionSettings settings) { SetSettings(settings); Changed?.Invoke(Settings); }
}
