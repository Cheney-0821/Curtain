using Curtain.App.Interop;
using Curtain.Core.Settings;
using Curtain.Core.Protection;
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
    private AutoProtectionSettings? _lastLoggedSettings;
    private bool _wasThresholdReached;
    private readonly IdleProtectionRearm _rearm = new();
    internal void ResetAfterResume()
    {
        _rearm.Resume(Environment.TickCount64);
        _wasThresholdReached = false;
    }
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
            var idle = settings.Enabled ? PrivacyNative.GetIdleDuration() : null;
            double idleSeconds = idle is null ? 0 : idle.Value.TotalSeconds;
            CurtainRuntimeStatus.SetAutoProtection(settings.Enabled, settings.IdleMinutes,
                settings.Scope.ToString(), _isAutoActive(), idleSeconds);
            if (_lastLoggedSettings != settings)
            {
                _lastLoggedSettings = settings;
                CurtainDiagnostics.Write("idle.settings", stage: "idle-monitor",
                    reasonCode: settings.Enabled ? "Enabled" : "Disabled",
                    errorCode: settings.IdleMinutes);
            }
            if (!settings.Enabled) return;
            bool reached = _rearm.AllowsActivation(Environment.TickCount64, settings.IdleMinutes) &&
                idle is not null && idle.Value >= TimeSpan.FromMinutes(settings.IdleMinutes);
            if (reached != _wasThresholdReached)
            {
                _wasThresholdReached = reached;
                CurtainDiagnostics.Write("idle.threshold", reached ? CurtainDiagnosticLevel.Information : CurtainDiagnosticLevel.Trace,
                    stage: "idle-threshold", reasonCode: reached ? "Reached" : "Reset",
                    errorCode: idle is null ? 0 : (int)Math.Min(int.MaxValue, idle.Value.TotalSeconds),
                    metricName: "IdleSeconds", metricValue: idleSeconds);
            }
            if (!reached) return;
            if (_isAutoActive())
                _reapplyOnIdle(); // Re-arm windows the user temporarily revealed.
            else
            {
                CurtainDiagnostics.Write("idle.protection.activate", stage: "idle-activation",
                    reasonCode: settings.Scope.ToString());
                _activate(settings.Scope);
            }
        }
        catch (Exception ex)
        {
            CurtainDiagnostics.WriteException("idle.monitor.failed", ex,
                stage: "idle-tick", reasonCode: "IdleMonitorFailure");
            System.Diagnostics.Debug.WriteLine("Curtain idle monitor: " + ex.Message);
        }
    }
    public void Dispose()
    {
        _timer.Stop(); _timer.Tick -= OnTick;
        CurtainDiagnostics.Write("idle.monitor.stopped", stage: "idle-monitor", reasonCode: "Disposed");
    }
}
