using Curtain.App.Interop;
using Curtain.App.Localization;
using Curtain.App.Services;
using Curtain.Core.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace Curtain.App;

public sealed partial class MainWindow : Window
{
    private readonly CurtainManager _manager;
    private readonly ScreenPrivacyManager _screenPrivacy;
    private readonly IdleProtectionMonitor _idleMonitor;
    private bool _manualScreenActive;
    private AutoProtectionScope? _idleScope;
    private const int PrivacyHotkeyId = 0x4353;
    private bool _privacyHotkeyRegistered;
    private readonly WindowPicker _picker;
#if DEBUG
    private readonly CurtainUserSettingsStore _settingsStore = new(
        Environment.GetEnvironmentVariable("CURTAIN_TEST_SETTINGS_DIR"));
#else
    private readonly CurtainUserSettingsStore _settingsStore = new();
#endif
    private readonly WindowMessageHost _messageHost;
    private readonly GlobalHotkey _hotkey;
    private readonly TrayIcon _tray;
    private CurtainUserSettings _settings;
    private readonly LocalUpdateService _updateService = new();
    private readonly BackgroundUpdateService _backgroundUpdates;
    private readonly DispatcherQueueTimer _autoUpdateTimer;
    private readonly CancellationTokenSource _updateShutdown = new();
    private bool _updateInProgress;
    private bool _automaticUpdateInProgress;
    private bool _loadingSettings = true;
    private bool _allowClose;
    private bool _disposed;

    public MainWindow()
    {
        InitializeComponent();
        var loaded = _settingsStore.Load();
        _settings = loaded.Settings;
        LocalizationService.SetLanguage(_settings.Language);
        InitializeLocalizedControls();
        UpdateWindowTitle();
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Curtain.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        UpdateAppVersion();
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(700, 600));

        _backgroundUpdates = new BackgroundUpdateService(_updateService);
        _autoUpdateTimer = DispatcherQueue.CreateTimer();
        _autoUpdateTimer.IsRepeating = false;
        _autoUpdateTimer.Tick += OnAutoUpdateTimer;

        _manager = new(DispatcherQueue);
        _screenPrivacy = new(DispatcherQueue);
        _screenPrivacy.ResumeRequested += OnResumeScreenRequested;
        WindowList.ItemsSource = _manager.Windows;
        _manager.CollectionChanged += UpdateSummary;
        _manager.Notice += OnNotice;

        nint handle = WindowNative.GetWindowHandle(this);
        _picker = new(_manager, DispatcherQueue, handle);
        _picker.PreviewChanged += OnPickerPreviewChanged;
        _picker.Finished += OnPickFinished;
        _picker.HandoffCompleted += OnPickerHandoffCompleted;

        _messageHost = new(handle);
        _messageHost.Message += OnWindowMessage;
        // Separate id from the configurable window-selection hotkey.
        _privacyHotkeyRegistered = NativeMethods.RegisterHotKey(handle, PrivacyHotkeyId,
            HotkeyModifier.Control | HotkeyModifier.Shift | 0x4000, 0x79); // Ctrl+Shift+F10
        _hotkey = new(handle);
        _hotkey.Pressed += StartPickerFromHotkey;
        _tray = new(handle)
        {
            AllProtectionEnabled = () => _manager.Windows.Count > 0 && _manager.Windows.All(item => item.Session.Target.IsClosed)
        };
        _tray.OpenRequested += ShowFromTray;
        _tray.ToggleProtectionRequested += ToggleAllProtection;
        _tray.ExitRequested += ExitCurtain;
        LocalizationService.LanguageChanged += OnLanguageChanged;

        AppWindow.Closing += OnWindowClosing;
        Closed += OnWindowClosed;

