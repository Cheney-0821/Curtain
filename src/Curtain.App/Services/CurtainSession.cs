using System.Runtime.InteropServices;
using System.Diagnostics;
using Curtain.App.Interop;
using Curtain.App.Windows;
using Curtain.Core.Geometry;
using Curtain.Core.Sessions;
using Curtain.Core.Settings;
using Microsoft.UI.Dispatching;

namespace Curtain.App.Services;

internal sealed class CurtainSession : IDisposable
{
    private CurtainWindow? _curtain;
    private PinWindow? _pin;
    private WindowHooks? _hooks;
    private DragGesture? _drag;
    private (DockedPin Placement, PixelRect Target, int Dpi)? _lastPlacement;
    private bool _disposed;
    private bool? _lastClosed;
    private bool? _lastAppliedProtectionState;
    private bool _manualClosed = true;
    private bool _idleForcedClosed;
    private WindowProtectionSettings _defaults;
    internal WindowProtectionSettings? CustomProtection { get; private set; }
    internal WindowProtectionSettings EffectiveProtection => WindowBlurScale.Resolve(_defaults, CustomProtection);
    // A temporary per-window choice while this idle-protection cycle remains active.
    // Null means idle protection has not been overridden for this window.
    private bool? _idleUserOverrideClosed;
    private Process? _process;
    private string _applicationName = string.Empty;
    internal string SessionId { get; } = Guid.NewGuid().ToString("N");
    private readonly string _operationId;
    private string? _ruleId;
    internal string OperationId => _operationId;
    internal string? RuleId => _ruleId;
    internal void AssociateRememberedRule(Guid ruleId)
    {
        if (_disposed) return;
        _ruleId = ruleId.ToString("N");
        _curtain?.AssociateRememberedRule(_ruleId);
    }

    private nint Handle => (nint)Target.Identity.Handle;
    public ProtectedTarget Target { get; }
    public ProtectedWindowItem Item { get; }
    public bool IsDestroyed => _disposed || _hooks?.IsDestroyed == true;
    public event Action<CurtainSession, string>? Ended;
    public event Action<string>? Notice;
    internal bool WaitForFirstFrame(Action callback) => _curtain?.WaitForFirstFrame(callback) == true;

