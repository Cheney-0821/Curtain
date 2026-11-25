using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using Curtain.App.Interop;
using Curtain.App.Services;
using Curtain.Core.Geometry;
using Windows.UI.Composition;

namespace Curtain.App.Windows;

internal sealed partial class CurtainWindow
{
    private const int ResizePadding = 4;
    private const double PreviewIntervalMs = 8;
    private ClearAreaResizeWindow? _areaResizeWindow;
    private PixelRect? _areaResizeBounds;
    private (float Scale, int Width, int Height, PixelRect? Popup) _areaBandsLayout;
    private bool _areaBandsDirty;
    private AreaDrag? _areaDrag;

    private sealed record AreaDrag(ClearArea Area, AreaResizeEdge Edge, DragGesture Gesture,
        nint Input, nint Foreground, PixelRect Frame, float Scale, PixelRect PinBounds)
    {
        internal PixelRect? Preview { get; set; }
        internal PixelPoint? LastPoint { get; set; }
        internal long LastPreviewTick { get; set; }
        internal CompositionContainerShape? Outline { get; set; }
        internal LiveAreaMask? LiveMask { get; set; }
    }

    private bool CanDragArea => _desiredVisible && IsVisible && _clearRegionsAllowed &&
        !IsEditingClearRegions && _protectionMenu is null && !HasSurfaceCapture;

    private void SyncAreaResizeWindow()
    {
        if (!CanDragArea || _clearRegions.All(area => area.IsClear) ||
            !NativeMethods.GetWindowRect(Handle, out var rect))
        {
            HideAreaResizeWindow();
            return;
        }
        if (_areaResizeWindow is null)
        {
            _areaResizeWindow = new(Handle, () => _areaDrag?.Edge ?? HitAreaResize()?.Edge ?? AreaResizeEdge.None);
            _areaResizeWindow.Pressed += BeginAreaResize;
            _areaResizeWindow.Released += ReleaseAreaDrag;
            _areaResizeWindow.ContextMenuRequested += RequestAreaBorderMenu;
            _areaResizeWindow.PointerMoved += ContinueAreaDragFromPointer;
            _areaBandsDirty = true;
        }
        var frame = rect.ToPixelRect();
        var layout = (Scale: RegionScale, frame.Width, frame.Height, Popup: _coveredPopup);
        if (_areaBandsDirty || _areaBandsLayout != layout)
        {
            int padding = (int)MathF.Ceiling(ResizePadding * layout.Scale);
            var bands = _clearRegions.Where(area => !area.IsClear).SelectMany(area => area.Edges)
                .Select(edge => new PixelRect(
                    Math.Max(0, (int)MathF.Round(edge.Start.X * layout.Scale) - padding),
                    Math.Max(0, (int)MathF.Round(edge.Start.Y * layout.Scale) - padding),
                    Math.Min(frame.Width, (int)MathF.Round(edge.End.X * layout.Scale) + padding + 1),
                    Math.Min(frame.Height, (int)MathF.Round(edge.End.Y * layout.Scale) + padding + 1)))
                .Where(band => band.IsValid);
            _areaResizeWindow.SetBands(bands, _coveredPopup);
            _areaBandsLayout = layout;
            _areaBandsDirty = false;
        }
        if (_areaResizeBounds == frame) return;
        if (!NativeMethods.PositionGeometry(_areaResizeWindow.Handle, frame, showWindow: true))
            throw new Win32Exception();
        NativeMethods.EnsureAbove(_areaResizeWindow.Handle, Handle);
        _areaResizeBounds = frame;
    }

    private void HideAreaResizeWindow()
    {
        if (_areaResizeBounds.HasValue && _areaResizeWindow is { } window)
            NativeMethods.ShowWindow(window.Handle, NativeMethods.SW_HIDE);
        _areaResizeBounds = null;
    }

    private (ClearArea Area, AreaResizeEdge Edge)? HitAreaResize()
    {
        if (!CanDragArea || !NativeMethods.GetCursorPos(out var cursor) ||
            !NativeMethods.GetWindowRect(Handle, out var rect)) return null;
        var screen = new PixelPoint(cursor.X, cursor.Y);
        if (ClearArea.Contains(_primaryPinBounds, screen) ||
            (_areaPin?.IsVisible == true && _areaPin.ContainsPoint(cursor))) return null;
        if (_coveredPopup is { } popup && ClearArea.Contains(popup, new(cursor.X - rect.Left, cursor.Y - rect.Top)))
            return null;
        var local = AreaPoint(cursor, rect.ToPixelRect(), RegionScale);
        foreach (var area in _clearRegions)
        {
            if (area.IsClear) continue;
            AreaResizeEdge edge = area.ResizeEdgeAt(local, ResizePadding);
            if (edge != AreaResizeEdge.None) return (area, edge);
        }
        return null;
    }