        UpdateLocalizedScopeItems();
        ApplySettingsToControls();
        Localized.RefreshAll();
        RefreshLocalizedOptionControls();
        _loadingSettings = false;
        ApplyHotkey();
        // Upgrade from a development path to Velopack's stable launcher on first installed start.
        if (_settings.StartWithWindows)
        {
            string? startupError = StartupRegistration.SetEnabled(true);
            if (startupError is not null) Localized.SetText(SettingsStatusText, startupError);
        }
        if (loaded.RecoveredFromInvalidFile)
        {
            Localized.SetText(SettingsStatusText, "SettingsReadFailed");
            SaveSettings();
        }
        Navigation.SelectedItem = HomeItem;
        UpdateSummary();
        SaveSettings(); // Rewrite any older settings without session-only window rules.
        if (_backgroundUpdates.ReadyVersion is { } ready)
            MarkUpdateReady(ready);
        StartAutomaticUpdateTimer(initial: true);
        _idleMonitor = new(DispatcherQueue, () => _settings.AutoProtection,
            () => _idleScope.HasValue, ActivateIdleProtection,
            () => { if (_screenPrivacy.HasOverlays) _screenPrivacy.RefreshDisplays(); },
            () => { if (_idleScope == AutoProtectionScope.AddedWindows)
                _manager.ReapplyIdleForcedClosed(); });
        if (!_privacyHotkeyRegistered)
            Localized.SetText(SettingsStatusText, "PrivacyHotkeyUnavailable");
    }

    internal void StartHidden() => AppWindow.Hide();

    internal void ShowFromTray()
    {
        if (_disposed) return;
        AppWindow.Show();
        if (NativeMethods.IsIconic(WindowNative.GetWindowHandle(this)))
            NativeMethods.ShowWindow(WindowNative.GetWindowHandle(this), NativeMethods.SW_RESTORE);
        Activate();
    }

    private void OnWindowMessage(uint message, nint wParam, nint lParam)
    {
        if (message == 0x0011) _allowClose = true; // WM_QUERYENDSESSION
        else if (message == 0x0016 && wParam == 0) _allowClose = false; // WM_ENDSESSION cancelled
        if (message == NativeMethods.WM_HOTKEY && wParam.ToInt32() == PrivacyHotkeyId)
        { OnPrivacyHotkey(); return; }
        if (_hotkey.HandleMessage(message, wParam)) return;
        if (_tray.HandleMessage(message, wParam, lParam)) return;
    }

    private void OnSettingsRowSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is not Grid row || row.Children.Count != 2) return;
        bool stacked = args.NewSize.Width < 440;
        var control = (FrameworkElement)row.Children[1];
        Grid.SetColumn(control, stacked ? 0 : 1);
        Grid.SetRow(control, stacked ? 1 : 0);
        control.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        control.Margin = stacked ? new Thickness(0, 10, 0, 0) : new Thickness(0);
        if (control is StackPanel keys)
            keys.Orientation = args.NewSize.Width < 340 ? Orientation.Vertical : Orientation.Horizontal;
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string section = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "home";
        HomePage.Visibility = section == "home" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = section == "settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = section == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLanguageChanged()
    {
        if (_disposed) return;
        Localized.RefreshAll();
        RefreshLocalizedOptionControls();
        UpdateWindowTitle();
        UpdateAppVersion();
        UpdateLocalizedScopeItems();
        _tray.RefreshLocalization();
        _screenPrivacy.RefreshLocalization();
        UpdateScreenButtons();
        UpdateSummary();
        UpdateHotkeyStatus();
    }

    private void InitializeLocalizedControls()
    {
        Localized.SetContent(HomeItem, "NavigationHome");
        Localized.SetContent(SettingsItem, "NavigationSettings");
        Localized.SetContent(AboutItem, "NavigationAbout");
        Localized.SetText(HomeTitleText, "HomeTitle");
        Localized.SetText(HomeDescriptionText, "HomeDescription");
        Localized.SetText(AddWindowTitleText, "AddWindowTitle");
        Localized.SetText(AddWindowDescriptionText, "AddWindowDescription");
        Localized.SetContent(PickButton, "AddWindowButton");
        Localized.SetAutomationName(PickButton, "AddWindowButton");
        Localized.SetContent(ScreenPrivacyButton, "ProtectScreenButton");
        Localized.SetAutomationName(ScreenPrivacyButton, "ProtectScreenButton");
        Localized.SetContent(ResumeAutoButton, "EndAutoProtectionButton");
        Localized.SetText(CandidateText, "PickerHelp");
        Localized.SetTitle(BulkActionsItem, "BulkActions");
        Localized.SetContent(ProtectAllMenuItem, "ProtectAll");
        Localized.SetContent(PauseAllMenuItem, "PauseAll");
        Localized.SetText(EmptyText, "EmptyWindows");
        Localized.SetText(StatusText, "Ready");
        Localized.SetText(SettingsTitleText, "SettingsTitle");
        Localized.SetText(GeneralTitleText, "GeneralTitle");
        Localized.SetText(GeneralDescriptionText, "GeneralDescription");
        Localized.SetText(StartupLabelText, "StartupHeader");
        Localized.SetAutomationName(StartupSwitch, "StartupHeader");
        Localized.SetText(AutoUpdateLabelText, "AutoUpdateHeader");
        Localized.SetAutomationName(AutoUpdateSwitch, "AutoUpdateHeader");
        Localized.SetText(LanguageLabelText, "LanguageHeader");
        Localized.SetAutomationName(LanguageCombo, "LanguageHeader");
        SetComboItemContent(LanguageCombo, "LanguageSystem", "LanguageEnglish", "LanguageChinese");
        Localized.SetText(AutoProtectionTitleText, "AutoProtectionTitle");
        Localized.SetText(AutoProtectionDescriptionText, "AutoProtectionDescription");
        Localized.SetText(IdleProtectLabelText, "EnableIdleProtection");
        Localized.SetAutomationName(IdleProtectSwitch, "EnableIdleProtection");
        Localized.SetText(IdleMinutesLabelText, "IdleDurationHeader");
        Localized.SetAutomationName(IdleMinutesCombo, "IdleDurationHeader");
        SetComboItemContent(IdleMinutesCombo, "IdleMinute1", "IdleMinute3", "IdleMinute5", "IdleMinute10");
        Localized.SetText(AutoProtectionScopeLabelText, "AutoProtectionScopeLabel");
        Localized.SetAutomationName(IdleScopeButtons, "AutoProtectionScopeLabel");
        Localized.SetText(AutoProtectionHotkeyHintText, "AutoProtectionHotkeyHint");
        Localized.SetText(HotkeyTitleText, "HotkeyTitle");
        Localized.SetText(HotkeyEnabledLabelText, "EnableHotkeyHeader");
        Localized.SetAutomationName(HotkeyEnabledSwitch, "EnableHotkeyHeader");
        Localized.SetText(HotkeyKeyLabelText, "HotkeyKeyHeader");
        Localized.SetAutomationName(HotkeyKeyCombo, "HotkeyKeyHeader");
        Localized.SetText(AboutTitleText, "AboutTitle");
        Localized.SetText(AboutDescriptionText, "AboutDescription");
        Localized.SetText(UpdatesTitleText, "UpdatesTitle");
        Localized.SetText(UpdatesDescriptionText, "UpdatesDescription");
        Localized.SetContent(UpdateActionButton, "CheckUpdates");
        Localized.SetText(UpdateStatusText, "UpdateNotChecked");
    }

    private static void SetComboItemContent(ComboBox comboBox, params string[] keys)
    {
        var items = comboBox.Items.OfType<ComboBoxItem>().ToArray();
        for (int index = 0; index < Math.Min(items.Length, keys.Length); index++)
            Localized.SetContent(items[index], keys[index]);
    }

    private void RefreshLocalizedOptionControls()
    {
        SetComboItemContent(LanguageCombo, "LanguageSystem", "LanguageEnglish", "LanguageChinese");
        SetComboItemContent(IdleMinutesCombo, "IdleMinute1", "IdleMinute3", "IdleMinute5", "IdleMinute10");

        string on = LocalizationService.Get("ToggleOn");
        string off = LocalizationService.Get("ToggleOff");
        StartupSwitch.OnContent = on;
        StartupSwitch.OffContent = off;
        AutoUpdateSwitch.OnContent = on;
        AutoUpdateSwitch.OffContent = off;
        IdleProtectSwitch.OnContent = on;
        IdleProtectSwitch.OffContent = off;
        HotkeyEnabledSwitch.OnContent = on;
        HotkeyEnabledSwitch.OffContent = off;

        bool wasLoading = _loadingSettings;
        _loadingSettings = true;
        RefreshSelectedComboBox(LanguageCombo);
        RefreshSelectedComboBox(IdleMinutesCombo);
        _loadingSettings = wasLoading;
    }

    private static void RefreshSelectedComboBox(ComboBox comboBox)
    {
        object? selectedItem = comboBox.SelectedItem;
        if (selectedItem is null) return;
        comboBox.SelectedItem = null;
        comboBox.SelectedItem = selectedItem;
    }

    private void OnRemoveButtonLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Button button) return;
        Localized.SetContent(button, "RemoveProtection");
        Localized.SetAutomationName(button, "RemoveProtection");
    }

    private void UpdateWindowTitle() => Title = LocalizationService.Get("WindowTitle");

    private void UpdateAppVersion()
    {
        string version = _updateService.InstalledVersion;
        if (version == "开发版") version = LocalizationService.Get("DevelopmentBuild");
        Localized.SetText(AppVersionText, "AppVersion", version);
    }

    private void UpdateLocalizedScopeItems()
    {
        bool wasLoading = _loadingSettings;
        _loadingSettings = true;
        int selected = IdleScopeButtons.SelectedIndex;
        IdleScopeButtons.ItemsSource = new[]
        {
            LocalizationService.Get("ScopeAddedWindows"),
            LocalizationService.Get("ScopeAllDisplays")
        };
        IdleScopeButtons.SelectedIndex = selected is 0 or 1 ? selected : 0;
        _loadingSettings = wasLoading;
    }

    private void OnPickerPreviewChanged(LocalizationMessage message) =>
        Localized.SetText(CandidateText, message.Key, message.Arguments);

    private void OnPickClick(object sender, RoutedEventArgs e) => StartPicker(WindowNative.GetWindowHandle(this));

    private void StartPicker(nint expectedForeground)
    {
        if (_picker.IsActive) return;
        try
        {
            _picker.Start(expectedForeground);
            PickButton.IsEnabled = false;
            Localized.SetContent(PickButton, "PickerInProgress");
            UpdateSummary();
        }
        catch (Exception)
        {
            OnNotice("UnableStartPicker");
        }
    }

    private void StartPickerFromHotkey()
    {
        if (_picker.IsActive) return;
        nint foreground = NativeMethods.GetForegroundWindow();
        if (foreground == nint.Zero || foreground == WindowNative.GetWindowHandle(this))
        {
            OnNotice("SelectAppBeforeHotkey");
            return;
        }
        try
        {
            _picker.Start(foreground);
            PickButton.IsEnabled = false;
            Localized.SetContent(PickButton, "PickerInProgress");
            UpdateSummary();
        }
        catch (Exception) { OnNotice("UnableStartPicker"); }
    }

    private CurtainSession? OnPickFinished(nint target, LocalizationMessage message)
    {
        PickButton.IsEnabled = true;
        Localized.SetContent(PickButton, "AddWindowButton");
        Localized.SetText(CandidateText, message.Key, message.Arguments);
        CurtainSession? session = null;
        if (target != nint.Zero)
        {
            try
            {
                var item = _manager.Attach(target);
                WindowList.SelectedItem = item;
                OnNotice("WindowAdded");
                session = item.Session;
            }
            catch (InvalidOperationException) { OnNotice("WindowNotSelectable"); }
            catch (Exception) { OnNotice("WindowNotAdded"); }
        }
        UpdateSummary();
        return session;
    }

    private void OnPickerHandoffCompleted() => UpdateSummary();

    private void UpdateSummary()
    {
        Localized.SetText(CountText, "WindowCount", _manager.Windows.Count);
        bool hasWindows = _manager.Windows.Count > 0;
        ProtectAllMenuItem.IsEnabled = hasWindows && !_picker.IsActive;
        PauseAllMenuItem.IsEnabled = hasWindows && !_picker.IsActive;
        WindowList.IsEnabled = !_picker.IsActive;
        PickButton.IsEnabled = !_picker.IsActive;
        EmptyText.Visibility = hasWindows ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnNotice(string key) => Localized.SetText(StatusText, key);

    private void OnScreenPrivacyClick(object sender, RoutedEventArgs e) => ToggleManualScreen();
    private void OnResumeAutoClick(object sender, RoutedEventArgs e) => ResumeAutomaticProtection();
    private void OnResumeScreenRequested() => ResumeScreen();

    private void OnPrivacyHotkey()
    {
        if (_idleScope.HasValue) { ResumeAutomaticProtection(); return; }
        ToggleManualScreen();
    }

    private void ToggleManualScreen()
    {
        // The home button says "恢复屏幕" whenever a full-screen overlay is visible.
        // If the overlay was started by idle protection, honor that action and
        // clear the automatic state instead of accidentally enabling manual protection.
        if (_idleScope == AutoProtectionScope.AllDisplays)
        {
            ResumeScreen();
            return;
        }
        if (_manualScreenActive) { _manualScreenActive = false; UpdateFullScreen(animate: true); return; }
        try { _screenPrivacy.Enable(animate: true); _manualScreenActive = true; UpdateScreenButtons(); }
        catch (Exception) { OnNotice("UnableEnableScreen"); }
    }
    private void ResumeScreen()
    {
        _manualScreenActive = false;
        ResumeAutomaticProtection();
        UpdateFullScreen(animate: true);
    }
    private void ActivateIdleProtection(AutoProtectionScope scope)
    {
        if (_idleScope.HasValue || !_settings.AutoProtection.Enabled) return;
        if (scope == AutoProtectionScope.AddedWindows && !_manager.HasWindows) return;
        try
        {
            if (scope == AutoProtectionScope.AllDisplays) _screenPrivacy.Enable(animate: false);
            else _manager.SetIdleForcedClosed(true, animate: false);
            _idleScope = scope;
            UpdateScreenButtons();
        }
        catch (Exception) { OnNotice("AutoProtectionStartFailed"); }
    }
    private void ResumeAutomaticProtection()
    {
        if (!_idleScope.HasValue) return;
        if (_idleScope == AutoProtectionScope.AddedWindows) _manager.SetIdleForcedClosed(false, animate: true);
        _idleScope = null;
        UpdateFullScreen(animate: true);
    }
    private void UpdateFullScreen(bool animate)
    {
        if (_manualScreenActive || _idleScope == AutoProtectionScope.AllDisplays)
        {
            try { _screenPrivacy.Enable(animate); }
            catch (Exception) { _manualScreenActive = false; OnNotice("FullScreenResumeFailed"); }
        }
        else _screenPrivacy.Disable(animate);
        UpdateScreenButtons();
    }
    private void UpdateScreenButtons()
    {
        Localized.SetContent(ScreenPrivacyButton, _screenPrivacy.IsActive ? "RestoreScreenButton" : "ProtectScreenButton");
        ResumeAutoButton.Visibility = _idleScope.HasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAutoProtectionChanged(object sender, RoutedEventArgs e) => SaveAutoProtectionFromControls();
    private void OnAutoProtectionScopeChanged(object sender, SelectionChangedEventArgs e) => SaveAutoProtectionFromControls();
    private void SaveAutoProtectionFromControls()
    {
        if (_loadingSettings || IdleMinutesCombo.SelectedValue is null) return;
        int minutes = int.TryParse(IdleMinutesCombo.SelectedValue.ToString(), out var value) ? value : 3;
        var auto = new AutoProtectionSettings(IdleProtectSwitch.IsOn, minutes,
            IdleScopeButtons.SelectedIndex == 1 ? AutoProtectionScope.AllDisplays : AutoProtectionScope.AddedWindows);
        var old = _settings;
        _settings = _settings with { AutoProtection = auto };
        if (!SaveSettings()) { _settings = old; ApplyAutoProtectionControls(); return; }
        if (!auto.Enabled || (_idleScope.HasValue && _idleScope.Value != auto.Scope)) ResumeAutomaticProtection();
        UpdateAutoProtectionEnabledControls();
    }
    private void ApplyAutoProtectionControls()
    {
        bool wasLoading = _loadingSettings;
        _loadingSettings = true;
        IdleProtectSwitch.IsOn = _settings.AutoProtection.Enabled;
        IdleMinutesCombo.SelectedItem = IdleMinutesCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i =>
            int.TryParse(i.Tag?.ToString(), out int v) && v == _settings.AutoProtection.IdleMinutes);
        IdleScopeButtons.SelectedIndex = _settings.AutoProtection.Scope == AutoProtectionScope.AllDisplays ? 1 : 0;
        _loadingSettings = wasLoading;
        UpdateAutoProtectionEnabledControls();
    }
    private void UpdateAutoProtectionEnabledControls()
    {
        IdleMinutesCombo.IsEnabled = IdleProtectSwitch.IsOn;
        IdleScopeButtons.IsEnabled = IdleProtectSwitch.IsOn;
    }

    private void OnProtectAll(object sender, RoutedEventArgs e) => _manager.SetAllClosed(true);
    private void OnPauseAll(object sender, RoutedEventArgs e) => _manager.SetAllClosed(false);
    private void ToggleAllProtection()
    {
        if (_manager.Windows.Count == 0) { ShowFromTray(); return; }
        bool allEnabled = _manager.Windows.All(item => item.Session.Target.IsClosed);
        _manager.SetAllClosed(!allEnabled);
    }

    private void OnRowToggle(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ProtectedWindowItem item) item.Session.Toggle();
    }

    private void OnRowDetach(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProtectedWindowItem item) return;
        _manager.Detach(item);
        OnNotice("WindowRemoved");
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        ReadSettingsAndApply();
    }

    private void OnHotkeyKeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        ReadSettingsAndApply();
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || LanguageCombo.SelectedValue is not string selected) return;
        var previous = _settings;
        _settings = _settings with { Language = AppLanguage.Normalize(selected) };
        if (!SaveSettings())
        {
            _settings = previous;
            _loadingSettings = true;
            LanguageCombo.SelectedValue = previous.Language;
            _loadingSettings = false;
            return;
        }
        LocalizationService.SetLanguage(_settings.Language);
    }

    private void OnAutoUpdateToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var before = _settings;
        _settings = _settings with { AutoDownloadUpdates = AutoUpdateSwitch.IsOn };
        if (!SaveSettings())
        {
            _settings = before;
            _loadingSettings = true;
            AutoUpdateSwitch.IsOn = before.AutoDownloadUpdates;
            _loadingSettings = false;
            return;
        }
        StartAutomaticUpdateTimer(initial: true);
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        bool enabled = StartupSwitch.IsOn;
        string? error = StartupRegistration.SetEnabled(enabled);
        if (error is not null)
        {
            _loadingSettings = true;
            StartupSwitch.IsOn = _settings.StartWithWindows;
            _loadingSettings = false;
            Localized.SetText(SettingsStatusText, error);
            return;
        }
        var previous = _settings;
        _settings = _settings with { StartWithWindows = enabled };
        if (!SaveSettings())
        {
            string? rollbackError = StartupRegistration.SetEnabled(previous.StartWithWindows);
            _settings = previous;
            _loadingSettings = true;
            StartupSwitch.IsOn = previous.StartWithWindows;
            _loadingSettings = false;
            if (rollbackError is not null) Localized.SetText(SettingsStatusText, "StartupRollbackFailure");
            return;
        }
        Localized.SetText(SettingsStatusText, enabled ? "StartupEnabled" : "StartupDisabled");
    }

    private void ReadSettingsAndApply()
    {
        uint modifiers = 0;
        if (ControlModifierCheck.IsChecked == true) modifiers |= HotkeyModifier.Control;
        if (ShiftModifierCheck.IsChecked == true) modifiers |= HotkeyModifier.Shift;
        if (AltModifierCheck.IsChecked == true) modifiers |= HotkeyModifier.Alt;
        uint key = 0;
        if (HotkeyKeyCombo.SelectedValue is not null)
            uint.TryParse(HotkeyKeyCombo.SelectedValue.ToString(), out key);
        var hotkey = new GlobalHotkeySettings(HotkeyEnabledSwitch.IsOn, modifiers, key);
        if (!CurtainUserSettingsStore.IsValidHotkey(hotkey))
        {
            if (hotkey.Enabled)
            {
                Localized.SetText(HotkeyStatusText, "HotkeyInvalid");
                return;
            }
            hotkey = _settings.Hotkey with { Enabled = false };
        }
        _settings = _settings with
        {
            Hotkey = hotkey
        };
        SaveSettings();
        ApplyHotkey();
    }

    private void ApplySettingsToControls()
    {
        StartupSwitch.IsOn = _settings.StartWithWindows;
        AutoUpdateSwitch.IsOn = _settings.AutoDownloadUpdates;
        ApplyAutoProtectionControls();
        HotkeyEnabledSwitch.IsOn = _settings.Hotkey.Enabled;
        ControlModifierCheck.IsChecked = (_settings.Hotkey.Modifiers & HotkeyModifier.Control) != 0;
        ShiftModifierCheck.IsChecked = (_settings.Hotkey.Modifiers & HotkeyModifier.Shift) != 0;
        AltModifierCheck.IsChecked = (_settings.Hotkey.Modifiers & HotkeyModifier.Alt) != 0;
        HotkeyKeyCombo.SelectedItem = HotkeyKeyCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
            uint.TryParse(item.Tag?.ToString(), out uint key) && key == _settings.Hotkey.VirtualKey);
        LanguageCombo.SelectedValue = _settings.Language;
    }

    private void ApplyHotkey()
    {
        _hotkey.Apply(_settings.Hotkey);
        UpdateHotkeyStatus();
    }

    private void UpdateHotkeyStatus()
    {
        bool registered = _hotkey.IsRegistered;
        if (!_settings.Hotkey.Enabled) Localized.SetText(HotkeyStatusText, "HotkeyDisabled");
        else if (registered) Localized.SetText(HotkeyStatusText, "HotkeyCurrent", DescribeHotkey(_settings.Hotkey));
        else if (_hotkey.LastError == NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED)
            Localized.SetText(HotkeyStatusText, "HotkeyAlreadyRegistered", DescribeHotkey(_settings.Hotkey));
        else Localized.SetText(HotkeyStatusText, "HotkeyRegistrationFailed",
            DescribeHotkey(_settings.Hotkey), _hotkey.LastError);
    }

    private static string DescribeHotkey(GlobalHotkeySettings hotkey)
    {
        var parts = new List<string>();
        if ((hotkey.Modifiers & HotkeyModifier.Control) != 0) parts.Add("Ctrl");
        if ((hotkey.Modifiers & HotkeyModifier.Shift) != 0) parts.Add("Shift");
        if ((hotkey.Modifiers & HotkeyModifier.Alt) != 0) parts.Add("Alt");
        string key = hotkey.VirtualKey is >= 0x70 and <= 0x7A ? $"F{hotkey.VirtualKey - 0x6F}" : ((char)hotkey.VirtualKey).ToString();
        parts.Add(key);
        return string.Join("+", parts);
    }

    private bool SaveSettings()
    {
        try { _settingsStore.Save(_settings); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Localized.SetText(SettingsStatusText, "SettingsSaveFailed"); return false; }
    }

    // Automatic checks run silently. An explicit click is always needed to install.
    private void StartAutomaticUpdateTimer(bool initial)
    {
        _autoUpdateTimer.Stop();
        if (_disposed || !_settings.AutoDownloadUpdates || !_backgroundUpdates.IsInstalled) return;
        _autoUpdateTimer.Interval = initial ? TimeSpan.FromMinutes(2) : TimeSpan.FromHours(24);
        _autoUpdateTimer.Start();
    }

    private async void OnAutoUpdateTimer(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        try
        {
            if (!_disposed && _settings.AutoDownloadUpdates)
            {
                _automaticUpdateInProgress = true;
                RefreshUpdateActionButton();
                var result = await _backgroundUpdates.CheckAndDownloadAsync(percent =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_disposed) return;
                        Localized.SetContent(UpdateActionButton, "InstallUpdate");
                        UpdateActionButton.IsEnabled = false;
                        UpdateProgress.Visibility = Visibility.Visible;
                        UpdateProgress.Value = percent;
                        Localized.SetText(UpdateStatusText, "DownloadingPercent", percent);
                    });
                },
                    cancellationToken: _updateShutdown.Token,
                    updateFound: QueueUpdateDownloading);
                if (!_disposed && result.Kind == UpdateScanKind.Ready)
                    MarkUpdateReady(result.Version);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Background network errors must not interrupt the user's work.
            DevelopmentDiagnostics.Write("automatic-update-error", new { exception = ex.GetType().Name, ex.HResult });
        }
        finally
        {
            if (!_disposed)
            {
                _automaticUpdateInProgress = false;
                UpdateProgress.Visibility = Visibility.Collapsed;
                RefreshUpdateActionButton();
            }
            StartAutomaticUpdateTimer(initial: false);
        }
    }

    private void MarkUpdateReady(string? version)
    {
        Localized.SetContent(UpdateActionButton, "InstallUpdate");
        UpdateActionButton.IsEnabled = !_updateInProgress && !_automaticUpdateInProgress;
        UpdateProgress.Visibility = Visibility.Collapsed;
        Localized.SetText(UpdateStatusText, "UpdateReady", version ?? string.Empty);
    }

    private void QueueUpdateDownloading()
    {
        DispatcherQueue.TryEnqueue(MarkUpdateDownloading);
    }

    private void MarkUpdateDownloading()
    {
        if (_disposed) return;
        Localized.SetContent(UpdateActionButton, "InstallUpdate");
        UpdateActionButton.IsEnabled = false;
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        Localized.SetText(UpdateStatusText, "UpdateDownloading");
    }

    private void RefreshUpdateActionButton()
    {
        bool ready = _backgroundUpdates.ReadyVersion is not null;
        Localized.SetContent(UpdateActionButton, ready ? "InstallUpdate" : "CheckUpdates");
        UpdateActionButton.IsEnabled = !_updateInProgress && !_automaticUpdateInProgress;
    }

    private async void OnUpdateActionClick(object sender, RoutedEventArgs e)
    {
        if (_updateInProgress || _disposed) return;
        if (_backgroundUpdates.ReadyVersion is not null)
        {
            await InstallReadyUpdateAsync();
            return;
        }

        _updateInProgress = true;
        UpdateActionButton.IsEnabled = false;
        try
        {
            Localized.SetText(UpdateStatusText, "UpdateChecking");
            if (!_backgroundUpdates.IsInstalled)
            {
                Localized.SetText(UpdateStatusText, "UpdateNotInstalled");
                return;
            }

            UpdateProgress.Value = 0;
            var result = await _backgroundUpdates.CheckAndDownloadAsync(percent =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_disposed) return;
                    Localized.SetContent(UpdateActionButton, "InstallUpdate");
                    UpdateActionButton.IsEnabled = false;
                    UpdateProgress.Visibility = Visibility.Visible;
                    UpdateProgress.Value = percent;
                    Localized.SetText(UpdateStatusText, "DownloadingPercent", percent);
                });
            }, _updateShutdown.Token, QueueUpdateDownloading);
            if (_disposed) return;
            switch (result.Kind)
            {
                case UpdateScanKind.Ready: MarkUpdateReady(result.Version); break;
                case UpdateScanKind.Busy: Localized.SetText(UpdateStatusText, "UpdateBusy"); break;
                case UpdateScanKind.NotInstalled: Localized.SetText(UpdateStatusText, "UpdateNotInstalled"); break;
                default: Localized.SetText(UpdateStatusText, "UpdateLatest"); break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_disposed) Localized.SetText(UpdateStatusText, "UpdateCheckFailed"); }
        finally
        {
            _updateInProgress = false;
            if (!_disposed)
            {
                UpdateProgress.Visibility = Visibility.Collapsed;
                RefreshUpdateActionButton();
            }
        }
    }

    private async Task InstallReadyUpdateAsync()
    {
        if (_disposed || _updateInProgress) return;
        string? ready = _backgroundUpdates.ReadyVersion;
        if (ready is null)
        {
            RefreshUpdateActionButton();
            Localized.SetText(UpdateStatusText, "UpdateMissing");
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = LocalizationService.Get("InstallUpdateTitle", ready),
            Content = LocalizationService.Get("InstallUpdateConfirm"),
            PrimaryButtonText = LocalizationService.Get("InstallAndRestart"),
            CloseButtonText = LocalizationService.Get("Later"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || _disposed) return;
        try
        {
            if (!_backgroundUpdates.BeginApplyPending())
            {
                Localized.SetText(UpdateStatusText, "UpdateInvalid");
                RefreshUpdateActionButton();
                return;
            }
            ExitCurtain();
        }
        catch (Exception) { if (!_disposed) Localized.SetText(UpdateStatusText, "UpdateInstallFailed"); }
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        AppWindow.Hide();
        OnNotice("HiddenToTray");
    }

    private void ExitCurtain()
    {
        _allowClose = true;
        Close();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        DisposeServices();
        Application.Current.Exit();
    }

    private void DisposeServices()
    {
        if (_disposed) return;
        _disposed = true;
        _autoUpdateTimer.Stop();
        _autoUpdateTimer.Tick -= OnAutoUpdateTimer;
        _idleMonitor.Dispose();
        if (_privacyHotkeyRegistered) NativeMethods.UnregisterHotKey(WindowNative.GetWindowHandle(this), PrivacyHotkeyId);
        _privacyHotkeyRegistered = false;
        _screenPrivacy.ResumeRequested -= OnResumeScreenRequested;
        _screenPrivacy.Dispose();
        _updateShutdown.Cancel();
        // A running download observes cancellation; don't dispose the CTS mid-await.
        AppWindow.Closing -= OnWindowClosing;
        _picker.Finished -= OnPickFinished;
        _picker.HandoffCompleted -= OnPickerHandoffCompleted;
        _picker.Dispose();
        _hotkey.Pressed -= StartPickerFromHotkey;
        _hotkey.Dispose();
        _tray.OpenRequested -= ShowFromTray;
        _tray.ToggleProtectionRequested -= ToggleAllProtection;
        _tray.ExitRequested -= ExitCurtain;
        _tray.Dispose();
        _messageHost.Message -= OnWindowMessage;
        _messageHost.Dispose();
        _manager.CollectionChanged -= UpdateSummary;
        _manager.Notice -= OnNotice;
        _manager.Dispose();
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        App.OnMainWindowClosed(this);
    }
}
