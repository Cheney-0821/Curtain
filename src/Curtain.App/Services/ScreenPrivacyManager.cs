using Curtain.App.Interop;
using Curtain.App.Windows;

namespace Curtain.App.Services;

internal sealed class ScreenPrivacyManager : IDisposable
{
    private readonly Dictionary<nint, (ScreenRect Bounds, ScreenPrivacyWindow Window)> _overlays = new();
    private bool _enabled;
    private bool _disposed;
    internal bool IsActive => _enabled;
    internal bool HasOverlays => _overlays.Count != 0;
    internal event Action? ResumeRequested;

    internal void Enable(bool animate = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_enabled) { RefreshDisplays(); return; }
        _enabled = true;
        try
        {
            RefreshDisplaysCore(showNewImmediately: false);
            long? batchStart = animate ? OverlayOpacityFade.CaptureBatchStart() : null;
            foreach (var (_, overlay) in _overlays.Values)
                overlay.SetProtectedVisible(true, animate, batchStart);
        }
        catch
        {
            _enabled = false;
            ClearOverlays();
            throw;
        }
    }

    internal void RefreshDisplays() => RefreshDisplaysCore(showNewImmediately: true);

    private void RefreshDisplaysCore(bool showNewImmediately)
    {
        var displays = PrivacyNative.Displays();
        var valid = displays.Select(d => d.Monitor).ToHashSet();
        var created = new List<(nint Monitor, ScreenPrivacyWindow Window)>();
        try
        {
            // Position every new overlay while hidden before showing any of them.
            foreach (var display in displays)
            {
                if (_overlays.TryGetValue(display.Monitor, out var current))
                {
                    if (current.Bounds != display)
                    {
                        current.Window.Cover(display);
                        _overlays[display.Monitor] = (display, current.Window);
                    }
                    continue;
                }
                if (!_enabled) continue;
                var window = new ScreenPrivacyWindow();
                window.ResumeRequested += OnResume;
                try
                {
                    window.Cover(display);
                    _overlays.Add(display.Monitor, (display, window));
                    created.Add((display.Monitor, window));
                }
                catch
                {
                    window.ResumeRequested -= OnResume;
                    window.CloseOverlay();
                    throw;
                }
            }
        }
        catch
        {
            foreach (var (monitor, window) in created)
            {
                _overlays.Remove(monitor);
                window.ResumeRequested -= OnResume;
                window.CloseOverlay();
            }
            throw;
        }

        if (_enabled && showNewImmediately)
            foreach (var (_, window) in created) window.SetProtectedVisible(true, animate: false);

        foreach (var gone in _overlays.Keys.Except(valid).ToArray())
        {
            var window = _overlays[gone].Window;
            _overlays.Remove(gone);
            window.ResumeRequested -= OnResume;
            window.CloseOverlay();
        }
    }

    private void OnResume()
    {
        if (_enabled) ResumeRequested?.Invoke();
    }

    internal void RefreshLocalization()
    {
        foreach (var (_, overlay) in _overlays.Values) overlay.RefreshLocalization();
    }

    internal void Disable(bool animate = true)
    {
        _enabled = false;
        if (animate)
        {
            long? batchStart = OverlayOpacityFade.CaptureBatchStart();
            foreach (var (_, window) in _overlays.Values)
                window.SetProtectedVisible(false, animate: true, batchStart);
            return;
        }

        foreach (var (_, window) in _overlays.Values) window.SetProtectedVisible(false, animate: false);
        ClearOverlays();
    }

    private void ClearOverlays()
    {
        var windows = _overlays.Values.Select(entry => entry.Window).ToArray();
        _overlays.Clear();
        foreach (var window in windows)
        {
            window.ResumeRequested -= OnResume;
            window.CloseOverlay();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disable(animate: false);
        ResumeRequested = null;
    }
}
