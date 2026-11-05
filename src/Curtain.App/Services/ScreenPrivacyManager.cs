using Curtain.App.Interop;
using Curtain.App.Windows;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Curtain.Core.Geometry;
using Curtain.Core.Protection;

namespace Curtain.App.Services;

internal sealed class ScreenPrivacyManager : IDisposable
{
    private readonly Dictionary<nint, (ScreenRect Bounds, ScreenPrivacyWindow Window)> _overlays = new();
    private bool _enabled;
    private bool _disposed;
    private readonly DispatcherQueue _dispatcher;
    private FullScreenZOrderGuard? _zOrderGuard;
    private readonly Dictionary<nint, long> _lastUnresolvedRaise = new();
    private string? _operationId;
    private readonly FullscreenControlState _controlState = new();
    private readonly DispatcherQueueTimer _controlTimer;
    private long _visibleControlMonitor;
    internal bool IsActive => _enabled;
    internal bool HasOverlays => _overlays.Count != 0;
    internal event Action? ResumeRequested;

    internal ScreenPrivacyManager(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _controlTimer = dispatcher.CreateTimer();
        _controlTimer.Interval = TimeSpan.FromMilliseconds(50);
        _controlTimer.IsRepeating = true;
        _controlTimer.Tick += UpdateRestoreControl;
    }

