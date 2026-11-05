using Velopack;

namespace Curtain.App.Services;

internal enum UpdateScanKind
{
    NotInstalled,
    Busy,
    UpToDate,
    Ready
}

internal sealed record UpdateScanResult(UpdateScanKind Kind, string? Version = null);

/// <summary>
/// A single owner for both manual and background checks; Velopack downloads are
/// serialized and only an explicit user action installs/restarts the application.
/// </summary>
internal sealed class BackgroundUpdateService
{
    private readonly UpdateManager _manager;
    private readonly SemaphoreSlim _operations = new(1, 1);

    internal BackgroundUpdateService(LocalUpdateService factory)
    {
        _manager = factory.CreateManager();
    }

    internal bool IsInstalled => _manager.IsInstalled;
    internal string? ReadyVersion => _manager.UpdatePendingRestart?.Version.ToString();

    internal async Task<UpdateScanResult> CheckAndDownloadAsync(
        Action<int>? progress = null, CancellationToken cancellationToken = default,
        Action? updateFound = null)
    {
        string operationId = CurtainDiagnostics.NewOperationId();
        CurtainRuntimeStatus.SetUpdateState("Checking");
        CurtainDiagnostics.Write("update.check.started", operationId: operationId,
            stage: "update-check", reasonCode: "Started");
        if (!_manager.IsInstalled)
        {
            CurtainRuntimeStatus.SetUpdateState("NotInstalled");
            CurtainDiagnostics.Write("update.check.completed", operationId: operationId,
                stage: "update-check", reasonCode: "NotInstalled");
            return new(UpdateScanKind.NotInstalled);
        }
        bool acquired;
        try { acquired = await _operations.WaitAsync(0, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            CurtainRuntimeStatus.SetUpdateState("Cancelled");
            CurtainDiagnostics.Write("update.operation.cancelled", CurtainDiagnosticLevel.Information,
                operationId: operationId, stage: "operation-lock", reasonCode: "CancelledBeforeStart");
            throw;
        }
        if (!acquired)
        {
            CurtainRuntimeStatus.SetUpdateState("Busy");
            CurtainDiagnostics.Write("update.check.completed", operationId: operationId,
                stage: "operation-lock", reasonCode: "Busy");
            return new(UpdateScanKind.Busy);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_manager.UpdatePendingRestart is { } pending)
            {
                CurtainRuntimeStatus.SetUpdateState("Ready");
                CurtainDiagnostics.Write("update.check.completed", operationId: operationId,
                    stage: "pending-update", reasonCode: "Ready");
                return new(UpdateScanKind.Ready, pending.Version.ToString());
            }

            CurtainDiagnostics.Write("update.feed-check.started", operationId: operationId,
                stage: "check-for-updates", reasonCode: "Started");
            var available = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            CurtainRuntimeStatus.SetUpdateState(available is null ? "UpToDate" : "Available", checkedNow: true);
            if (available is null)
            {
                CurtainRuntimeStatus.SetUpdateState("UpToDate");
                CurtainDiagnostics.Write("update.check.completed", operationId: operationId,
                    stage: "check-for-updates", reasonCode: "UpToDate");
                return new(UpdateScanKind.UpToDate);
            }

            CurtainDiagnostics.Write("update.available", operationId: operationId,
                stage: "check-for-updates", reasonCode: "Available");
            updateFound?.Invoke();
            CurtainRuntimeStatus.SetUpdateState("Downloading");
            CurtainDiagnostics.Write("update.download.started", operationId: operationId,
                stage: "download", reasonCode: "Started");
            await _manager.DownloadUpdatesAsync(available, progress, cancellationToken)
                .ConfigureAwait(false);
            CurtainDiagnostics.Write("update.download.completed", operationId: operationId,
                stage: "download", reasonCode: "VelopackCompleted");
            bool staged = _manager.UpdatePendingRestart is not null;
            CurtainDiagnostics.Write(staged ? "update.package.staged" : "update.package.stage-unconfirmed",
                operationId: operationId, stage: "pending-restart", reasonCode: staged ? "PendingRestart" : "NotVisible");
            CurtainRuntimeStatus.SetUpdateState("Ready");
            return new(UpdateScanKind.Ready,
                _manager.UpdatePendingRestart?.Version.ToString() ?? available.TargetFullRelease.Version.ToString());
        }
        catch (OperationCanceledException)
        {
            CurtainRuntimeStatus.SetUpdateState("Cancelled");
            CurtainDiagnostics.Write("update.operation.cancelled", CurtainDiagnosticLevel.Information,
                operationId: operationId, stage: "update-operation", reasonCode: "Cancelled");
            throw;
        }
        catch (Exception ex)
        {
            CurtainRuntimeStatus.SetUpdateState("Failed", checkedNow: true);
            CurtainDiagnostics.WriteException("update.operation.failed", ex, operationId: operationId,
                stage: "update-operation", reasonCode: "UpdateOperationFailed");
            throw;
        }
        finally
        {
            _operations.Release();
        }
    }

    /// <summary>
    /// The caller MUST have obtained an explicit install-and-restart confirmation.
    /// Call on the UI thread; then cleanly close Curtain to release overlays and tray.
    /// </summary>
    internal bool BeginApplyPending()
    {
        var pending = _manager.UpdatePendingRestart;
        if (pending is null) return false;
        CurtainRuntimeStatus.SetUpdateState("Applying");
        CurtainDiagnostics.Write("update.apply.requested", stage: "install-and-restart", reasonCode: "UserConfirmed");
        _manager.WaitExitThenApplyUpdates(pending, silent: false, restart: true);
        CurtainDiagnostics.Write("update.apply.queued", stage: "install-and-restart", reasonCode: "VelopackQueued");
        return true;
    }
}
