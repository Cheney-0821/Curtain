using System.Diagnostics;
using Curtain.App.Localization;
using Curtain.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Curtain.App.Pages;

public sealed partial class DiagnosticsPage : UserControl, IDisposable
{
    private readonly CurtainEtwSessionController _etw = new();
    private readonly DiagnosticFeedbackUploader _feedbackUploader = new();
    private CancellationTokenSource? _uploadCancellation;
    private string? _lastBundlePath;
    private Guid _bundleRequestId;
    private string? _lastFeedbackId;
    private bool _uploadInProgress;
    private bool _feedbackDialogOpen;
    private DateTimeOffset _retryNotBefore;
    private readonly DispatcherQueueTimer _maintenanceTimer;
    private Func<bool, bool, bool>? _save;
    private Task _tracingTask = Task.CompletedTask;
    private bool _recordingEnabled = true;
    private bool _includeRawEtw = true;
    private bool _refreshing;
    private bool _busy;
    private bool _disposed;

    public DiagnosticsPage()
    {
        InitializeComponent();
        _maintenanceTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _maintenanceTimer.Interval = TimeSpan.FromMinutes(1);
        _maintenanceTimer.IsRepeating = true;
        _maintenanceTimer.Tick += OnMaintenanceTick;
        Localize();
        LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    internal void Configure(bool enabled, bool includeRawEtw, Func<bool, bool, bool> save)
    {
        _recordingEnabled = enabled;
        _includeRawEtw = includeRawEtw;
        _save = save;
        RefreshControls();
        _maintenanceTimer.Start();
        _ = ApplyTracingPolicyAsync();
    }

    private void Localize()
    {
        PageTitleText.Text = LocalizationService.Get("DiagnosticsTitle");
        RecordingTitleText.Text = LocalizationService.Get("DiagnosticsRecordingTitle");
        RecordingDescriptionText.Text = LocalizationService.Get("DiagnosticsRecordingDescription");
        RecordingSwitch.OnContent = LocalizationService.Get("ToggleOn");
        RecordingSwitch.OffContent = LocalizationService.Get("ToggleOff");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RecordingSwitch, RecordingTitleText.Text);
        IncludeRawEtwCheckBox.Content = LocalizationService.Get("DiagnosticsIncludeRawEtw");
        RawEtwDescriptionText.Text = LocalizationService.Get("DiagnosticsRawEtwDescription");
        BundleTitleText.Text = LocalizationService.Get("FeedbackBundleTitle");
        ViewBundleButton.Content = LocalizationService.Get("FeedbackViewBundle");
        OpenLogsFolderButton.Content = LocalizationService.Get("DiagnosticsViewLogs");
        FeedbackTitleText.Text = LocalizationService.Get("FeedbackTitle");
        FeedbackDescriptionText.Text = LocalizationService.Get("FeedbackDescription");
        FeedbackIssueTextBox.PlaceholderText = LocalizationService.Get("FeedbackIssuePlaceholder");
        FeedbackEmailTextBox.PlaceholderText = LocalizationService.Get("FeedbackEmailPlaceholder");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(FeedbackIssueTextBox, FeedbackIssueTextBox.PlaceholderText);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(FeedbackEmailTextBox, FeedbackEmailTextBox.PlaceholderText);
        FeedbackConsentText.Text = LocalizationService.Get("FeedbackConsent");
        OpenFeedbackButton.Content = LocalizationService.Get("FeedbackOpen");
        FeedbackDialog.Title = LocalizationService.Get("FeedbackTitle");
        FeedbackDialog.PrimaryButtonText = LocalizationService.Get("FeedbackSubmit");
        UpdateFeedbackControls();
    }

    private void OnLanguageChanged() => Localize();

    private void RefreshControls()
    {
        _refreshing = true;
        RecordingSwitch.IsOn = _recordingEnabled;
        IncludeRawEtwCheckBox.IsChecked = _includeRawEtw;
        _refreshing = false;
    }

