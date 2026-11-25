using System.ComponentModel;
using Curtain.App.Interop;
using Curtain.App.Services;
using Curtain.Core.Geometry;
using Curtain.Core.Rendering;
using Microsoft.UI.Dispatching;

namespace Curtain.App.Windows;

internal sealed partial class CurtainWindow
{
    private DispatcherQueueTimer? _areaTimer;
    private PinWindow? _areaPin;
    private ClearArea? _hoveredArea;
    private PixelRect _primaryPinBounds;
    private PixelRect _areaPinBounds;
    private (PixelRect Viewport, float Scale, PixelRect PrimaryPin)? _areaPinLayout;
    private long _lastAreaHover;
    private long _areaFadeStarted;
    private double _areaOpacity, _areaFadeFrom, _areaFadeTarget;

    internal void SetPrimaryPinBounds(PixelRect bounds) => _primaryPinBounds = bounds;
    internal bool OwnsAreaControl(nint hwnd) => _areaPin?.Handle == hwnd || _areaResizeWindow?.Handle == hwnd;

    internal void SyncAreaControlZOrder()
    {
        // Protection can change Z-order bands when the target gains or loses focus.
        if (_areaResizeBounds.HasValue && _areaResizeWindow is { } resize)
            NativeMethods.EnsureAbove(resize.Handle, Handle);
        if (_areaPin is { IsVisible: true } pin)
            NativeMethods.EnsureAbove(pin.Handle, Handle);
    }

    private void RefreshClearAreaControl()
    {
        UpdateOutlineLayout();
        if (!_desiredVisible || !_clearRegionsAllowed || IsEditingClearRegions || _clearRegions.Count == 0)
        {
            EndAreaDrag(save: false);
            HideAreaResizeWindow();
            FinishAreaTransition();
            _areaTimer?.Stop();
            HideAreaPin();
            return;
        }

        if (_areaTimer is null)
        {
            _areaTimer = _dispatcher.CreateTimer();
            _areaTimer.Tick += OnAreaControlTick;
        }
        UpdateAreaTimerInterval();
        if (!_areaTimer.IsRunning) _areaTimer.Start();
    }

    private void UpdateAreaTimerInterval()
    {
        if (_areaTimer is not { } timer) return;
        var interval = TimeSpan.FromMilliseconds(_areaDrag is not null || _areaTransition is not null ? 16 : 32);
        if (timer.Interval != interval) timer.Interval = interval;
    }

