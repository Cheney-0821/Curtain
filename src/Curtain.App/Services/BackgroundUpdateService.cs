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
        if (!_manager.IsInstalled) return new(UpdateScanKind.NotInstalled);
        if (!await _operations.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new(UpdateScanKind.Busy);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_manager.UpdatePendingRestart is { } pending)
                return new(UpdateScanKind.Ready, pending.Version.ToString());

            var available = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (available is null) return new(UpdateScanKind.UpToDate);

            updateFound?.Invoke();
            await _manager.DownloadUpdatesAsync(available, progress, cancellationToken)
                .ConfigureAwait(false);
            return new(UpdateScanKind.Ready,
                _manager.UpdatePendingRestart?.Version.ToString() ?? available.TargetFullRelease.Version.ToString());
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
        _manager.WaitExitThenApplyUpdates(pending, silent: false, restart: true);
        return true;
    }
}
