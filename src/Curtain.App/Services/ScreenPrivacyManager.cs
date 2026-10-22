using Curtain.App.Interop;
using Curtain.App.Windows;

namespace Curtain.App.Services;

internal sealed class ScreenPrivacyManager : IDisposable
{
    private readonly Dictionary<nint, (ScreenRect Bounds, ScreenPrivacyWindow Window)> _overlays = new();
    internal bool IsActive => _overlays.Count != 0;
    internal event Action? ResumeRequested;

    internal void Enable()
    {
        if (IsActive) { RefreshDisplays(); return; }
        try { RefreshDisplays(); }
        catch { Disable(); throw; }
    }

    internal void RefreshDisplays()
    {
        var displays = PrivacyNative.Displays();
        var valid = displays.Select(d => d.Monitor).ToHashSet();
        // Add screens before removing old overlays to minimize unprotected gaps.
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
            var window = new ScreenPrivacyWindow();
            window.ResumeRequested += OnResume;
            try { window.Cover(display); _overlays.Add(display.Monitor, (display, window)); }
            catch { window.ResumeRequested -= OnResume; window.CloseOverlay(); throw; }
        }
        foreach (var gone in _overlays.Keys.Except(valid).ToArray())
        {
            var window = _overlays[gone].Window;
            _overlays.Remove(gone);
            window.ResumeRequested -= OnResume;
            window.CloseOverlay();
        }
    }

    private void OnResume() => ResumeRequested?.Invoke();
    internal void Disable()
    {
        foreach (var (_, window) in _overlays.Values)
        {
            window.ResumeRequested -= OnResume;
            window.CloseOverlay();
        }
        _overlays.Clear();
    }
    public void Dispose() { Disable(); ResumeRequested = null; }
}
