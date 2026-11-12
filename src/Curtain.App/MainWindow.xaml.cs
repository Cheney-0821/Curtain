using Curtain.App.Interop;
using Curtain.App.Localization;
using Curtain.App.Services;
using Curtain.App.Controls;
using Curtain.Core.Protection;
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
    private readonly System.Collections.ObjectModel.ObservableCollection<ProtectedApplicationItem> _applications = new();
    private readonly CurtainManager _manager;
    private readonly ScreenPrivacyManager _screenPrivacy;
    private readonly IdleProtectionMonitor _idleMonitor;
    private bool _manualScreenActive;
    private AutoProtectionScope? _idleScope;
    private const int PrivacyHotkeyId = 0x4353;
    private readonly GlobalHotkey _privacyHotkey;
    private PrivacyMouseHook? _privacyMouseHook;
    private readonly WindowPicker _picker;
    private ProtectedAppRuleAutoBinder? _appRuleBinder;
#if DEBUG || CURTAIN_TEST_SETTINGS
    private readonly CurtainUserSettingsStore _settingsStore;
#else
    private readonly CurtainUserSettingsStore _settingsStore;
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
    private WindowProtectionControl? _globalProtection;
    private DispatcherQueueTimer? _protectionSaveTimer;
    private string? _pendingProtectionOperationId;

    public MainWindow()
    {
#if DEBUG || CURTAIN_TEST_SETTINGS
        string? testDataDirectory = Environment.GetEnvironmentVariable("CURTAIN_TEST_SETTINGS_DIR");
        if (string.IsNullOrWhiteSpace(testDataDirectory))
            testDataDirectory = Path.Combine(AppContext.BaseDirectory, "test-settings");
        _settingsStore = new CurtainUserSettingsStore(testDataDirectory);
#else
        _settingsStore = new CurtainUserSettingsStore();
#endif
        var loaded = _settingsStore.Load();
        _settings = loaded.Settings;
        CurtainDiagnostics.SetEnabled(_settings.DiagnosticsEnabled);
#if DEBUG || CURTAIN_TEST_SETTINGS
        CurtainDiagnostics.Initialize(testDataDirectory);
#else
        CurtainDiagnostics.Initialize();
#endif
        InitializeComponent();
        CurtainDiagnostics.Write("settings.load.completed", stage: "settings-store",
            reasonCode: loaded.RecoveredFromInvalidFile ? "RecoveredOrDefault" : "Success");
        CurtainRuntimeStatus.SetSettingsLoadState(loaded.RecoveredFromInvalidFile ? "RecoveredOrDefault" : "ValidOrDefault");
        CurtainRuntimeStatus.SetAutoProtection(_settings.AutoProtection.Enabled,
            _settings.AutoProtection.IdleMinutes, _settings.AutoProtection.Scope.ToString(), active: false);
        CurtainRuntimeStatus.SetStartupRegistrationState(_settings.StartWithWindows ? "EnabledInSettings" : "DisabledInSettings");
        LocalizationService.SetLanguage(_settings.Language);
        InitializeLocalizedControls();
        UpdateWindowTitle();
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Curtain.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        UpdateAppVersion();
        SystemBackdrop = new MicaBackdrop();

        _backgroundUpdates = new BackgroundUpdateService(_updateService);
        _autoUpdateTimer = DispatcherQueue.CreateTimer();
        _autoUpdateTimer.IsRepeating = false;
        _autoUpdateTimer.Tick += OnAutoUpdateTimer;

        _manager = new(DispatcherQueue);
        _screenPrivacy = new(DispatcherQueue);
        _screenPrivacy.ResumeRequested += OnResumeScreenRequested;
        WindowList.ItemsSource = _applications;
        _manager.CollectionChanged += UpdateSummary;
        _manager.Notice += OnNotice;
        InitializeGlobalProtection();
        _appRuleBinder = new ProtectedAppRuleAutoBinder(
            DispatcherQueue,
            new ProtectedAppRuleStore(CurtainDiagnostics.DataDirectory),
            (hwnd, rule, operationId) =>
                _manager.Attach(hwnd, initiallyClosed: false, customProtection: rule.CustomProtection,
                    operationId: operationId, ruleId: rule.RuleId.ToString("N")),
            () => _manager.Windows.Select(item => (nint)item.Session.Target.Identity.Handle).ToArray());
        RememberedAppsList.ItemsSource = _appRuleBinder.RuleItems;
        _appRuleBinder.RulesChanged += UpdateSummary;

        nint handle = WindowNative.GetWindowHandle(this);
        _picker = new(_manager, DispatcherQueue);
        _picker.PreviewChanged += OnPickerPreviewChanged;
        _picker.Finished += OnPickFinished;
        _picker.HandoffCompleted += OnPickerHandoffCompleted;
        _appRuleBinder.Start();

        _messageHost = new(handle);
        _messageHost.Message += OnWindowMessage;
        _messageHost.AfterMessage += OnAfterWindowMessage;
        double windowScale = NativeMethods.GetDpiForWindow(handle) / 96d;
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32((int)(700 * windowScale), (int)(600 * windowScale)));
        _privacyHotkey = new(handle, PrivacyHotkeyId);
        _privacyHotkey.Pressed += OnPrivacyHotkey;
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
        _globalProtection?.RefreshLanguage();
        _loadingSettings = false;
        ApplyHotkey();
        ApplyPrivacyHotkey();
        ApplyPrivacyMouseButton();
        // Upgrade from a development path to Velopack's stable launcher on first installed start.
        if (_settings.StartWithWindows)
        {
            string? startupError = StartupRegistration.SetEnabled(true, CurtainDiagnostics.NewOperationId());
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
        DiagnosticsPageControl.Configure(_settings.DiagnosticsEnabled, _settings.IncludeRawEtw,
            (enabled, includeRawEtw) =>
            {
                var previous = _settings;
                string operationId = CurtainDiagnostics.NewOperationId();
                _settings = _settings with { DiagnosticsEnabled = enabled, IncludeRawEtw = includeRawEtw };
                if (enabled) CurtainDiagnostics.SetEnabled(true);
                if (!SaveSettings(operationId))
                {
                    _settings = previous;
                    CurtainDiagnostics.SetEnabled(previous.DiagnosticsEnabled);
                    return false;
                }
                if (previous.DiagnosticsEnabled != enabled)
                    CurtainDiagnostics.Write("settings.preference.changed", operationId: operationId,
                        stage: "Diagnostics.Enabled", reasonCode: enabled ? "Enabled" : "Disabled");
                if (previous.IncludeRawEtw != includeRawEtw)
                    CurtainDiagnostics.Write("settings.preference.changed", operationId: operationId,
                        stage: "Diagnostics.IncludeRawEtw", reasonCode: includeRawEtw ? "Enabled" : "Disabled");
                if (!enabled) CurtainDiagnostics.SetEnabled(false);
                return true;
            });
        if (!_privacyHotkey.IsRegistered)
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
        if (_privacyHotkey.HandleMessage(message, wParam)) return;
        if (_hotkey.HandleMessage(message, wParam)) return;
        if (_tray.HandleMessage(message, wParam, lParam)) return;
    }

    private void OnAfterWindowMessage(uint message, nint wParam, nint lParam)
    {
        if (message == 0x0024 && lParam != 0) // WM_GETMINMAXINFO, after Windows sets its defaults.
        {
            double scale = NativeMethods.GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96d;
            System.Runtime.InteropServices.Marshal.WriteInt32(lParam, 24, (int)Math.Ceiling(700 * scale));
            System.Runtime.InteropServices.Marshal.WriteInt32(lParam, 28, (int)Math.Ceiling(600 * scale));
        }
        else if (message == 0x0214 && lParam != 0) // WM_SIZING
        {
            var bounds = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.Rect>(lParam);
            int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
            int edge = (int)wParam;
            // Keep settings usable in both landscape and moderately tall layouts.
            if (width > height * 2) height = (int)Math.Ceiling(width / 2d);
            if (width < height * 0.8) width = (int)Math.Ceiling(height * 0.8);
            if (edge is 1 or 4 or 7) bounds.Left = bounds.Right - width;
            else bounds.Right = bounds.Left + width;
            if (edge is 3 or 4 or 5) bounds.Top = bounds.Bottom - height;
            else bounds.Bottom = bounds.Top + height;
            System.Runtime.InteropServices.Marshal.StructureToPtr(bounds, lParam, false);
        }
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string section = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "home";
        HomePage.Visibility = section == "home" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = section == "settings" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLanguageChanged()
    {
        if (_disposed) return;
        Localized.RefreshAll();
        _appRuleBinder?.RefreshRuleItems();
        _globalProtection?.RefreshLanguage();
        RefreshLocalizedOptionControls();
        UpdateWindowTitle();
        UpdateAppVersion();
        UpdateLocalizedScopeItems();
        _tray.RefreshLocalization();
        _screenPrivacy.RefreshLocalization();
        UpdateScreenButtons();
        UpdateSummary();
        UpdateHotkeyStatus();
        RefreshPrivacyHotkeyStatus();
    }

    private void InitializeLocalizedControls()
    {
        Localized.SetContent(HomeItem, "NavigationHome");
        Localized.SetContent(SettingsItem, "NavigationSettings");
        Localized.SetText(HomeTitleText, "HomeTitle");
        Localized.SetText(AddWindowTitleText, "AddWindowTitle");
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
        Localized.SetText(RememberedAppsTitleText, "RememberedAppsTitle");
        Localized.SetText(RememberedAppsDescriptionText, "RememberedAppsDescription");
        Localized.SetText(StatusText, "Ready");
        Localized.SetText(SettingsTitleText, "SettingsTitle");
        Localized.SetText(GeneralTitleText, "GeneralTitle");
        Localized.SetText(StartupLabelText, "StartupHeader");
        Localized.SetAutomationName(StartupSwitch, "StartupHeader");
        Localized.SetText(AutoUpdateLabelText, "AutoUpdateHeader");
        Localized.SetAutomationName(AutoUpdateCheck, "AutoUpdateHeader");
        Localized.SetText(LanguageLabelText, "LanguageHeader");
        Localized.SetAutomationName(LanguageCombo, "LanguageHeader");
        SetComboItemContent(LanguageCombo, "LanguageSystem", "LanguageEnglish", "LanguageChinese");
        Localized.SetText(AutoProtectionTitleText, "AutoProtectionTitle");
        Localized.SetAutomationName(IdleProtectSwitch, "EnableIdleProtection");
        Localized.SetText(IdleMinutesLabelText, "IdleDurationHeader");
        Localized.SetAutomationName(IdleMinutesCombo, "IdleDurationHeader");
        SetComboItemContent(IdleMinutesCombo, "IdleMinute1", "IdleMinute3", "IdleMinute5", "IdleMinute10");
        Localized.SetText(AutoProtectionScopeLabelText, "AutoProtectionScopeLabel");
        Localized.SetAutomationName(IdleScopeCombo, "AutoProtectionScopeLabel");
        Localized.SetText(PrivacyHotkeyLabelText, "PrivacyHotkeyHeader");
        Localized.SetText(PrivacyMouseLabelText, "PrivacyMouseHeader");
        Localized.SetText(PrivacyMouseHintText, "PrivacyMouseHint");
        Localized.SetAutomationName(PrivacyMouseCombo, "PrivacyMouseHeader");
        SetComboItemContent(PrivacyMouseCombo, "PrivacyMouseOff", "PrivacyMouseBack", "PrivacyMouseForward");
        Localized.SetText(HotkeyEnabledLabelText, "EnableHotkeyHeader");
        Localized.SetText(AboutDescriptionText, "AboutDescription");
        Localized.SetText(UpdatesTitleText, "UpdatesTitle");
        Localized.SetContent(UpdateActionButton, "CheckUpdates");
        Localized.SetText(UpdateStatusText, "UpdateLatest");
        InitializeSettingDescriptions();
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
        IdleProtectSwitch.OnContent = on;
        IdleProtectSwitch.OffContent = off;

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
#if DEBUG
        version = LocalizationService.Get("DevelopmentBuild");
#else
        if (version == "开发版") version = LocalizationService.Get("DevelopmentBuild");
#endif
        Localized.SetText(AppVersionText, "AppVersion", version);
    }

    private void UpdateLocalizedScopeItems()
    {
        bool wasLoading = _loadingSettings;
        _loadingSettings = true;
        int selected = IdleScopeCombo.SelectedIndex;
        IdleScopeCombo.ItemsSource = new[]
        {
            LocalizationService.Get("ScopeAddedWindows"),
            LocalizationService.Get("ScopeAllDisplays")
        };
        IdleScopeCombo.SelectedIndex = selected is 0 or 1 ? selected : 0;
        _loadingSettings = wasLoading;
    }

    private void OnPickerPreviewChanged(LocalizationMessage message) =>
        Localized.SetText(CandidateText, message.Key, message.Arguments);

    private void OnPickClick(object sender, RoutedEventArgs e) => StartPicker();

    private void StartPicker()
    {
        if (_picker.IsActive) return;
        string operationId = CurtainDiagnostics.NewOperationId();
        CurtainDiagnostics.Write("ui.command.started", operationId: operationId,
            stage: "add-window", reasonCode: "StartPicker");
        try
        {
            _picker.Start(operationId);
            CandidateText.Visibility = Visibility.Visible;
            PickButton.IsEnabled = false;
            Localized.SetContent(PickButton, "PickerInProgress");
            UpdateSummary();
            CurtainDiagnostics.Write("ui.command.completed", operationId: operationId,
                stage: "add-window", reasonCode: "PickerStarted");
        }
        catch (Exception ex)
        {
            CurtainDiagnostics.WriteException("ui.command.failed", ex, operationId: operationId,
                stage: "add-window", reasonCode: "PickerStartFailed");
            OnNotice("UnableStartPicker");
        }
    }

    private void StartPickerFromHotkey()
    {
        if (_editingHotkey) _captureRegisteredHotkey?.Invoke(_settings.Hotkey);
        else if (!_screenPrivacy.IsActive) StartPicker();
    }

    private CurtainSession? OnPickFinished(nint target, LocalizationMessage message)
    {
        CandidateText.Visibility = Visibility.Collapsed;
        PickButton.IsEnabled = true;
        Localized.SetContent(PickButton, "AddWindowButton");
        Localized.SetText(CandidateText, message.Key, message.Arguments);
        CurtainSession? session = null;
        if (target != nint.Zero)
        {
            try
            {
                var item = _manager.Attach(target, operationId: _picker.OperationId);
                WindowList.SelectedItem = item;
                ProtectedAppRule? rule = _appRuleBinder?.RememberSelectedWindow(target, item.Session.CustomProtection,
                    _picker.OperationId, item.Session.SessionId);
                if (rule is not null) item.Session.AssociateRememberedRule(rule.RuleId);
                OnNotice(rule is null ? "WindowAddedNotRemembered" : "WindowAdded");
                session = item.Session;
            }
            catch (InvalidOperationException ex)
            {
                CurtainDiagnostics.WriteException("window.attach.ui-failed", ex,
                    operationId: _picker.OperationId, stage: "picker-result", reasonCode: "SelectionRejected",
                    hwnd: target.ToInt64());
                OnNotice("WindowNotSelectable");
            }
            catch (Exception ex)
            {
                CurtainDiagnostics.WriteException("window.attach.ui-failed", ex,
                    operationId: _picker.OperationId, stage: "picker-result", reasonCode: "WindowNotAdded",
                    hwnd: target.ToInt64());
                OnNotice("WindowNotAdded");
            }
        }
        UpdateSummary();
        return session;
    }

    private void OnPickerHandoffCompleted() => UpdateSummary();

    private void UpdateSummary()
    {
        _appRuleBinder?.RefreshRuleItems();
        var groups = _manager.Windows.GroupBy(item =>
            ApplicationIdentityReader.TryRead((nint)item.Session.Target.Identity.Handle).Identity?.StableKey
            ?? "window:" + item.Session.Target.Identity.Handle, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var old in _applications.ToArray())
            if (!groups.Any(group => group.Key == old.Key))
            {
                old.Dispose();
                _applications.Remove(old);
            }
        foreach (var group in groups)
        {
            var existing = _applications.FirstOrDefault(item => item.Key == group.Key);
            if (existing is null) _applications.Add(new(group.Key, group.ToArray()));
            else existing.Update(group.ToArray());
        }
        Localized.SetText(CountText, "ApplicationCount", _applications.Count);
        bool hasWindows = _manager.Windows.Count > 0;
        ProtectAllMenuItem.IsEnabled = hasWindows && !_picker.IsActive;
        PauseAllMenuItem.IsEnabled = hasWindows && !_picker.IsActive;
        WindowList.IsEnabled = !_picker.IsActive;
        PickButton.IsEnabled = !_picker.IsActive;
        EmptyText.Visibility = hasWindows ? Visibility.Collapsed : Visibility.Visible;
        WindowList.MinHeight = hasWindows ? 112 : 0;
    }

    private void OnNotice(string key) => Localized.SetText(StatusText, key);

    private void OnScreenPrivacyClick(object sender, RoutedEventArgs e) => ToggleManualScreen();
    private void OnResumeAutoClick(object sender, RoutedEventArgs e) => ResumeAutomaticProtection();
    private void OnResumeScreenRequested() => ResumeScreen();

    private void OnPrivacyHotkey()
    {
        if (_editingHotkey) { _captureRegisteredHotkey?.Invoke(_settings.PrivacyHotkey); return; }
        TogglePrivacyProtection("Hotkey");
    }

    private void TogglePrivacyProtection(string inputSource)
    {
        if (_disposed || _editingHotkey) return;
        CurtainDiagnostics.Write("fullscreen.toggle.requested", stage: "input-dispatch", reasonCode: inputSource);
        if (_screenPrivacy.IsActive) { ResumeScreen(); return; }
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
        try { _picker.Cancel(); _screenPrivacy.Enable(animate: true); _manualScreenActive = true; UpdateScreenButtons(); }
        catch (Exception ex)
        {
            CurtainDiagnostics.WriteException("ui.command.failed", ex,
                stage: "manual-fullscreen-protection", reasonCode: "EnableFailed");
            OnNotice("UnableEnableScreen");
        }
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
            CurtainRuntimeStatus.SetAutoProtection(_settings.AutoProtection.Enabled,
                _settings.AutoProtection.IdleMinutes, scope.ToString(), active: true);
            CurtainDiagnostics.Write("idle.protection.activated", stage: "idle-activation", reasonCode: scope.ToString());
            UpdateScreenButtons();
        }
        catch (Exception ex)
        {
            CurtainDiagnostics.WriteException("idle.protection.activation-failed", ex,
                stage: "idle-activation", reasonCode: "ProtectionStartFailed");
            OnNotice("AutoProtectionStartFailed");
        }
    }
    private void ResumeAutomaticProtection()
    {
        if (!_idleScope.HasValue) return;
        _idleMonitor.ResetAfterResume();
        string previousScope = _idleScope.Value.ToString();
        if (_idleScope == AutoProtectionScope.AddedWindows) _manager.SetIdleForcedClosed(false, animate: true);
        _idleScope = null;
        CurtainRuntimeStatus.SetAutoProtection(_settings.AutoProtection.Enabled,
            _settings.AutoProtection.IdleMinutes, _settings.AutoProtection.Scope.ToString(), active: false);
        CurtainDiagnostics.Write("idle.protection.resumed", stage: "idle-resume", reasonCode: previousScope);
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
            IdleScopeCombo.SelectedIndex == 1 ? AutoProtectionScope.AllDisplays : AutoProtectionScope.AddedWindows);
        var old = _settings;
        _settings = _settings with { AutoProtection = auto };
        string operationId = CurtainDiagnostics.NewOperationId();
        if (!SaveSettings(operationId)) { _settings = old; ApplyAutoProtectionControls(); return; }
        if (old.AutoProtection.Enabled != auto.Enabled)
            CurtainDiagnostics.Write("settings.preference.changed", operationId: operationId,
                stage: "AutoProtection.Enabled", reasonCode: auto.Enabled ? "Enabled" : "Disabled");
        if (old.AutoProtection.IdleMinutes != auto.IdleMinutes)
            CurtainDiagnostics.Write("settings.preference.changed", operationId: operationId,
                stage: "AutoProtection.IdleMinutes", reasonCode: "Updated",
                metricName: "IdleMinutes", metricValue: auto.IdleMinutes);
        if (old.AutoProtection.Scope != auto.Scope)
            CurtainDiagnostics.Write("settings.preference.changed", operationId: operationId,
                stage: "AutoProtection.Scope", reasonCode: auto.Scope.ToString());
        CurtainRuntimeStatus.SetAutoProtection(auto.Enabled, auto.IdleMinutes, auto.Scope.ToString(), _idleScope.HasValue);
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
        IdleScopeCombo.SelectedIndex = _settings.AutoProtection.Scope == AutoProtectionScope.AllDisplays ? 1 : 0;
        _loadingSettings = wasLoading;
        UpdateAutoProtectionEnabledControls();
    }
    private void UpdateAutoProtectionEnabledControls()
    {
        IdleMinutesCombo.IsEnabled = IdleProtectSwitch.IsOn;
        IdleScopeCombo.IsEnabled = IdleProtectSwitch.IsOn;
    }

    private void OnProtectAll(object sender, RoutedEventArgs e) => _manager.SetAllClosed(true);
    private void OnPauseAll(object sender, RoutedEventArgs e) => _manager.SetAllClosed(false);
    private void ToggleAllProtection()
    {
        if (_manager.Windows.Count == 0) { ShowFromTray(); return; }
        bool allEnabled = _manager.Windows.All(item => item.Session.Target.IsClosed);
        _manager.SetAllClosed(!allEnabled);
    }

    private void InitializeGlobalProtection()
    {
        var title = new TextBlock { Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        Localized.SetText(title, "DefaultWindowProtection");
        _manager.SetProtectionDefaults(_settings.WindowProtection);
        _globalProtection = new WindowProtectionControl(_settings.WindowProtection);
        _protectionSaveTimer = DispatcherQueue.CreateTimer();
        _protectionSaveTimer.IsRepeating = false;
        _protectionSaveTimer.Interval = TimeSpan.FromMilliseconds(300);
        _protectionSaveTimer.Tick += (_, _) =>
        {
            string? operationId = _pendingProtectionOperationId;
            _pendingProtectionOperationId = null;
            SaveSettings(operationId);
        };
        _globalProtection.Changed += value =>
        {
            WindowProtectionSettings previous = _settings.WindowProtection;
            string operationId = _pendingProtectionOperationId ??= CurtainDiagnostics.NewOperationId();
            _settings = _settings with { WindowProtection = value };
            if (previous.BlurAmount != value.BlurAmount)
                CurtainDiagnostics.Write("settings.preference.changed", operationId: operationId,
                    stage: "WindowProtection.BlurAmount", reasonCode: "Updated",
                    metricName: "BlurAmount", metricValue: value.BlurAmount);
            if (previous.AllowClickThrough != value.AllowClickThrough)
                CurtainDiagnostics.Write("settings.preference.changed", operationId: operationId,
                    stage: "WindowProtection.AllowClickThrough",
                    reasonCode: value.AllowClickThrough ? "Enabled" : "Disabled",
                    metricName: "AllowClickThrough", metricValue: value.AllowClickThrough ? 1 : 0);
            _manager.SetProtectionDefaults(value);
            _protectionSaveTimer.Stop();
            _protectionSaveTimer.Start();
        };
        GlobalProtectionHost.Children.Add(title);
        GlobalProtectionHost.Children.Add(_globalProtection);

    }

    private void OnWindowSettingsLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not CommunityToolkit.WinUI.Controls.SettingsExpander expander || expander.Tag is not ProtectedApplicationItem app) return;
        var item = app.Members[0];
        void ConfigureApplication(WindowProtectionSettings? value)
        {
            string operationId = CurtainDiagnostics.NewOperationId();
            foreach (var member in app.Members.ToArray())
                if (!member.Session.IsDestroyed) member.Session.ConfigureWindowProtection(value, operationId);
            if (app.Members.Length > 0)
                _appRuleBinder?.UpdateWindowProtection(
                    (nint)app.Members[0].Session.Target.Identity.Handle, value,
                    operationId, app.Members[0].Session.SessionId);
        }
        var follow = new CheckBox { IsChecked = item.UsesGlobal };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(follow, LocalizationService.Get("FollowGlobalProtection"));
        var followCard = new CommunityToolkit.WinUI.Controls.SettingsCard
        {
            Header = LocalizationService.Get("FollowGlobalProtection"),
            Description = LocalizationService.Get("FollowGlobalDescription"),
            HeaderIcon = new FontIcon { Glyph = "\uE71B", FontSize = 20 }, Content = follow
        };
        var editor = new WindowProtectionControl(item.ProtectionOptions);
        var editorCards = editor.TakeSettingsCards();
        editor.SetEditable(follow.IsChecked != true);
        bool refreshing = false;
        void Refresh()
        {
            refreshing = true;
            follow.IsChecked = app.UsesGlobal; editor.SetEditable(follow.IsChecked != true); editor.SetSettings(app.ProtectionOptions);
            refreshing = false;
        }
        void ConfigureFollow()
        {
            if (refreshing || app.Members.All(member => member.Session.IsDestroyed)) return;
            WindowProtectionSettings? value = follow.IsChecked == true ? null : editor.Settings;
            ConfigureApplication(value);
            Refresh();
        }
        follow.Checked += (_, _) => ConfigureFollow();
        follow.Unchecked += (_, _) => ConfigureFollow();
        editor.Changed += value =>
        {
            if (!refreshing && follow.IsChecked != true)
            {
                ConfigureApplication(value);
            }
        };
        System.ComponentModel.PropertyChangedEventHandler changed = (_, e) =>
        { if (e.PropertyName is nameof(ProtectedWindowItem.ProtectionOptions) or nameof(ProtectedWindowItem.UsesGlobal)) Refresh(); };
        app.PropertyChanged += changed;
        RoutedEventHandler? unloaded = null;
        unloaded = (_, _) => { app.PropertyChanged -= changed; expander.Unloaded -= unloaded; };
        expander.Unloaded += unloaded;
        expander.Items.Clear(); expander.Items.Add(followCard);
        foreach (var card in editorCards) expander.Items.Add(card);
    }

    private void OnRowDetach(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProtectedApplicationItem app) return;
        foreach (var item in app.Members.ToArray())
        {
            _appRuleBinder?.SuppressWindow((nint)item.Session.Target.Identity.Handle);
            _manager.Detach(item);
        }
        OnNotice("WindowRemoved");
    }

    private void OnRememberedAppRemoveButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) Localized.SetContent(button, "RememberedAppsRemove");
    }

    private void OnRemoveRememberedAppClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Guid ruleId && _appRuleBinder?.RemoveRule(ruleId) == true)
            OnNotice("RememberedAppRemoved");
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || LanguageCombo.SelectedValue is not string selected) return;
        var previous = _settings;
        string operationId = CurtainDiagnostics.NewOperationId();
        _settings = _settings with { Language = AppLanguage.Normalize(selected) };
        if (!SaveSettings(operationId))
        {
            _settings = previous;
            _loadingSettings = true;
            LanguageCombo.SelectedValue = previous.Language;
            _loadingSettings = false;
            return;
        }
        CurtainDiagnostics.Write("settings.preference.changed", operationId: operationId,
            stage: "Language", reasonCode: _settings.Language);
        LocalizationService.SetLanguage(_settings.Language);
    }

    private void OnAutoUpdateToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var before = _settings;
        string operationId = CurtainDiagnostics.NewOperationId();
        _settings = _settings with { AutoDownloadUpdates = (AutoUpdateCheck.IsChecked == true) };
        if (!SaveSettings(operationId))
        {
            _settings = before;
            _loadingSettings = true;
            AutoUpdateCheck.IsChecked = before.AutoDownloadUpdates;
            _loadingSettings = false;
            return;
        }
        CurtainDiagnostics.Write("settings.preference.changed", operationId: operationId,
            stage: "AutoDownloadUpdates", reasonCode: _settings.AutoDownloadUpdates ? "Enabled" : "Disabled");
        StartAutomaticUpdateTimer(initial: true);
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        bool enabled = StartupSwitch.IsOn;
        string operationId = CurtainDiagnostics.NewOperationId();
        string? error = StartupRegistration.SetEnabled(enabled, operationId);
        if (error is not null)
        {
            CurtainRuntimeStatus.SetStartupRegistrationState("Failed");
            _loadingSettings = true;
            StartupSwitch.IsOn = _settings.StartWithWindows;
            _loadingSettings = false;
            Localized.SetText(SettingsStatusText, error);
            return;
        }
        var previous = _settings;
        _settings = _settings with { StartWithWindows = enabled };
        if (!SaveSettings(operationId))
        {
            string? rollbackError = StartupRegistration.SetEnabled(previous.StartWithWindows, operationId);
            _settings = previous;
            _loadingSettings = true;
            StartupSwitch.IsOn = previous.StartWithWindows;
            _loadingSettings = false;
            if (rollbackError is not null) Localized.SetText(SettingsStatusText, "StartupRollbackFailure");
            return;
        }
        CurtainDiagnostics.Write("settings.preference.changed", operationId: operationId,
            stage: "StartWithWindows", reasonCode: enabled ? "Enabled" : "Disabled");
        Localized.SetText(SettingsStatusText, enabled ? "StartupEnabled" : "StartupDisabled");
        CurtainRuntimeStatus.SetStartupRegistrationState(enabled ? "EnabledBackgroundArgument" : "Disabled");
    }

    private void ApplySettingsToControls()
    {
        StartupSwitch.IsOn = _settings.StartWithWindows;
        AutoUpdateCheck.IsChecked = _settings.AutoDownloadUpdates;
        ApplyAutoProtectionControls();
        RefreshHotkeyKeycaps();
        LanguageCombo.SelectedValue = _settings.Language;
        PrivacyMouseCombo.SelectedIndex = (int)_settings.PrivacyMouseButton;
    }

    private void ApplyHotkey()
    {
        _hotkey.Apply(_settings.Hotkey);
        UpdateHotkeyStatus();
    }

    private void UpdateHotkeyStatus()
    {
        RefreshHotkeyKeycaps();
        bool registered = _hotkey.IsRegistered;
        HotkeyStatusText.Visibility = registered || !_settings.Hotkey.Enabled ? Visibility.Collapsed : Visibility.Visible;
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
        if ((hotkey.Modifiers & HotkeyModifier.Windows) != 0) parts.Add("Win");
        if ((hotkey.Modifiers & HotkeyModifier.Control) != 0) parts.Add("Ctrl");
        if ((hotkey.Modifiers & HotkeyModifier.Shift) != 0) parts.Add("Shift");
        if ((hotkey.Modifiers & HotkeyModifier.Alt) != 0) parts.Add("Alt");
        string key = KeyName(hotkey.VirtualKey);
        parts.Add(key);
        return string.Join("+", parts);
    }

    private bool SaveSettings(string? operationId = null)
    {
        if (operationId is null && _pendingProtectionOperationId is { } pendingOperationId)
        {
            operationId = pendingOperationId;
            _pendingProtectionOperationId = null;
            _protectionSaveTimer?.Stop();
        }
        operationId ??= CurtainDiagnostics.NewOperationId();
        try
        {
            CurtainDiagnostics.Write("settings.save.started", operationId: operationId,
                stage: "settings-store", reasonCode: "Started");
            _settingsStore.Save(_settings, (stage, errorCode) =>
                CurtainDiagnostics.Write(errorCode == 0 ? "settings.store.stage" : "settings.store.failed",
                    errorCode == 0 ? CurtainDiagnosticLevel.Information : CurtainDiagnosticLevel.Warning,
                    operationId: operationId, stage: stage,
                    reasonCode: errorCode == 0 ? "Success" : "Failed", errorCode: errorCode));
            CurtainDiagnostics.Write("settings.save.completed", operationId: operationId,
                stage: "settings-store", reasonCode: "Success");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            CurtainDiagnostics.WriteException("settings.save.failed", ex, operationId: operationId,
                stage: "settings-store", reasonCode: "SettingsSaveFailed");
            Localized.SetText(SettingsStatusText, "SettingsSaveFailed"); return false;
        }
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
                else if (!_disposed && result.Kind == UpdateScanKind.UpToDate)
                    Localized.SetText(UpdateStatusText, "UpdateLatest");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Background network errors must not interrupt the user's work.
            CurtainDiagnostics.WriteException("update.background.failed", ex,
                stage: "automatic-update", reasonCode: "BackgroundUpdateFailed");
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
        CurtainDiagnostics.Write("app.exit.requested", stage: "tray-command", reasonCode: "UserRequested");
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
        if (_protectionSaveTimer?.IsRunning == true)
        {
            _protectionSaveTimer.Stop();
            string? operationId = _pendingProtectionOperationId;
            _pendingProtectionOperationId = null;
            SaveSettings(operationId);
        }
        _disposed = true;
        _appRuleBinder?.Dispose();
        _appRuleBinder = null;
        foreach (var app in _applications) app.Dispose();
        if (_appRuleBinder is not null) _appRuleBinder.RulesChanged -= UpdateSummary;
        DiagnosticsPageControl.Dispose();
        CurtainDiagnostics.Write("app.stopping", stage: "window-closed", reasonCode: "NormalExit");
        _autoUpdateTimer.Stop();
        _autoUpdateTimer.Tick -= OnAutoUpdateTimer;
        _idleMonitor.Dispose();
        _privacyMouseHook?.Dispose();
        _privacyMouseHook = null;
        _privacyHotkey.Pressed -= OnPrivacyHotkey;
        _privacyHotkey.Dispose();
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
        CurtainDiagnostics.Write("app.cleanup.completed", stage: "services-disposed", reasonCode: "Success");
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        App.OnMainWindowClosed(this);
    }
}