    internal void Enable(bool animate = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_enabled) { RefreshDisplays(); return; }
        _operationId = CurtainDiagnostics.NewOperationId();
        CurtainDiagnostics.Write("fullscreen.enable.started", operationId: _operationId,
            stage: "screen-protection", reasonCode: animate ? "Animated" : "Immediate");
        _enabled = true;
        NativeMethods.GetCursorPos(out var entryPoint);
        _controlState.Reset(new(entryPoint.X, entryPoint.Y));
        _visibleControlMonitor = 0;
        CurtainRuntimeStatus.SetFullScreen(true, _overlays.Count);
        try
        {
            RefreshDisplaysCore(showNewImmediately: false);
            long? batchStart = animate ? OverlayOpacityFade.CaptureBatchStart() : null;
            foreach (var (bounds, overlay) in _overlays.Values)
            {
                overlay.Cover(bounds); // Reused HWNDs must also regain their Z-order before showing.
                overlay.SetControlVisible(false, animate: false);
                WriteOverlayGeometry("fullscreen.overlay.covered", overlay, bounds, "Positioned");
                overlay.SetProtectedVisible(true, animate, batchStart);
            }
            _zOrderGuard = new FullScreenZOrderGuard(_dispatcher, EnsureZOrder);
            _controlTimer.Start();
            CurtainDiagnostics.Write("fullscreen.enable.completed", operationId: _operationId,
                stage: "screen-protection", reasonCode: "Success");
        }
        catch (Exception ex)
        {
            CurtainDiagnostics.WriteException("fullscreen.enable.failed", ex, operationId: _operationId,
                stage: "screen-protection-create", reasonCode: "OverlayCreateFailed");
            _enabled = false;
            _controlTimer.Stop();
            CurtainRuntimeStatus.SetFullScreen(false, 0);
            _zOrderGuard?.Dispose();
            _zOrderGuard = null;
            ClearOverlays();
            throw;
        }
    }

    internal void RefreshDisplays() => RefreshDisplaysCore(showNewImmediately: true);

    private void UpdateRestoreControl(DispatcherQueueTimer sender, object args)
    {
        if (!_enabled || _disposed || !NativeMethods.GetCursorPos(out var cursor)) return;
        var point = new PixelPoint(cursor.X, cursor.Y);
        long monitor = 0;
        bool inRevealArea = false, over = false, pressed = false;
        foreach (var (bounds, window) in _overlays.Values.OrderByDescending(entry => entry.Window.ControlPressed))
        {
            var rect = new PixelRect(bounds.Left, bounds.Top, bounds.Left + bounds.Width, bounds.Top + bounds.Height);
            if (!FullscreenControlState.Contains(rect, point) && !window.ControlPressed) continue;
            monitor = bounds.Monitor.ToInt64();
            inRevealArea = FullscreenControlState.IsRevealArea(rect, point);
            over = window.IsOverControl(cursor);
            pressed = window.ControlPressed;
            break;
        }
        long visible = _controlState.Update(Environment.TickCount64, point, monitor, inRevealArea, over, pressed);
        if (visible != _visibleControlMonitor)
        {
            _visibleControlMonitor = visible;
            CurtainDiagnostics.Write("fullscreen.control.visibility-requested", operationId: _operationId,
                stage: "restore-control", reasonCode: visible == 0 ? "Hidden" : "Revealed");
        }
        foreach (var (bounds, window) in _overlays.Values)
            window.SetControlVisible(visible == bounds.Monitor.ToInt64(),
                animate: visible == 0 || visible == bounds.Monitor.ToInt64());
    }

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
                        WriteOverlayGeometry("fullscreen.overlay.geometry-changed", current.Window, display, "Positioned");
                    }
                    continue;
                }
                if (!_enabled) continue;
                var window = new ScreenPrivacyWindow();
                window.ResumeRequested += OnResume;
                window.VisibilityTransitionCompleted += OnOverlayVisibilityTransitionCompleted;
                try
                {
                    window.Cover(display);
                    _overlays.Add(display.Monitor, (display, window));
                    created.Add((display.Monitor, window));
                    CurtainDiagnostics.Write("fullscreen.overlay.created", operationId: _operationId,
                        stage: "cover-display", reasonCode: "Success", hwnd: window.Handle.ToInt64(),
                        left: display.Left, top: display.Top, width: display.Width, height: display.Height);
                }
                catch
                {
                    window.ResumeRequested -= OnResume;
                    window.VisibilityTransitionCompleted -= OnOverlayVisibilityTransitionCompleted;
                    CloseTrackedOverlay(window, display, "CoverFailed");
                    throw;
                }
            }
        }
        catch
        {
            foreach (var (monitor, window) in created)
            {
                _overlays.Remove(monitor, out var createdEntry);
                window.ResumeRequested -= OnResume;
                window.VisibilityTransitionCompleted -= OnOverlayVisibilityTransitionCompleted;
                CloseTrackedOverlay(window, createdEntry.Bounds, "RefreshRollback");
            }
            throw;
        }

        if (_enabled && showNewImmediately)
            foreach (var (_, window) in created) window.SetProtectedVisible(true, animate: false);

        foreach (var gone in _overlays.Keys.Except(valid).ToArray())
        {
            var window = _overlays[gone].Window;
            var bounds = _overlays[gone].Bounds;
            _overlays.Remove(gone);
            window.ResumeRequested -= OnResume;
            window.VisibilityTransitionCompleted -= OnOverlayVisibilityTransitionCompleted;
            CloseTrackedOverlay(window, bounds, "DisplayRemoved");
        }
        CurtainRuntimeStatus.SetFullScreen(_enabled, _enabled ? _overlays.Count : 0);
        EnsureZOrder();
        if (_enabled)
            foreach (var (bounds, window) in _overlays.Values)
                FullScreenZOrderDiagnostics.Sample(window.Handle, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
    }

    private void EnsureZOrder()
    {
        if (!_enabled || _disposed) return;
        long now = Environment.TickCount64;
        foreach (var (bounds, window) in _overlays.Values)
        {
            if (!window.IsVisible || PrivacyNative.FindFullScreenOccluder(window.Handle, bounds) == 0) continue;
            if (_lastUnresolvedRaise.TryGetValue(window.Handle, out long last) && now - last < 1000) continue;
            FullScreenZOrderDiagnostics.Sample(window.Handle, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            bool raised = PrivacyNative.RaiseFullScreen(window.Handle);
            CurtainDiagnostics.Write("fullscreen.zorder.checked", CurtainDiagnosticLevel.Warning,
                operationId: _operationId, stage: "raise-fullscreen", reasonCode: raised ? "Raised" : "RaiseFailed",
                hwnd: window.Handle.ToInt64(), errorCode: raised ? 0 : Marshal.GetLastPInvokeError());
            if (!raised || PrivacyNative.FindFullScreenOccluder(window.Handle, bounds) != 0)
                _lastUnresolvedRaise[window.Handle] = now;
            else
                _lastUnresolvedRaise.Remove(window.Handle);
            FullScreenZOrderDiagnostics.Sample(window.Handle, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        }
    }

    private void OnResume()
    {
        if (_enabled) ResumeRequested?.Invoke();
    }

    private void OnOverlayVisibilityTransitionCompleted(ScreenPrivacyWindow window, bool visible)
    {
        if (!_overlays.Values.Any(entry => ReferenceEquals(entry.Window, window))) return;
        var entry = _overlays.Values.First(value => ReferenceEquals(value.Window, window));
        bool isVisible = NativeMethods.IsWindow(window.Handle) && NativeMethods.IsWindowVisible(window.Handle);
        CurtainDiagnostics.Write("fullscreen.overlay.visibility-completed",
            visible && !isVisible ? CurtainDiagnosticLevel.Warning : CurtainDiagnosticLevel.Information,
            operationId: _operationId, stage: visible ? "show" : "hide",
            reasonCode: isVisible == visible ? "Success" : "VisibilityMismatch", hwnd: window.Handle.ToInt64(),
            left: entry.Bounds.Left, top: entry.Bounds.Top, width: entry.Bounds.Width, height: entry.Bounds.Height);
    }

    private void WriteOverlayGeometry(string eventName, ScreenPrivacyWindow window, ScreenRect bounds, string reasonCode) =>
        CurtainDiagnostics.Write(eventName, operationId: _operationId, stage: "screen-overlay-geometry",
            reasonCode: reasonCode, hwnd: window.Handle.ToInt64(),
            left: bounds.Left, top: bounds.Top, width: bounds.Width, height: bounds.Height);

    private void CloseTrackedOverlay(ScreenPrivacyWindow window, ScreenRect bounds, string reasonCode)
    {
        nint hwnd = window.Handle;
        window.CancelAndHide();
        bool hidden = !NativeMethods.IsWindowVisible(hwnd);
        window.CloseOverlay();
        bool destroyed = !NativeMethods.IsWindow(hwnd);
        CurtainDiagnostics.Write(destroyed && hidden ? "fullscreen.overlay.destroyed" : "fullscreen.overlay.cleanup-failed",
            destroyed && hidden ? CurtainDiagnosticLevel.Information : CurtainDiagnosticLevel.Warning,
            operationId: _operationId, stage: "overlay-cleanup",
            reasonCode: !hidden ? "HideFailed" : !destroyed ? "DestroyWindowIncomplete" : reasonCode, hwnd: hwnd.ToInt64(),
            left: bounds.Left, top: bounds.Top, width: bounds.Width, height: bounds.Height,
            errorCode: destroyed ? 0 : Marshal.GetLastPInvokeError());
    }

    internal void RefreshLocalization()
    {
        foreach (var (_, overlay) in _overlays.Values) overlay.RefreshLocalization();
    }

    internal void Disable(bool animate = true)
    {
        if (!_enabled && _overlays.Count == 0) return;
        CurtainDiagnostics.Write("fullscreen.disable.started", operationId: _operationId,
            stage: "screen-protection", reasonCode: animate ? "Animated" : "Immediate");
        _enabled = false;
        _controlTimer.Stop();
        CurtainRuntimeStatus.SetFullScreen(false, 0);
        _zOrderGuard?.Dispose();
        _zOrderGuard = null;
        _lastUnresolvedRaise.Clear();
        FullScreenZOrderDiagnostics.Reset();
        if (animate)
        {
            long? batchStart = OverlayOpacityFade.CaptureBatchStart();
            foreach (var (bounds, window) in _overlays.Values)
            {
                WriteOverlayGeometry("fullscreen.overlay.hide-requested", window, bounds, "Animated");
                window.SetProtectedVisible(false, animate: true, batchStart);
            }
            return;
        }

        foreach (var (bounds, window) in _overlays.Values)
        {
            WriteOverlayGeometry("fullscreen.overlay.hide-requested", window, bounds, "Immediate");
            window.SetProtectedVisible(false, animate: false);
        }
        ClearOverlays();
        CurtainDiagnostics.Write("fullscreen.disable.completed", operationId: _operationId,
            stage: "screen-protection", reasonCode: "Success");
    }

    private void ClearOverlays()
    {
        var entries = _overlays.Values.ToArray();
        _overlays.Clear();
        foreach (var entry in entries)
        {
            var window = entry.Window;
            window.ResumeRequested -= OnResume;
            window.VisibilityTransitionCompleted -= OnOverlayVisibilityTransitionCompleted;
            CloseTrackedOverlay(window, entry.Bounds, "ProtectionEnded");
        }
        CurtainRuntimeStatus.SetFullScreen(false, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _controlTimer.Tick -= UpdateRestoreControl;
        Disable(animate: false);
        ResumeRequested = null;
    }
}
