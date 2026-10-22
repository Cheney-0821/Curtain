using Curtain.App.Interop;
using Curtain.Core.Settings;
using Microsoft.UI.Dispatching;

namespace Curtain.App.Services;

/// <summary>Polls the Windows session's last keyboard/mouse input, not key contents.</summary>
internal sealed class IdleProtectionMonitor : IDisposable
{
    private readonly DispatcherQueueTimer _timer;
    private readonly Func<AutoProtectionSettings> _getSettings;
    private readonly Action<AutoProtectionScope> _activate;
    private readonly Action _refreshScreens;
    private readonly Func<bool> _isAutoActive;
    private readonly Action _reapplyOnIdle;
    internal IdleProtectionMonitor(DispatcherQueue dispatcher, Func<AutoProtectionSettings> getSettings,
        Func<bool> isAutoActive, Action<AutoProtectionScope> activate, Action refreshScreens,
        Action reapplyOnIdle)
    {
        _getSettings = getSettings;
        _isAutoActive = isAutoActive;
        _activate = activate;
        _refreshScreens = refreshScreens;
        _reapplyOnIdle = reapplyOnIdle;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.IsRepeating = true;
        _timer.Tick += OnTick;
        _timer.Start();
    }
    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            _refreshScreens();
            var settings = _getSettings();
            if (!settings.Enabled) return;
            var idle = PrivacyNative.GetIdleDuration();
            if (idle is null || idle.Value < TimeSpan.FromMinutes(settings.IdleMinutes)) return;
            if (_isAutoActive())
                _reapplyOnIdle(); // Re-arm windows the user temporarily revealed.
            else
                _activate(settings.Scope);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Curtain idle monitor: " + ex.Message); }
    }
    public void Dispose() { _timer.Stop(); _timer.Tick -= OnTick; }
}
