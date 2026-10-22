using Curtain.App.Interop;
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
    private readonly CurtainUserSettingsStore _settingsStore = new();
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
        Title = "Curtain · 窗口隐私保护";
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Curtain.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        AppVersionText.Text = "窗口隐私保护 · " + _updateService.InstalledVersion;
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(860, 760));

        _backgroundUpdates = new BackgroundUpdateService(_updateService);
        _autoUpdateTimer = DispatcherQueue.CreateTimer();
        _autoUpdateTimer.IsRepeating = false;
        _autoUpdateTimer.Tick += OnAutoUpdateTimer;

        var loaded = _settingsStore.Load();
        _settings = loaded.Settings;
        _manager = new(DispatcherQueue);
        _screenPrivacy = new();
        _screenPrivacy.ResumeRequested += OnResumeScreenRequested;
        WindowList.ItemsSource = _manager.Windows;
        _manager.CollectionChanged += UpdateSummary;
        _manager.Notice += OnNotice;

        nint handle = WindowNative.GetWindowHandle(this);
        _picker = new(_manager, DispatcherQueue, handle);
        _picker.PreviewChanged += text => CandidateText.Text = text;
        _picker.Finished += OnPickFinished;

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

        AppWindow.Closing += OnWindowClosing;
        Closed += OnWindowClosed;

        ApplySettingsToControls();
        _loadingSettings = false;
        ApplyHotkey();
        // Upgrade from a development path to Velopack's stable launcher on first installed start.
        if (_settings.StartWithWindows)
        {
            string? startupError = StartupRegistration.SetEnabled(true);
            if (startupError is not null) SettingsStatusText.Text = startupError;
        }
        if (loaded.RecoveredFromInvalidFile)
        {
            SettingsStatusText.Text = "设置文件无法读取，已使用安全的默认设置。";
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
            () => { if (_screenPrivacy.IsActive) _screenPrivacy.RefreshDisplays(); },
            () => { if (_idleScope == AutoProtectionScope.AddedWindows)
                _manager.ReapplyIdleForcedClosed(); });
        if (!_privacyHotkeyRegistered)
            SettingsStatusText.Text = "Ctrl+Shift+F10 已被占用，可在首页操作全屏保护。";
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
        if (message == NativeMethods.WM_SIZE && unchecked((uint)wParam.ToInt64()) == NativeMethods.SIZE_MINIMIZED)
            AppWindow.Hide();
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string section = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "home";
        HomePage.Visibility = section == "home" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = section == "settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = section == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPickClick(object sender, RoutedEventArgs e) => StartPicker(WindowNative.GetWindowHandle(this));

    private void StartPicker(nint expectedForeground)
    {
        if (_picker.IsActive) return;
        try
        {
            _picker.Start(expectedForeground);
            PickButton.IsEnabled = false;
            PickButton.Content = "正在添加窗口";
            UpdateSummary();
        }
        catch (Exception ex)
        {
            OnNotice($"无法开始添加窗口：{ex.Message}");
        }
    }

    private void StartPickerFromHotkey()
    {
        if (_picker.IsActive) return;
        nint foreground = NativeMethods.GetForegroundWindow();
        if (foreground == nint.Zero || foreground == WindowNative.GetWindowHandle(this))
        {
            OnNotice("请先切换到需要保护的应用，再按快捷键添加窗口。");
            return;
        }
        try
        {
            _picker.Start(foreground);
            PickButton.IsEnabled = false;
            PickButton.Content = "正在添加窗口";
            UpdateSummary();
        }
        catch (Exception ex) { OnNotice($"无法开始添加窗口：{ex.Message}"); }
    }

    private void OnPickFinished(nint target, string message)
    {
        PickButton.IsEnabled = true;
        PickButton.Content = "添加窗口";
        CandidateText.Text = message;
        if (target != nint.Zero)
        {
            try
            {
                WindowList.SelectedItem = _manager.Attach(target);
                OnNotice("窗口已添加。");
            }
            catch (Exception ex) { OnNotice($"窗口尚未添加：{ex.Message}"); }
        }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        CountText.Text = $"已添加 {_manager.Windows.Count} 个窗口";
        bool hasWindows = _manager.Windows.Count > 0;
        ProtectAllMenuItem.IsEnabled = hasWindows && !_picker.IsActive;
        PauseAllMenuItem.IsEnabled = hasWindows && !_picker.IsActive;
        WindowList.IsEnabled = !_picker.IsActive;
        PickButton.IsEnabled = !_picker.IsActive;
        EmptyText.Visibility = hasWindows ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnNotice(string message)
    {
        StatusText.Text = message;
    }

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
        if (_manualScreenActive) { _manualScreenActive = false; UpdateFullScreen(); return; }
        try { _screenPrivacy.Enable(); _manualScreenActive = true; UpdateScreenButtons(); }
        catch (Exception ex) { OnNotice("无法启用全屏保护：" + ex.Message); }
    }
    private void ResumeScreen()
    {
        _manualScreenActive = false;
        ResumeAutomaticProtection();
        UpdateFullScreen();
    }
    private void ActivateIdleProtection(AutoProtectionScope scope)
    {
        if (_idleScope.HasValue || !_settings.AutoProtection.Enabled) return;
        if (scope == AutoProtectionScope.AddedWindows && !_manager.HasWindows) return;
        try
        {
            if (scope == AutoProtectionScope.AllDisplays) _screenPrivacy.Enable();
            else _manager.SetIdleForcedClosed(true);
            _idleScope = scope;
            UpdateScreenButtons();
        }
        catch (Exception ex) { OnNotice("自动保护启动失败：" + ex.Message); }
    }
    private void ResumeAutomaticProtection()
    {
        if (!_idleScope.HasValue) return;
        if (_idleScope == AutoProtectionScope.AddedWindows) _manager.SetIdleForcedClosed(false);
        _idleScope = null;
        UpdateFullScreen();
    }
    private void UpdateFullScreen()
    {
        if (_manualScreenActive || _idleScope == AutoProtectionScope.AllDisplays)
        {
            try { _screenPrivacy.Enable(); }
            catch (Exception ex) { _manualScreenActive = false; OnNotice("全屏保护恢复失败：" + ex.Message); }
        }
        else _screenPrivacy.Disable();
        UpdateScreenButtons();
    }
    private void UpdateScreenButtons()
    {
        ScreenPrivacyButton.Content = _screenPrivacy.IsActive ? "恢复屏幕" : "保护整个屏幕";
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
        OnNotice("已移除该窗口的保护。" );
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
            SettingsStatusText.Text = error;
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
            if (rollbackError is not null) SettingsStatusText.Text += $"；启动项回滚失败：{rollbackError}";
            return;
        }
        SettingsStatusText.Text = enabled ? "已为当前用户启用开机启动。" : "已关闭开机启动。";
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
                HotkeyStatusText.Text = "启用快捷键时，请选择 F1–F11 并至少保留一个修饰键。";
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
    }

    private void ApplyHotkey()
    {
        bool registered = _hotkey.Apply(_settings.Hotkey);
        HotkeyStatusText.Text = !_settings.Hotkey.Enabled ? "快捷键已关闭。" : registered
            ? "当前快捷键：" + DescribeHotkey(_settings.Hotkey)
            : _hotkey.LastError == NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED
                ? $"{DescribeHotkey(_settings.Hotkey)} 已被其他程序占用，请更改或关闭快捷键。"
                : $"{DescribeHotkey(_settings.Hotkey)} 注册失败（错误 {_hotkey.LastError}），请更改或关闭快捷键。";
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
        { SettingsStatusText.Text = $"设置未能保存：{ex.Message}"; return false; }
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
                        UpdateActionButton.Content = "安装更新";
                        UpdateActionButton.IsEnabled = false;
                        UpdateProgress.Visibility = Visibility.Visible;
                        UpdateProgress.Value = percent;
                        UpdateStatusText.Text = $"正在下载更新… {percent}%";
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
        UpdateActionButton.Content = "安装更新";
        UpdateActionButton.IsEnabled = !_updateInProgress && !_automaticUpdateInProgress;
        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = $"新版本 {version ?? ""} 已下载，随时可以安装。";
    }

    private void QueueUpdateDownloading()
    {
        DispatcherQueue.TryEnqueue(MarkUpdateDownloading);
    }

    private void MarkUpdateDownloading()
    {
        if (_disposed) return;
        UpdateActionButton.Content = "安装更新";
        UpdateActionButton.IsEnabled = false;
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateStatusText.Text = "发现新版本，正在下载更新…";
    }

    private void RefreshUpdateActionButton()
    {
        bool ready = _backgroundUpdates.ReadyVersion is not null;
        UpdateActionButton.Content = ready ? "安装更新" : "检查更新";
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
            UpdateStatusText.Text = "正在检查更新…";
            if (!_backgroundUpdates.IsInstalled)
            {
                UpdateStatusText.Text = "请安装 Curtain 后再检查更新。";
                return;
            }

            UpdateProgress.Value = 0;
            var result = await _backgroundUpdates.CheckAndDownloadAsync(percent =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_disposed) return;
                    UpdateActionButton.Content = "安装更新";
                    UpdateActionButton.IsEnabled = false;
                    UpdateProgress.Visibility = Visibility.Visible;
                    UpdateProgress.Value = percent;
                    UpdateStatusText.Text = $"正在下载更新… {percent}%";
                });
            }, _updateShutdown.Token, QueueUpdateDownloading);
            if (_disposed) return;
            switch (result.Kind)
            {
                case UpdateScanKind.Ready: MarkUpdateReady(result.Version); break;
                case UpdateScanKind.Busy: UpdateStatusText.Text = "正在后台检查或下载更新，请稍候。"; break;
                case UpdateScanKind.NotInstalled: UpdateStatusText.Text = "请安装 Curtain 后再检查更新。"; break;
                default: UpdateStatusText.Text = "当前已是最新版本。"; break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed) UpdateStatusText.Text = "检查更新失败：" + ex.Message; }
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
            UpdateStatusText.Text = "已下载的更新不存在，请重新检查更新。";
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "安装 Curtain " + ready,
            Content = "安装更新需要重新启动 Curtain，现有隐私遮罩会暂时关闭。是否现在安装？",
            PrimaryButtonText = "安装并重启",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || _disposed) return;
        try
        {
            if (!_backgroundUpdates.BeginApplyPending())
            {
                UpdateStatusText.Text = "更新文件已失效，请重新检查更新。";
                RefreshUpdateActionButton();
                return;
            }
            ExitCurtain();
        }
        catch (Exception ex) { if (!_disposed) UpdateStatusText.Text = "安装启动失败：" + ex.Message; }
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        AppWindow.Hide();
        OnNotice("Curtain 已隐藏到系统托盘，保护会继续运行。" );
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
        App.OnMainWindowClosed(this);
    }
}
