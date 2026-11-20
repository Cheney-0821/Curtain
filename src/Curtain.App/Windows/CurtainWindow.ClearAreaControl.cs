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
    internal bool OwnsAreaPin(nint hwnd) => _areaPin?.Handle == hwnd;

    private void RefreshClearAreaControl()
    {
        UpdateOutlineLayout();
        if (!_desiredVisible || !_clearRegionsAllowed || IsEditingClearRegions || _clearRegions.Count == 0)
        {
            FinishAreaTransition();
            _areaTimer?.Stop();
            HideAreaPin();
            return;
        }

        if (_areaTimer is null)
        {
            _areaTimer = _dispatcher.CreateTimer();
            _areaTimer.Interval = TimeSpan.FromMilliseconds(32);
            _areaTimer.Tick += OnAreaControlTick;
        }
        if (!_areaTimer.IsRunning) _areaTimer.Start();
    }

    private void OnAreaControlTick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            TickAreaTransition();
            UpdateAreaHover();
        }
        catch (Exception ex)
        {
            // Losing the hover control must leave the existing protection intact.
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
        bool overPin = _hoveredArea is not null && ClearArea.Contains(_areaPinBounds, screen);
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
            if (root != Handle && root != (nint)_protectedWindowHandle && root != _areaPin?.Handle)
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
        if (_hoveredArea == area && _areaPinLayout == layout) return true;
        if (area.ControlBounds(viewport, scale) is not { } bounds)
        {
            HideAreaPin();
            return false;
        }
        if (bounds.Left < _primaryPinBounds.Right && bounds.Right > _primaryPinBounds.Left &&
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
            pin.ToggleRequested += ToggleHoveredArea;
            _areaPin = pin;
        }
        _areaPin.UpdateState(!area.IsClear);
        _areaPin.UpdatePlacement(new(bounds, false, false), (int)MathF.Round(96 * scale));
        _areaPin.SetOpacity(_areaOpacity);
        bool show = _areaPin.IsVisible;
        if (!NativeMethods.PositionGeometry(_areaPin.Handle, bounds, show))
            throw new Win32Exception("Unable to position the clear-area control.");
        if (!show) NativeMethods.ShowWindow(_areaPin.Handle, 4);
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
    }
}