    private void BeginAreaResize()
    {
        if (HitAreaResize() is { } hit && _areaResizeWindow is { } input)
            BeginAreaDrag(hit.Area, hit.Edge, input.Handle);
    }

    private void RequestAreaBorderMenu()
    {
        if (_areaDrag is null && CanOpenProtectionMenu && NativeMethods.GetCursorPos(out var cursor))
            ProtectionMenuRequested?.Invoke(new(cursor.X, cursor.Y));
    }

    private void BeginAreaMove()
    {
        if (_hoveredArea is { } area && _areaPin is { } pin)
            BeginAreaDrag(area, AreaResizeEdge.None, pin.Handle);
    }

    private void BeginAreaDrag(ClearArea area, AreaResizeEdge edge, nint input)
    {
        if (_areaDrag is not null || !CanDragArea || !NativeMethods.GetCursorPos(out var cursor) ||
            !NativeMethods.GetWindowRect(Handle, out var rect)) return;
        float scale = RegionScale;
        var frame = rect.ToPixelRect();
        uint dpi = (uint)MathF.Round(96 * scale);
        int thresholdX = (int)MathF.Round(NativeMethods.GetSystemMetricsForDpi(68, dpi) / (2 * scale));
        int thresholdY = (int)MathF.Round(NativeMethods.GetSystemMetricsForDpi(69, dpi) / (2 * scale));
        var gesture = new DragGesture(AreaPoint(cursor, frame, scale), area.Bounds, thresholdX, thresholdY);
        _areaDrag = new(area, edge, gesture, input, NativeMethods.GetForegroundWindow(), frame, scale, _areaPinBounds);
        UpdateAreaTimerInterval();
        SetCapture(input);
    }

    private bool UpdateAreaDrag()
    {
        if (_areaDrag is not { } drag) return false;
        if (!CanContinueAreaDrag(drag))
            EndAreaDrag(save: false);
        else if ((NativeMethods.GetAsyncKeyState(0x01) & 0x8000) == 0)
            ReleaseAreaDrag();
        else
            PreviewAreaDrag(drag);
        return true;
    }

    private bool CanContinueAreaDrag(AreaDrag drag) =>
        CanDragArea && (NativeMethods.GetAsyncKeyState(0x1B) & 0x8000) == 0 &&
        NativeMethods.GetForegroundWindow() == drag.Foreground &&
        NativeMethods.GetWindowRect(Handle, out var rect) && rect.ToPixelRect() == drag.Frame &&
        RegionScale == drag.Scale;

    private void ContinueAreaDragFromPointer(nint input)
    {
        if (_areaDrag is not { } drag || drag.Input != input) return;
        long now = Stopwatch.GetTimestamp();
        if (drag.LastPreviewTick != 0 &&
            Stopwatch.GetElapsedTime(drag.LastPreviewTick, now).TotalMilliseconds < PreviewIntervalMs)
            return;
        if (CanContinueAreaDrag(drag)) PreviewAreaDrag(drag, now);
    }