    private void OnAreaControlTick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            TickAreaTransition();
            if (UpdateAreaDrag()) return;
            SyncAreaResizeWindow();
            UpdateAreaHover();
        }
        catch (Exception ex)
        {
            // Losing the hover control must leave the existing protection intact.
            EndAreaDrag(save: false);
            HideAreaResizeWindow();
            HideAreaPin();
            sender.Stop();
            CurtainDiagnostics.WriteException("overlay.clear-area.control-failed", ex,
                operationId: _operationId, stage: "window-protection", sessionId: _sessionId, ruleId: _ruleId);
        }
    }

    private void UpdateAreaHover()
    {
        if (!IsVisible || _protectionMenu is not null || HasSurfaceCapture ||
            !NativeMethods.GetCursorPos(out var cursor) || !NativeMethods.GetWindowRect(Handle, out var rect))
        {
            HideAreaPin();
            return;
        }

        var viewport = rect.ToPixelRect();
        var screen = new PixelPoint(cursor.X, cursor.Y);
        var local = new PixelPoint(cursor.X - rect.Left, cursor.Y - rect.Top);
        float scale = RegionScale;
        bool overPin = _hoveredArea is { } previous && ClearArea.Contains(_areaPinBounds with
        {
            Bottom = Math.Max(_areaPinBounds.Bottom, viewport.Top + (int)MathF.Round(previous.Bounds.Top * scale) + 1)
        }, screen);
        var area = _hoveredArea;
        if (!overPin && _areaPin?.IsPressed != true)
        {
            area = null;
            if (ClearArea.Contains(viewport, screen) &&
                !(_coveredPopup is { } popup && ClearArea.Contains(popup, local)))
                area = _clearRegions.Find(item => item.Tiles.Any(tile => ClearArea.Contains(ClearArea.ScaleTile(tile, scale), local)));
        }
        if (area is not null)
        {
            nint hit = NativeMethods.WindowFromPoint(cursor);
            nint root = hit == 0 ? 0 : NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOT);
            if (root != Handle && root != (nint)_protectedWindowHandle && !OwnsAreaControl(root))
                area = null;
        }

        long now = Environment.TickCount64;
        if (area is not null)
        {
            if (area != _hoveredArea) HideAreaPin();
            if (PositionAreaPin(area, viewport, scale))
            {
                _lastAreaHover = now;
                FadeAreaPin(1, now);
            }
        }
        else if (_hoveredArea is not null && now - _lastAreaHover >= 150)
            FadeAreaPin(0, now);

        if (_areaPin?.IsVisible != true) return;
        double duration = OverlayFadeCurve.DurationFor(_areaFadeFrom * 255, _areaFadeTarget * 255);
        double elapsed = now - _areaFadeStarted;
        _areaOpacity = OverlayFadeCurve.Sample(_areaFadeFrom, _areaFadeTarget, elapsed, duration);
        _areaPin.SetOpacity(_areaOpacity);
        if (_areaFadeTarget == 0 && elapsed >= duration) HideAreaPin();
    }

    private bool PositionAreaPin(ClearArea area, PixelRect viewport, float scale)
    {
        var layout = (viewport, scale, _primaryPinBounds);
        if (_hoveredArea == area && _areaPinLayout == layout && _areaPin?.IsVisible == true) return true;
        if (area.ControlBounds(viewport, scale) is not { } bounds)
        {
            HideAreaPin();
            return false;
        }
        if (_primaryPinBounds.IsValid && bounds.Left < _primaryPinBounds.Right && bounds.Right > _primaryPinBounds.Left &&
            bounds.Top < _primaryPinBounds.Bottom && bounds.Bottom > _primaryPinBounds.Top)
        {
            int top = Math.Min(viewport.Bottom - bounds.Height, _primaryPinBounds.Bottom + 2);
            bounds = bounds with { Top = top, Bottom = top + bounds.Height };
        }

        if (_areaPin is null)
        {
            var pin = new PinWindow(areaControl: true);
            if (!NativeMethods.SetOverlayOwner(pin.Handle, Handle, out int error))
            {
                pin.Close();
                throw new Win32Exception(error, "Unable to attach the clear-area control.");
            }
            pin.AreaPressed += BeginAreaMove;
            pin.AreaReleased += ReleaseAreaDrag;
            pin.AreaPointerMoved += ContinueAreaDragFromPointer;
            _areaPin = pin;
        }
        _areaPin.UpdateState(!area.IsClear);
        _areaPin.UpdatePlacement(new(bounds, false, false), (int)MathF.Round(96 * scale));
        _areaPin.SetOpacity(_areaOpacity);
        bool show = _areaPin.IsVisible;
        if (!NativeMethods.PositionGeometry(_areaPin.Handle, bounds, show))
            throw new Win32Exception("Unable to position the clear-area control.");
        if (!NativeMethods.EnsureAbove(_areaPin.Handle, Handle)) return false;
        if (!show) NativeMethods.ShowWindow(_areaPin.Handle, NativeMethods.SW_SHOWNOACTIVATE);
        _hoveredArea = area;
        _areaPinLayout = layout;
        _areaPinBounds = bounds;
        return true;
    }

    private void FadeAreaPin(double target, long now)
    {
        if (_areaFadeTarget == target) return;
        _areaFadeFrom = _areaOpacity;
        _areaFadeTarget = target;
        _areaFadeStarted = now;
    }

    private void HideAreaPin()
    {
        if (_areaPin?.IsVisible == true) NativeMethods.ShowWindow(_areaPin.Handle, 0);
        _hoveredArea = null;
        _areaPinLayout = null;
        _areaOpacity = _areaFadeFrom = _areaFadeTarget = 0;
    }

    private void ToggleHoveredArea()
    {
        if (_hoveredArea is not { } area || !_desiredVisible || !_clearRegionsAllowed || !IsVisible || IsEditingClearRegions)
            return;
        bool wasClear = area.IsClear;
        try { StartAreaTransition(area); }
        catch
        {
            area.IsClear = wasClear;
            ResetAreaTransition();
            UpdateClearRegionSurface();
            throw;
        }
        RebuildRegionOutlines();
        _areaPin?.UpdateState(!area.IsClear);
        CurtainDiagnostics.Write("overlay.clear-area.toggled", operationId: _operationId,
            stage: "window-protection", reasonCode: area.IsClear ? "Clear" : "Blurred",
            hwnd: Handle.ToInt64(), ownerHwnd: _protectedWindowHandle,
            targetProcessId: (int)_protectedProcessId, sessionId: _sessionId, ruleId: _ruleId);
        ClearRegionsChanged?.Invoke();
    }
}
