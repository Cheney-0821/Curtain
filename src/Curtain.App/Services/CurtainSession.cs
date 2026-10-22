using System.Runtime.InteropServices;
using System.Diagnostics;
using Curtain.App.Interop;
using Curtain.App.Windows;
using Curtain.Core.Geometry;
using Curtain.Core.Sessions;
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
    private bool _manualClosed = true;
    private bool _idleForcedClosed;
    // A temporary per-window choice while this idle-protection cycle remains active.
    // Null means idle protection has not been overridden for this window.
    private bool? _idleUserOverrideClosed;
    private Process? _process;
    private nint Handle => (nint)Target.Identity.Handle;
    public ProtectedTarget Target { get; }
    public ProtectedWindowItem Item { get; }
    public bool IsDestroyed => _disposed || _hooks?.IsDestroyed == true;
    public event Action<CurtainSession, string>? Ended;
    public event Action<string>? Notice;

    internal CurtainSession(ProtectedTarget target, DispatcherQueue dispatcher)
    {
        Target = target;
        Target.SetClosed(true);
        Item = new(this, NativeMethods.TitleOf(Handle));
        try
        {
            _process = Process.GetProcessById((int)target.Identity.ProcessId);
            if (_process.StartTime.ToUniversalTime().Ticks != target.Identity.ProcessStartTicks)
                throw new InvalidOperationException("目标进程已经更换。");
            _ = _process.Handle;
            _curtain = new CurtainWindow();
            _curtain.SurfacePressed += OnPressed;
            _curtain.SurfaceMoved += OnMoved;
            _curtain.SurfaceEnded += CancelGesture;
            _pin = new PinWindow();
            _pin.ToggleRequested += Toggle;
            if (!NativeMethods.SetOverlayOwner(_curtain.Handle, Handle) ||
                !NativeMethods.SetOverlayOwner(_pin.Handle, Handle))
                throw new InvalidOperationException("无法设置保护窗口的归属，未完成绑定。");
            DevelopmentDiagnostics.Write("overlay-owner", new { target = Target.Identity.Handle,
                curtainOwner = NativeMethods.GetWindow(_curtain.Handle, NativeMethods.GW_OWNER).ToInt64(),
                pinOwner = NativeMethods.GetWindow(_pin.Handle, NativeMethods.GW_OWNER).ToInt64() });
            _hooks = new WindowHooks(dispatcher, Handle, RefreshSafely);
            if (!IsCurrent()) throw new InvalidOperationException("目标已经关闭。");
            Refresh();
            if (IsDestroyed) throw new InvalidOperationException("目标已经关闭。");
        }
        catch { Dispose(); throw; }
    }
    internal bool OwnsOverlay(nint hwnd) => _curtain?.Handle == hwnd || _pin?.Handle == hwnd;
    private bool IsCurrent() => _process is not null && TargetWindow.Matches(Target.Identity, _process);
    internal void Toggle() => SetClosed(!Target.IsClosed);
    internal void SetIdleForcedClosed(bool active)
    {
        if (_disposed || _idleForcedClosed == active) return;
        _idleForcedClosed = active;
        _idleUserOverrideClosed = null;
        ApplyEffectiveState();
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
        ApplyEffectiveState();
    }

    internal void ReapplyIdleForcedClosed()
    {
        if (_disposed || !_idleForcedClosed || _idleUserOverrideClosed is null) return;
        _idleUserOverrideClosed = null;
        ApplyEffectiveState();
    }

    private void ApplyEffectiveState()
    {
        CancelGesture();
        Target.SetClosed(_idleForcedClosed
            ? (_idleUserOverrideClosed ?? true)
            : _manualClosed);
        RefreshSafely();
    }
    internal void RefreshSafely()
    {
        if (_disposed) return;
        try { Refresh(); }
        catch (Exception ex)
        {
            DevelopmentDiagnostics.Write("session-error", new { target = Target.Identity.Handle,
                exception = ex.GetType().Name, ex.HResult });
            Ended?.Invoke(this, "一个窗口的保护出现异常，已解除该窗口；其他窗口继续保护。");
        }
    }
    private void Refresh()
    {
        if (_curtain is null || _pin is null) return;
        if (IsDestroyed || !IsCurrent())
        {
            Ended?.Invoke(this, "目标窗口已关闭，自动解除该窗口的保护。");
            return;
        }
        WindowMetadataDiagnostics.Poll(Handle);
        var bounds = NativeMethods.GetVisibleBounds(Handle);
        Target.SetSuspended(bounds is null);
        Item.Update(NativeMethods.TitleOf(Handle), Target.IsClosed, Target.IsSuspended);
        if (bounds is null)
        {
            CancelGesture();
            NativeMethods.ShowWindow(_pin.Handle, NativeMethods.SW_HIDE);
            NativeMethods.ShowWindow(_curtain.Handle, NativeMethods.SW_HIDE);
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
            DevelopmentDiagnostics.Write("placement", new { target = Target.Identity.Handle, dpi,
                targetBounds = bounds.Value, placement, workArea = monitor.Work, monitorBounds = monitor.Monitor });
        }
        if (_lastClosed != Target.IsClosed) { _lastClosed = Target.IsClosed; _pin.UpdateState(Target.IsClosed); }
        _pin.UpdatePlacement(placement, dpi);
        if (Target.IsClosed)
        {
            NativeMethods.PositionAbove(_curtain.Handle, Handle, bounds.Value);
            _curtain.SetTargetMaximized(NativeMethods.IsZoomed(Handle));
            NativeMethods.PositionAbove(_pin.Handle, _curtain.Handle, placement.Bounds);
        }
        else
        {
            NativeMethods.ShowWindow(_curtain.Handle, NativeMethods.SW_HIDE);
            NativeMethods.PositionAbove(_pin.Handle, Handle, placement.Bounds);
        }
    }
    private void OnPressed(PixelPoint point)
    {
        if (_disposed || !Target.IsClosed || !IsCurrent()) return;
        bool accepted = NativeMethods.SetForegroundWindow(Handle);
        RefreshSafely();
        bool activated = NativeMethods.GetForegroundWindow() == Handle;
        DevelopmentDiagnostics.Write("activation", new { target = Target.Identity.Handle, accepted, activated });
        if (!activated) Notice?.Invoke("Windows暂未允许这个窗口置前；窗帘仍保持，请从任务栏切回目标。");
        if (_disposed || NativeMethods.GetVisibleBounds(Handle) is not { } visible ||
            !NativeMethods.GetWindowRect(Handle, out var raw)) return;
        if (_curtain?.HasSurfaceCapture != true)
        { Notice?.Invoke("这次鼠标捕获未成功，已保留单击置前；请重新按下后拖动。"); return; }
        int dpi = Math.Max(96, (int)NativeMethods.GetDpiForWindow(Handle));
        var monitor = NativeMethods.GetMonitorBounds(Handle, visible);
        if (!DragGesture.CanMove(visible, monitor.Work, monitor.Monitor, NativeMethods.IsZoomed(Handle), dpi))
        { Notice?.Invoke("最大化、全屏或贴靠窗口暂不拖动；可以单击置前，先还原后再拖动。"); return; }
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
            DevelopmentDiagnostics.Write("drag-failed", new { target = Target.Identity.Handle,
                error = Marshal.GetLastPInvokeError() });
            CancelGesture(); Notice?.Invoke("这个窗口未接受移动，已结束本次拖动。");
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
        CancelGesture(); _hooks?.Dispose(); _hooks = null;
        _process?.Dispose(); _process = null;
        if (_pin is not null) { _pin.ToggleRequested -= Toggle; _pin.Close(); _pin = null; }
        if (_curtain is not null)
        {
            _curtain.SurfacePressed -= OnPressed; _curtain.SurfaceMoved -= OnMoved;
            _curtain.SurfaceEnded -= CancelGesture; _curtain.CloseOwnedWindow(); _curtain = null;
        }
    }
}