    private void PreviewAreaDrag(AreaDrag drag, long now = 0)
    {
        if (!NativeMethods.GetCursorPos(out var cursor)) return;
        var point = AreaPoint(cursor, drag.Frame, drag.Scale);
        if (drag.LastPoint == point) return;
        drag.LastPoint = point;
        bool started = drag.Gesture.IsDragging;
        if (drag.Gesture.Move(point) is not { } moved) return;
        drag.LastPreviewTick = now == 0 ? Stopwatch.GetTimestamp() : now;
        if (!started)
        {
            FinishAreaTransition();
            if (!_regionOutlineGroups.TryGetValue(drag.Area, out var outline))
                outline = AddRegionOutline(drag.Area);
            drag.Outline = outline;
            if (drag.Edge != AreaResizeEdge.None)
            {
                HideAreaPin();
                foreach (var shape in outline.Shapes.OfType<CompositionSpriteShape>())
                    shape.IsStrokeNonScaling = true;
            }
            else if (_areaPin is { } areaPin)
                areaPin.AreaDragging = true;
            ClearAreaResizeWindow.UseCursor(drag.Edge);
        }
        var start = drag.Area.Bounds;
        var view = new PixelRect(0, 0, Math.Max(1, (int)MathF.Round(drag.Frame.Width / drag.Scale)),
            Math.Max(1, (int)MathF.Round(drag.Frame.Height / drag.Scale)));
        var next = drag.Area.AdjustBounds(view, new(moved.Left - start.Left, moved.Top - start.Top), drag.Edge);
        if (drag.Preview == next) return;
        drag.Preview = next;

        // The compositor moves the complete outline with one update per sample.
        float sx = (float)next.Width / start.Width, sy = (float)next.Height / start.Height;
        drag.Outline!.TransformMatrix = new Matrix3x2(sx, 0, 0, sy,
            next.Left - start.Left * sx, next.Top - start.Top * sy);
        if (!started && drag.Edge == AreaResizeEdge.None && drag.Area.IsClear)
            _ = StartLiveAreaMaskAsync(drag);
        else if (drag.LiveMask?.Ready == true)
            ApplyLiveAreaPosition(drag);

        if (drag.Edge == AreaResizeEdge.None && _areaPin is { } pin)
        {
            int dx = (int)MathF.Round((next.Left - start.Left) * drag.Scale);
            int dy = (int)MathF.Round((next.Top - start.Top) * drag.Scale);
            var b = drag.PinBounds;
            int x = Math.Clamp(b.Left + dx, drag.Frame.Left, drag.Frame.Right - b.Width);
            int y = Math.Clamp(b.Top + dy, drag.Frame.Top, drag.Frame.Bottom - b.Height);
            var pinBounds = new PixelRect(x, y, x + b.Width, y + b.Height);
            if (pinBounds != _areaPinBounds && NativeMethods.SetWindowPos(pin.Handle, 0, x, y, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER))
                _areaPinBounds = pinBounds;
        }
    }

    private void ReleaseAreaDrag()
    {
        if (_areaDrag is not { } drag) return;
        if (!CanContinueAreaDrag(drag))
        {
            EndAreaDrag(save: false);
            return;
        }
        PreviewAreaDrag(drag);
        bool toggle = !drag.Gesture.IsDragging && drag.Edge == AreaResizeEdge.None &&
            NativeMethods.GetCursorPos(out var cursor) && _areaPin?.ContainsPoint(cursor) == true;
        EndAreaDrag(save: true);
        if (toggle) ToggleHoveredArea();
    }

    private void EndAreaDrag(bool save)
    {
        if (_areaDrag is not { } drag) return;
        _areaDrag = null;
        bool hadLiveMask = drag.LiveMask is not null;
        drag.LiveMask?.Dispose();
        UpdateAreaTimerInterval();
        if (GetCapture() == drag.Input) ReleaseCapture();
        if (_areaPin is { } pin)
        {
            pin.AreaDragging = false;
            pin.ReleaseAreaPress();
        }
        bool changed = drag.Gesture.IsDragging;
        drag.Gesture.End();
        if (!changed) return;
        _areaPinLayout = null;
        if (save && drag.Preview is { } bounds)
        {
            var edited = ClearArea.MoveOrResize(_clearRegions, drag.Area, bounds);
            if (_hoveredArea == drag.Area) _hoveredArea = edited;
            _regionLayout = null;
            UpdateClearRegionSurface();
            if (edited != drag.Area)
            {
                CurtainDiagnostics.Write("overlay.clear-area.adjusted", operationId: _operationId,
                    stage: "window-protection", reasonCode: drag.Edge == AreaResizeEdge.None ? "Moved" : "Resized",
                    hwnd: Handle.ToInt64(), targetProcessId: (int)_protectedProcessId, sessionId: _sessionId, ruleId: _ruleId);
                ClearRegionsChanged?.Invoke();
            }
        }
        else if (hadLiveMask)
        {
            _regionLayout = null;
            UpdateClearRegionSurface();
        }
        RebuildRegionOutlines();
    }

    private static PixelPoint AreaPoint(NativeMethods.Point cursor, PixelRect frame, float scale) =>
        new((int)MathF.Round((cursor.X - frame.Left) / scale), (int)MathF.Round((cursor.Y - frame.Top) / scale));
}