    private async void OnRecordingToggled(object sender, RoutedEventArgs args)
    {
        if (_refreshing || _disposed || _save is null) return;
        if (!_save(RecordingSwitch.IsOn, _includeRawEtw)) { RefreshControls(); return; }
        _recordingEnabled = RecordingSwitch.IsOn;
        await ApplyTracingPolicyAsync();
    }

    private void OnRawEtwChanged(object sender, RoutedEventArgs args)
    {
        if (_refreshing || _disposed || _save is null) return;
        bool include = IncludeRawEtwCheckBox.IsChecked == true;
        if (!_save(_recordingEnabled, include)) { RefreshControls(); return; }
        _includeRawEtw = include;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RecordingSwitch.IsEnabled = !busy;
        GenerateBundleButton.IsEnabled = !busy;
        UpdateFeedbackControls();
    }

    private void UpdateFeedbackControls()
    {
        bool ready = !string.IsNullOrWhiteSpace(_lastBundlePath) && File.Exists(_lastBundlePath);
        FeedbackDialog.IsPrimaryButtonEnabled = !_disposed && !_busy && _lastFeedbackId is null &&
            DateTimeOffset.UtcNow >= _retryNotBefore &&
            FeedbackConsentCheckBox.IsChecked == true && ready;
        OpenFeedbackButton.IsEnabled = !_disposed && !_busy && !_feedbackDialogOpen;
        FeedbackDialog.CloseButtonText = LocalizationService.Get(_uploadInProgress ? "FeedbackCancel" : "FeedbackClose");
        FeedbackDialog.SecondaryButtonText = _lastFeedbackId is not null ? LocalizationService.Get("FeedbackCopy") : string.Empty;
        FeedbackDialog.IsSecondaryButtonEnabled = !_busy;
        FeedbackIssueTextBox.IsEnabled = !_busy && ready && _lastFeedbackId is null;
        FeedbackEmailTextBox.IsEnabled = !_busy && ready && _lastFeedbackId is null;
        FeedbackConsentCheckBox.IsEnabled = !_busy && ready && _lastFeedbackId is null;
        GenerateBundleButton.Content = LocalizationService.Get(ready ? "FeedbackRegenerate" : "DiagnosticsGenerateBundle");
        BundleDescriptionText.Text = ready ? Path.GetFileName(_lastBundlePath) : LocalizationService.Get("FeedbackBundleNotReady");
        ViewBundleButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnOpenFeedbackClick(object sender, RoutedEventArgs args)
    {
        if (_disposed || _feedbackDialogOpen) return;
        _feedbackDialogOpen = true;
        FeedbackDialog.XamlRoot = XamlRoot;
        FeedbackConsentCheckBox.IsChecked = false;
        UpdateFeedbackControls();
        try { await FeedbackDialog.ShowAsync(); }
        finally { _feedbackDialogOpen = false; if (!_disposed) UpdateFeedbackControls(); }
    }

    private void OnFeedbackDialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_uploadInProgress) _uploadCancellation?.Cancel();
        else if (_busy) args.Cancel = true;
    }

    private void OnFeedbackConsentChanged(object sender, RoutedEventArgs args)
    {
        if (!_disposed) UpdateFeedbackControls();
    }

    // JSONL is the default diagnostic source. ETW supplements it where permitted.
    // A rejected Windows trace must not turn off the local recording preference.
    private async Task ApplyTracingPolicyAsync()
    {
        if (_disposed || _busy) return;
        SetBusy(true);
        try
        {
            bool enabled = _recordingEnabled;
            _tracingTask = Task.Run(() =>
            {
                _etw.Reconcile();
                if (enabled)
                {
                    if (_etw.StartedAtUtc is { } started &&
                        DateTimeOffset.UtcNow - started >= TimeSpan.FromDays(1))
                        _etw.Stop(out _);
                    if (!_etw.IsRunning) _etw.Start(out _);
                }
                else _etw.Stop(out _);
            });
            await _tracingTask;
        }
        catch (Exception ex)
        {
            CurtainDiagnostics.WriteException("diagnostic.tracing-policy.failed", ex,
                stage: "recording-policy", reasonCode: "TraceUnavailable");
        }
        finally { if (!_disposed) SetBusy(false); }
    }

    private async void OnGenerateBundleClick(object sender, RoutedEventArgs args)
    {
        if (_disposed || _busy) return;
        // A failed new generation must never accidentally submit an earlier ZIP.
        _lastBundlePath = null;
        _lastFeedbackId = null;
        FeedbackConsentCheckBox.IsChecked = false;
        UpdateFeedbackControls();
        SetBusy(true);
        FeedbackStatusText.Text = LocalizationService.Get("DiagnosticsBundleGenerating");
        try
        {
            bool includeEtwTrace = true;
            _tracingTask = Task.Run(() => { if (_etw.IsRunning) includeEtwTrace = _etw.Stop(out _); });
            await _tracingTask;
            if (_disposed) return;
            bool includeRaw = _includeRawEtw;
            DiagnosticBundleResult result = await Task.Run(() =>
                new DiagnosticBundleBuilder().Create(includeRawEtw: includeRaw, includeEtwTrace: includeEtwTrace));
            if (_disposed) return;
            if (!result.Success || string.IsNullOrWhiteSpace(result.ArchivePath))
            {
                FeedbackStatusText.Text = LocalizationService.Get("DiagnosticsBundleFailed");
                return;
            }
            _lastBundlePath = result.ArchivePath;
            _bundleRequestId = Guid.NewGuid();
            _lastFeedbackId = null;
            FeedbackConsentCheckBox.IsChecked = false;
            FeedbackStatusText.Text = LocalizationService.Get("FeedbackReady");
            UpdateFeedbackControls();
        }
        catch (Exception ex)
        {
            CurtainDiagnostics.WriteException("diagnostic.bundle.ui-failed", ex,
                stage: "generate-button", reasonCode: "BundleUiFailure");
            if (!_disposed) FeedbackStatusText.Text = LocalizationService.Get("DiagnosticsBundleFailed");
        }
        finally
        {
            if (!_disposed)
            {
                SetBusy(false);
                await ApplyTracingPolicyAsync();
            }
        }
    }

    private async void OnSubmitFeedbackClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_disposed || _busy || FeedbackConsentCheckBox.IsChecked != true ||
            string.IsNullOrWhiteSpace(_lastBundlePath)) return;

        string archivePath = _lastBundlePath;
        _lastFeedbackId = null;
        _uploadCancellation = new CancellationTokenSource();
        _uploadInProgress = true;
        SetBusy(true);
        FeedbackStatusText.Text = LocalizationService.Get("FeedbackUploading");
        string operationId = CurtainDiagnostics.NewOperationId();
        CurtainDiagnostics.Write("feedback.upload.started", operationId: operationId,
            stage: "feedback", reasonCode: "UserConfirmed");
        try
        {
            DiagnosticFeedbackUploadResult result = await _feedbackUploader.UploadAsync(
                archivePath, FeedbackEmailTextBox.Text, FeedbackIssueTextBox.Text,
                _bundleRequestId, _uploadCancellation.Token);
            if (_disposed) return;
            if (result.RetryAfterSeconds > 0)
            {
                _retryNotBefore = DateTimeOffset.UtcNow.AddSeconds(result.RetryAfterSeconds);
                _ = ReleaseRetryAsync(result.RetryAfterSeconds);
            }
            CurtainDiagnostics.Write(result.Success ? "feedback.upload.completed" : "feedback.upload.failed",
                result.Success ? CurtainDiagnosticLevel.Information : CurtainDiagnosticLevel.Warning,
                operationId: operationId, stage: "feedback", reasonCode: result.ResultCode,
                metricName: "HttpStatus", metricValue: result.HttpStatusCode);
            if (result.Success && result.FeedbackId is { } feedbackId)
            {
                _lastFeedbackId = feedbackId;
                FeedbackStatusText.Text = LocalizationService.Get("FeedbackDone", feedbackId);
                FeedbackConsentCheckBox.IsChecked = false;
            }
            else
            {
                FeedbackStatusText.Text = LocalizationService.Get(result.ResultCode switch
                {
                    "ArchiveTooLarge" => "FeedbackTooLarge",
                    "ArchiveMissing" or "ArchiveInvalid" => "FeedbackArchiveMissing",
                    "EmailInvalid" => "FeedbackInvalidEmail",
                    "DescriptionInvalid" => "FeedbackInvalidIssue",
                    "RateLimited" => "FeedbackRateLimited",
                    "RequestIdConflict" => "FeedbackRequestIdConflict",
                    "NetworkUnavailable" or "Timeout" => "FeedbackNetwork",
                    "Cancelled" => "FeedbackCancelled",
                    "MultipartInvalid" or "VersionInvalid" or "RequestIdInvalid" or "ConsentRequired" => "FeedbackRequestInvalid",
                    "ProtocolUnavailable" or "ServiceUnavailable" => "FeedbackServiceUnavailable",
                    _ => "FeedbackFailed"
                });
            }
        }
        catch (Exception ex)
        {
            CurtainDiagnostics.WriteException("feedback.upload.ui-failed", ex,
                operationId: operationId, stage: "feedback", reasonCode: "UnexpectedFailure");
            if (!_disposed) FeedbackStatusText.Text = LocalizationService.Get("FeedbackFailed");
        }
        finally
        {
            _uploadInProgress = false;
            _uploadCancellation?.Dispose();
            _uploadCancellation = null;
            if (!_disposed) SetBusy(false);
        }
    }

    private void OnViewBundleClick(object sender, RoutedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(_lastBundlePath) || !File.Exists(_lastBundlePath)) return;
        try
        {
            var start = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true };
            start.ArgumentList.Add("/select," + _lastBundlePath);
            Process.Start(start);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            CurtainDiagnostics.WriteException("diagnostic.bundle.open-location-failed", ex,
                stage: "open-explorer", reasonCode: "ExplorerLaunchFailed");
            FeedbackStatusText.Text = LocalizationService.Get("DiagnosticsOpenFolderFailed");
        }
    }

    private async Task ReleaseRetryAsync(int seconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        if (!_disposed) UpdateFeedbackControls();
    }

    private void OnCopyFeedbackIdClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_lastFeedbackId is null) return;
        try
        {
            var data = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(_lastFeedbackId);
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            FeedbackStatusText.Text = LocalizationService.Get("FeedbackDone", _lastFeedbackId)
                + " " + LocalizationService.Get("FeedbackCopied");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            CurtainDiagnostics.WriteException("feedback.clipboard.failed", ex,
                stage: "feedback", reasonCode: "ClipboardUnavailable");
        }
    }

    private void OnOpenLogsFolderClick(object sender, RoutedEventArgs args)
    {
        try
        {
            Directory.CreateDirectory(CurtainDiagnostics.DirectoryPath);
            Process.Start(new ProcessStartInfo { FileName = CurtainDiagnostics.DirectoryPath, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            CurtainDiagnostics.WriteException("diagnostic.logs.open-folder-failed", ex,
                stage: "open-log-folder", reasonCode: "ExplorerLaunchFailed");
            StatusText.Text = LocalizationService.Get("DiagnosticsOpenFolderFailed");
        }
    }

    private async void OnMaintenanceTick(DispatcherQueueTimer sender, object args)
    {
        CurtainDiagnostics.Prune();
        if (!_busy && (_etw.NeedsReconciliation || (_etw.IsRunning && _etw.StartedAtUtc is { } started &&
            DateTimeOffset.UtcNow - started >= TimeSpan.FromDays(1))))
            await ApplyTracingPolicyAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _uploadCancellation?.Cancel();
        _maintenanceTimer.Stop();
        _maintenanceTimer.Tick -= OnMaintenanceTick;
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        _ = _tracingTask.ContinueWith(_ => _etw.Dispose(), TaskScheduler.Default);
    }
}