    internal CurtainSession(ProtectedTarget target, DispatcherQueue dispatcher, WindowProtectionSettings defaults,
        bool initiallyClosed = true, WindowProtectionSettings? customProtection = null,
        string? operationId = null, string? ruleId = null)
    {
        _operationId = operationId ?? CurtainDiagnostics.NewOperationId();
        _ruleId = ruleId;
        _defaults = defaults;
        CustomProtection = customProtection is null ? null : WindowBlurScale.Normalize(customProtection);
        Target = target;
        _manualClosed = initiallyClosed;
        Target.SetClosed(initiallyClosed);
        Item = new(this, Localization.LocalizationService.Get("ProtectedApplication"));
        string stage = "process-open";
        CurtainDiagnostics.Write("session.create.started", operationId: _operationId,
            stage: stage, reasonCode: initiallyClosed ? "InitiallyClosed" : "InitiallyOpen",
            hwnd: target.Identity.Handle, targetProcessId: (int)target.Identity.ProcessId,
            sessionId: SessionId, ruleId: _ruleId);
        try
        {
            _process = Process.GetProcessById((int)target.Identity.ProcessId);
            if (_process.StartTime.ToUniversalTime().Ticks != target.Identity.ProcessStartTicks)
                throw new InvalidOperationException("目标进程已经更换。");
            _ = _process.Handle;
            _applicationName = ReadApplicationName(_process);
            stage = "curtain-window-create";
            _curtain = new CurtainWindow(dispatcher, Target.Identity.Handle, Target.Identity.ProcessId,
                _operationId, SessionId, _ruleId);
            _curtain.RenderFailed += OnRenderFailed;
            _curtain.FirstFramePresented += OnFirstFramePresented;
            _curtain.SurfacePressed += OnPressed;
            _curtain.SurfaceMoved += OnMoved;
            _curtain.SurfaceEnded += CancelGesture;
            _pin = new PinWindow();
            _pin.ToggleRequested += Toggle;
            stage = "owner-assignment";
            if (!NativeMethods.SetOverlayOwner(_curtain.Handle, Handle) ||
                !NativeMethods.SetOverlayOwner(_pin.Handle, Handle))
                throw new InvalidOperationException("无法设置保护窗口的归属，未完成绑定。");
            CurtainDiagnostics.Write("session.overlay.owner-assigned", operationId: _operationId,
                stage: "owner-assignment", reasonCode: "CurtainOwnerAssigned",
                hwnd: _curtain.Handle.ToInt64(), ownerHwnd: Handle.ToInt64(), relatedHwnd: _pin.Handle.ToInt64(),
                targetProcessId: (int)Target.Identity.ProcessId, sessionId: SessionId, ruleId: _ruleId);
            CurtainDiagnostics.Write("session.overlay.owner-assigned", operationId: _operationId,
                stage: "owner-assignment", reasonCode: "PinOwnerAssigned",
                hwnd: _pin.Handle.ToInt64(), ownerHwnd: Handle.ToInt64(), relatedHwnd: _curtain.Handle.ToInt64(),
                targetProcessId: (int)Target.Identity.ProcessId, sessionId: SessionId, ruleId: _ruleId);
            stage = "target-hooks";
            _hooks = new WindowHooks(dispatcher, Handle, RefreshSafely);
            if (!IsCurrent()) throw new InvalidOperationException("目标已经关闭。");
            stage = "initial-refresh";
            Refresh(animateVisibilityChange: true);
            if (IsDestroyed) throw new InvalidOperationException("目标已经关闭。");
            CurtainDiagnostics.Write("session.create.completed", operationId: _operationId,
                stage: "initial-refresh", reasonCode: "Success", hwnd: target.Identity.Handle,
                targetProcessId: (int)target.Identity.ProcessId, sessionId: SessionId, ruleId: _ruleId);
            CurtainDiagnostics.Write("protection.state.changed", operationId: _operationId,
                stage: "session-create", reasonCode: Target.IsClosed ? "Closed" : "Open",
                hwnd: Target.Identity.Handle, targetProcessId: (int)Target.Identity.ProcessId,
                sessionId: SessionId, ruleId: _ruleId);
        }
        catch (Exception ex)
        {
            CurtainDiagnostics.WriteException("session.create.failed", ex, operationId: _operationId,
                stage: stage, reasonCode: "SessionCreateFailed", hwnd: Target.Identity.Handle,
                targetProcessId: (int)Target.Identity.ProcessId, sessionId: SessionId, ruleId: _ruleId);
            Dispose();
            throw;
        }
    }
    private void OnRenderFailed() { if (!_disposed) Ended?.Invoke(this, "OverlayErrorWindowRemoved"); }
    private void OnFirstFramePresented() => CurtainDiagnostics.Write("overlay.first-frame-presented",
        operationId: _operationId, stage: "curtain-window-paint", reasonCode: "Success",
        hwnd: Target.Identity.Handle, targetProcessId: (int)Target.Identity.ProcessId,
        sessionId: SessionId, ruleId: _ruleId);
    private static string ReadApplicationName(Process process)
    {
        try
        {
            var version = process.MainModule?.FileVersionInfo;
            if (!string.IsNullOrWhiteSpace(version?.FileDescription)) return version.FileDescription.Trim();
            if (!string.IsNullOrWhiteSpace(version?.ProductName)) return version.ProductName.Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        { }
        return process.ProcessName;
    }
    internal bool OwnsOverlay(nint hwnd) => _curtain?.Handle == hwnd || _pin?.Handle == hwnd;
    private bool IsCurrent() => _process is not null && TargetWindow.Matches(Target.Identity, _process);
    internal void Toggle() => SetClosed(!Target.IsClosed);
    internal void ConfigureWindowProtection(WindowProtectionSettings? custom, string? operationId = null)
    {
        if (_disposed) return;
        WindowProtectionSettings? previous = CustomProtection;
        CustomProtection = custom;
        CancelGesture();
        RefreshSafely();
        if (previous != CustomProtection)
            CurtainDiagnostics.Write("protection.settings.changed", operationId: operationId ?? _operationId,
                stage: "window-settings", reasonCode: custom is null ? "FollowGlobal" : "Custom",
                hwnd: Target.Identity.Handle, targetProcessId: (int)Target.Identity.ProcessId,
                sessionId: SessionId, ruleId: _ruleId);
    }
    internal void SetProtectionDefaults(WindowProtectionSettings defaults)
    {
        _defaults = defaults;
        if (CustomProtection is null) RefreshSafely();
    }
    internal void SetIdleForcedClosed(bool active, bool animate = false)
    {
        if (_disposed || _idleForcedClosed == active) return;
        _idleForcedClosed = active;
        _idleUserOverrideClosed = null;
        ApplyEffectiveState(animate);
    }

    internal void SetClosed(bool closed)
    {
        if (_disposed) return;
        if (_idleForcedClosed)
        {
            // Do not alter the manual state. The user may temporarily reveal
            // one window without revealing every other auto-protected window.
            _idleUserOverrideClosed = closed;
        }
        else _manualClosed = closed;
        ApplyEffectiveState(animate: true);
    }

    internal void ReapplyIdleForcedClosed()
    {
        if (_disposed || !_idleForcedClosed || _idleUserOverrideClosed is null) return;
        _idleUserOverrideClosed = null;
        ApplyEffectiveState(animate: false);
    }

    private void ApplyEffectiveState(bool animate)
    {
        CancelGesture();
        Target.SetClosed(_idleForcedClosed
            ? (_idleUserOverrideClosed ?? true)
            : _manualClosed);
        CurtainDiagnostics.Write("protection.state.changed", operationId: _operationId,
            stage: _idleForcedClosed ? "idle-protection" : "window-protection",
            reasonCode: Target.IsClosed ? "Closed" : "Open", hwnd: Target.Identity.Handle,
            targetProcessId: (int)Target.Identity.ProcessId, sessionId: SessionId, ruleId: _ruleId);
        RefreshSafely(animateVisibilityChange: animate, forceVisibilityUpdate: true);
    }
    internal void RefreshSafely() => RefreshSafely(animateVisibilityChange: false, forceVisibilityUpdate: false);

    private void RefreshSafely(bool animateVisibilityChange, bool forceVisibilityUpdate)
    {
        if (_disposed) return;
        try { Refresh(animateVisibilityChange, forceVisibilityUpdate); }
        catch (Exception ex)
        {
            CurtainDiagnostics.WriteException("session.refresh.failed", ex, operationId: _operationId,
                stage: "refresh", reasonCode: "SessionRefreshFailed", hwnd: Target.Identity.Handle,
                targetProcessId: (int)Target.Identity.ProcessId, sessionId: SessionId, ruleId: _ruleId);
            Ended?.Invoke(this, "OverlayErrorWindowRemoved");
        }
    }
    private void Refresh(bool animateVisibilityChange = false, bool forceVisibilityUpdate = false)
    {
        if (_curtain is null || _pin is null) return;
        if (IsDestroyed || !IsCurrent())
        {
            Ended?.Invoke(this, "TargetClosedWindowRemoved");
            return;
        }
        WindowMetadataDiagnostics.Poll(Handle);
        var bounds = NativeMethods.GetVisibleBounds(Handle);
        Target.SetSuspended(bounds is null);
        Item.Update(_applicationName, Target.IsClosed, Target.IsSuspended);
        if (bounds is null)
        {
            CancelGesture();
            NativeMethods.ShowWindow(_pin.Handle, NativeMethods.SW_HIDE);
            _curtain.CancelAndHide();
            _lastAppliedProtectionState = null;
            return;
        }
        if (_drag is not null && ((NativeMethods.GetAsyncKeyState(0x01) & 0x8000) == 0 ||
            (NativeMethods.GetAsyncKeyState(0x1B) & 0x8000) != 0 || NativeMethods.IsZoomed(Handle))) CancelGesture();
        int dpi = Math.Max(96, (int)NativeMethods.GetDpiForWindow(Handle));
        var monitor = NativeMethods.GetMonitorBounds(Handle, bounds.Value);
        var placement = PinPlacement.Calculate(bounds.Value, monitor.Work, monitor.Monitor, dpi, NativeMethods.IsZoomed(Handle));
        var current = (placement, bounds.Value, dpi);
        if (_lastPlacement != current)
        {
            _lastPlacement = current;
            CurtainDiagnostics.Write("overlay.geometry.changed", operationId: _operationId,
                stage: "window-overlay-geometry", reasonCode: "PositionRecalculated",
                hwnd: _curtain.Handle.ToInt64(), ownerHwnd: Handle.ToInt64(), relatedHwnd: _pin.Handle.ToInt64(),
                targetProcessId: (int)Target.Identity.ProcessId,
                left: bounds.Value.Left, top: bounds.Value.Top, width: bounds.Value.Width, height: bounds.Value.Height,
                metricName: "Dpi", metricValue: dpi, sessionId: SessionId, ruleId: _ruleId);
            CurtainDiagnostics.Write("overlay.pin.geometry.changed", operationId: _operationId,
                stage: "pin-placement", reasonCode: "PositionRecalculated",
                hwnd: _pin.Handle.ToInt64(), ownerHwnd: Handle.ToInt64(), relatedHwnd: _curtain.Handle.ToInt64(),
                targetProcessId: (int)Target.Identity.ProcessId,
                left: placement.Bounds.Left, top: placement.Bounds.Top,
                width: placement.Bounds.Width, height: placement.Bounds.Height,
                metricName: "Dpi", metricValue: dpi, sessionId: SessionId, ruleId: _ruleId);
        }
        if (_lastClosed != Target.IsClosed) { _lastClosed = Target.IsClosed; _pin.UpdateState(Target.IsClosed); }
        _pin.UpdatePlacement(placement, dpi);
        if (!NativeMethods.PositionAbove(_curtain.Handle, Handle, bounds.Value, showWindow: false))
            CurtainDiagnostics.Write("overlay.position.failed", CurtainDiagnosticLevel.Warning,
                operationId: _operationId, stage: "position-above-target", reasonCode: "PositionAboveFailed",
                hwnd: _curtain.Handle.ToInt64(), targetProcessId: (int)Target.Identity.ProcessId,
                errorCode: Marshal.GetLastPInvokeError(), sessionId: SessionId, ruleId: _ruleId);
        _curtain.SetTargetMaximized(NativeMethods.IsZoomed(Handle));
        _curtain.ConfigureWorkingProtection(_idleForcedClosed ? new WindowProtectionSettings() : EffectiveProtection);
        nint pinAnchor = Target.IsClosed || _curtain.IsVisible ? _curtain.Handle : Handle;
        NativeMethods.PositionAbove(_pin.Handle, pinAnchor, placement.Bounds);
        if (forceVisibilityUpdate || _lastAppliedProtectionState != Target.IsClosed)
        {
            _curtain.SetProtectedVisible(Target.IsClosed, animateVisibilityChange);
            _lastAppliedProtectionState = Target.IsClosed;
        }
    }
    private void OnPressed(PixelPoint point)
    {
        if (_disposed || !Target.IsClosed || !IsCurrent()) return;
        bool accepted = NativeMethods.SetForegroundWindow(Handle);
        RefreshSafely();
        bool activated = NativeMethods.GetForegroundWindow() == Handle;
        CurtainDiagnostics.Write("window.activation.result", activated ? CurtainDiagnosticLevel.Information : CurtainDiagnosticLevel.Warning,
            operationId: _operationId, stage: "protected-window-activation",
            reasonCode: accepted ? activated ? "ForegroundAccepted" : "ForegroundAcceptedButNotActive" : "ForegroundRejected",
            hwnd: Handle.ToInt64(), targetProcessId: (int)Target.Identity.ProcessId,
            errorCode: accepted ? 0 : Marshal.GetLastPInvokeError(), sessionId: SessionId, ruleId: _ruleId);
        if (!activated) Notice?.Invoke("TargetWindowNotForeground");
        if (_disposed || NativeMethods.GetVisibleBounds(Handle) is not { } visible ||
            !NativeMethods.GetWindowRect(Handle, out var raw)) return;
        if (_curtain?.HasSurfaceCapture != true)
        { Notice?.Invoke("PointerCaptureFailed"); return; }
        int dpi = Math.Max(96, (int)NativeMethods.GetDpiForWindow(Handle));
        var monitor = NativeMethods.GetMonitorBounds(Handle, visible);
        if (!DragGesture.CanMove(visible, monitor.Work, monitor.Monitor, NativeMethods.IsZoomed(Handle), dpi))
        { Notice?.Invoke("MoveNotSupported"); return; }
        _drag = new(point, raw.ToPixelRect(), Math.Max(1, NativeMethods.GetSystemMetricsForDpi(68, (uint)dpi) / 2),
            Math.Max(1, NativeMethods.GetSystemMetricsForDpi(69, (uint)dpi) / 2));
    }
    private void OnMoved(PixelPoint point)
    {
        if (_drag?.Move(point) is not { } next) return;
        if (!IsCurrent() || NativeMethods.IsIconic(Handle) || NativeMethods.IsZoomed(Handle) ||
            _curtain is null || _pin is null || NativeMethods.GetVisibleBounds(Handle) is not { } visible ||
            !NativeMethods.GetWindowRect(Handle, out var raw)) { CancelGesture(); return; }
        var monitor = NativeMethods.GetMonitorBounds(Handle, next);
        int dpi = Math.Max(96, (int)NativeMethods.GetDpiForWindow(Handle));
        var work = NativeMethods.GetWorkAreaAt(point, monitor.Work);
        next = DragGesture.KeepReachable(next, work,
            (int)Math.Round(64 * dpi / 96.0), (int)Math.Round(32 * dpi / 96.0));
        var nextVisible = DragGesture.TranslateVisibleBounds(raw.ToPixelRect(), visible, next);
        var placement = PinPlacement.Calculate(nextVisible, work, monitor.Monitor, dpi, false);
        _pin.UpdatePlacement(placement, dpi);
        bool moved = NativeMethods.MoveProtectedWindows(Handle, next, _curtain.Handle,
            nextVisible, _pin.Handle, placement.Bounds);
        if (!moved)
        {
            CurtainDiagnostics.Write("overlay.drag.failed", CurtainDiagnosticLevel.Warning,
                operationId: _operationId, stage: "move-protected-windows", reasonCode: "MoveProtectedWindowsFailed",
                hwnd: Handle.ToInt64(), relatedHwnd: _curtain.Handle.ToInt64(), ownerHwnd: _pin.Handle.ToInt64(),
                targetProcessId: (int)Target.Identity.ProcessId, errorCode: Marshal.GetLastPInvokeError(),
                sessionId: SessionId, ruleId: _ruleId);
            CancelGesture(); Notice?.Invoke("MoveFailed");
        }
        // The batch already positioned all three windows. WinEvent/watchdog reconcile actual bounds.
        if (!moved) RefreshSafely();
    }
    private void CancelGesture()
    {
        _drag?.End(); _drag = null;
        _curtain?.ReleaseSurfaceCapture();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var failures = new List<Exception>();
        void Release(string stage, Action cleanup)
        {
            try { cleanup(); }
            catch (Exception ex)
            {
                failures.Add(ex);
                CurtainDiagnostics.WriteException("session.cleanup.failed", ex, operationId: _operationId,
                    stage: stage, reasonCode: "ResourceDisposeFailed", hwnd: Target.Identity.Handle,
                    targetProcessId: (int)Target.Identity.ProcessId, sessionId: SessionId, ruleId: _ruleId);
            }
        }
        Release("item", Item.Dispose);
        Release("gesture", CancelGesture);
        var hooks = _hooks; _hooks = null;
        Release("hooks", () => hooks?.Dispose());
        var process = _process; _process = null;
        Release("process", () => process?.Dispose());
        var pin = _pin; _pin = null;
        if (pin is not null)
        {
            Release("pin-unsubscribe", () => pin.ToggleRequested -= Toggle);
            Release("pin-close", pin.Close);
        }
        var curtain = _curtain; _curtain = null;
        if (curtain is not null)
        {
            Release("overlay-unsubscribe", () =>
            {
                curtain.RenderFailed -= OnRenderFailed;
                curtain.FirstFramePresented -= OnFirstFramePresented;
                curtain.SurfacePressed -= OnPressed; curtain.SurfaceMoved -= OnMoved;
                curtain.SurfaceEnded -= CancelGesture;
            });
            Release("overlay-hide", curtain.CancelAndHide);
            Release("overlay-close", curtain.CloseOwnedWindow);
        }
        if (failures.Count > 0) throw new AggregateException("SessionCleanupIncomplete", failures);
    }
}
