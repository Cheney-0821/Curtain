using System.Numerics;
using System.Runtime.InteropServices;
using Curtain.App.Interop;
using Curtain.App.Localization;
using Curtain.App.Services;
using Curtain.Core.Geometry;
using Windows.UI;
using Windows.UI.Composition;

namespace Curtain.App.Windows;

internal sealed partial class CurtainWindow
{
    private const int RegionDifference = 4;
    private List<ClearArea> _clearRegions = [];
    private List<ClearArea>? _regionDraft;
    private PixelPoint? _drawStart;
    private ShapeVisual? _regionOutlines;
    private CompositionColorBrush? _regionInk;
    private CompositionColorBrush? _regionTint;
    private readonly List<(CompositionGeometry Geometry, CompositionSpriteShape Shape)> _regionShapes = [];
    private CompositionRectangleGeometry? _regionPreview;
    private Action<bool>? _regionEditFinished;
    private RegionEditorFeedback? _regionFeedback;
    private nint _regionCrosshair;
    private bool _trackingEditorMouse;
    private bool _regionMenuOpen;
    private bool _clearRegionsAllowed;
    private bool _hasWindowRegion;
    private PixelRect? _coveredPopup;
    private (Vector2 Size, float Scale, PixelRect? CoveredPopup, bool UseAreas)? _regionLayout;

    internal bool IsEditingClearRegions => _regionDraft is not null;
    private float RegionScale => Math.Max(96, NativeMethods.GetDpiForWindow(Handle)) / 96f;

    internal void AllowClearRegions(bool allowed, PixelRect? coveredPopup = null)
    {
        _clearRegionsAllowed = allowed;
        _coveredPopup = coveredPopup;
        if (!allowed) _protectionMenu?.Dismiss();
        UpdateClearRegionSurface();
        RefreshClearAreaControl();
    }

    private void UpdateClearRegionSurface()
    {
        UpdateOutlineLayout();
        var layout = (Size: _visual.Size, Scale: RegionScale, CoveredPopup: _coveredPopup,
            UseAreas: _clearRegionsAllowed && !IsEditingClearRegions);
        if (_regionLayout == layout) return;
        int width = (int)layout.Size.X, height = (int)layout.Size.Y;
        var transitioning = layout.UseAreas ? _areaTransition?.Area : null;
        var holes = layout.UseAreas
            ? _clearRegions.Where(area => area.IsClear && area != transitioning)
                .SelectMany(area => area.VisibleTiles(width, height, layout.Scale, layout.CoveredPopup)).ToArray()
            : [];
        var animated = transitioning?.VisibleTiles(width, height, layout.Scale, layout.CoveredPopup).ToArray() ?? [];

        UpdateAreaBlurClips(new(0, 0, width, height), holes, animated);
        if (holes.Length == 0)
        {
            if (_hasWindowRegion)
            {
                if (SetWindowRgn(Handle, 0, IsVisible) == 0)
                    throw new System.ComponentModel.Win32Exception();
                _hasWindowRegion = false;
            }
            _regionLayout = layout;
            return;
        }

        nint region = CreateRectRgn(0, 0, width, height);
        try
        {
            if (region == 0) throw new System.ComponentModel.Win32Exception();
            foreach (var tile in holes)
            {
                nint cutout = CreateRectRgn(tile.Left, tile.Top, tile.Right, tile.Bottom);
                if (cutout == 0) throw new System.ComponentModel.Win32Exception();
                try
                {
                    if (CombineRgn(region, region, cutout, RegionDifference) == 0)
                        throw new System.ComponentModel.Win32Exception();
                }
                finally { HandleNative.DeleteObject(cutout); }
            }

            if (SetWindowRgn(Handle, region, IsVisible) == 0)
                throw new System.ComponentModel.Win32Exception();
            region = 0; // Windows owns the region after a successful call.
            _regionLayout = layout;
            _hasWindowRegion = true;
        }
        finally { if (region != 0) HandleNative.DeleteObject(region); }
    }

    internal bool BeginClearRegionEdit(PixelRect bounds, int dpi, Action<bool> finished)
    {
        if (_disposed || IsEditingClearRegions) return false;
        CancelAndHide();
        _regionDraft = new(_clearRegions);
        _regionEditFinished = finished;
        try
        {
            _input.SetPointerPassthrough(false);
            RebuildRegionOutlines();
            MainBlurSurface.Brush = null;
            _regionLayout = null;
            UpdateClearRegionSurface();
            NativeMethods.ShowWindow(Handle, 4);
            if (NativeMethods.SetForegroundWindow(Handle) && NativeMethods.GetForegroundWindow() == Handle)
            {
                _regionCrosshair = HandleNative.LoadCursorW(0, (nint)32515); // IDC_CROSS
                try
                {
                    _regionFeedback = new RegionEditorFeedback(Handle, LocalizationService.Get("ClearRegionDrawPrompt"));
                }
                catch (Exception ex)
                {
                    CurtainDiagnostics.WriteException("overlay.region-feedback.failed", ex, stage: "create");
                }
                UpdateRegionEditorTarget(bounds, dpi);
                TrackEditorMouse();
                CurtainDiagnostics.Write("overlay.region-edit.started", operationId: _operationId,
                    stage: "window-protection", reasonCode: "Ready", hwnd: Handle.ToInt64(),
                    ownerHwnd: _protectedWindowHandle, targetProcessId: (int)_protectedProcessId,
                    sessionId: _sessionId, ruleId: _ruleId);
                return true;
            }
        }
        catch (Exception ex)
        {
            CurtainDiagnostics.WriteException("overlay.region-edit.failed", ex, stage: "begin");
        }
        EndClearRegionEdit(save: false);
        return false;
    }

    internal void EndClearRegionEdit(bool save, bool notify = true)
    {
        if (_regionDraft is not { } regions) return;
        _regionDraft = null;
        _drawStart = null;
        var finished = _regionEditFinished;
        _regionEditFinished = null;
        bool wasForeground = NativeMethods.GetForegroundWindow() == Handle;
        if (save) _clearRegions = regions;
        _regionLayout = null;
        _regionFeedback?.Dispose();
        _regionFeedback = null;
        _trackingEditorMouse = false;
        if (_regionCrosshair != 0 && GetCursor() == _regionCrosshair)
            SetCursor(HandleNative.LoadCursorW(0, (nint)32512));
        _regionCrosshair = 0;
        ReleaseSurfaceCapture();
        MainBlurSurface.Brush = _effect;
        ApplyBlur();
        RebuildRegionOutlines();
        if (!notify)
        {
            NativeMethods.ShowWindow(Handle, 0);
            return;
        }
        if (save)
            CurtainDiagnostics.Write("overlay.clear-regions.saved", operationId: _operationId,
                stage: "window-protection", reasonCode: "Saved", hwnd: Handle.ToInt64(),
                ownerHwnd: _protectedWindowHandle, targetProcessId: (int)_protectedProcessId,
                metricName: "RegionCount", metricValue: _clearRegions.Count, sessionId: _sessionId, ruleId: _ruleId);
        try { _input.SetPointerPassthrough(_settings.AllowClickThrough); }
        finally { finished?.Invoke(save); }
        if (wasForeground && NativeMethods.IsWindow((nint)_protectedWindowHandle))
            NativeMethods.SetForegroundWindow((nint)_protectedWindowHandle);
    }

    private PixelPoint EditorPoint()
    {
        NativeMethods.GetCursorPos(out var point);
        ScreenToClient(Handle, ref point);
        var local = Vector2.Clamp(new(point.X, point.Y), Vector2.Zero, _visual.Size) / RegionScale;
        return new((int)MathF.Round(local.X), (int)MathF.Round(local.Y));
    }

    private static PixelRect Between(PixelPoint start, PixelPoint end) =>
        new(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Max(start.X, end.X), Math.Max(start.Y, end.Y));

    internal void UpdateRegionEditorTarget(PixelRect bounds, int dpi)
    {
        try { _regionFeedback?.UpdateTarget(bounds, dpi); }
        catch (Exception ex)
        {
            CurtainDiagnostics.WriteException("overlay.region-feedback.failed", ex, stage: "target");
            _regionFeedback?.Dispose();
            _regionFeedback = null;
        }
    }

    private void TrackEditorMouse()
    {
        _regionFeedback?.UpdateHint();
        if (_trackingEditorMouse) return;
        var track = new HandleNative.Track
        {
            Size = (uint)Marshal.SizeOf<HandleNative.Track>(), Flags = 2, Window = Handle
        };
        _trackingEditorMouse = HandleNative.TrackMouseEvent(ref track);
    }

    private void UpdateOutlineLayout()
    {
        if (_regionOutlines is null) return;
        float scale = RegionScale;
        _regionOutlines.Size = _visual.Size / scale;
        _regionOutlines.Scale = new(scale, scale, 1);
        _regionOutlines.IsVisible = IsEditingClearRegions || (_desiredVisible && _clearRegionsAllowed);
    }

    private void RebuildRegionOutlines()
    {
        ClearRegionOutlines();
        bool editing = IsEditingClearRegions;
        var areas = _regionDraft ?? _clearRegions;
        if (!editing && areas.All(area => area.IsClear)) return;
        _regionOutlines = _compositor.CreateShapeVisual();
        _regionInk = _compositor.CreateColorBrush(Color.FromArgb(255, 0, 120, 212));
        if (editing) _regionTint = _compositor.CreateColorBrush(Color.FromArgb(24, 0, 80, 145));
        _visual.Children.InsertAtTop(_regionOutlines);

        foreach (var area in areas)
        {
            if (editing)
            {
                foreach (var tile in area.Tiles)
                {
                    var fill = _compositor.CreateRectangleGeometry();
                    fill.Offset = new(tile.Left, tile.Top);
                    fill.Size = new(tile.Width, tile.Height);
                    AddShape(fill).FillBrush = _regionTint;
                }
            }
            if (!editing && area.IsClear) continue;
            foreach (var edge in area.Edges)
            {
                var line = _compositor.CreateLineGeometry();
                line.Start = new(edge.Start.X, edge.Start.Y);
                line.End = new(edge.End.X, edge.End.Y);
                AddStroke(line);
            }
        }
        if (editing)
        {
            _regionPreview = _compositor.CreateRectangleGeometry();
            AddStroke(_regionPreview).FillBrush = _regionTint;
        }
        UpdateOutlineLayout();

        CompositionSpriteShape AddShape(CompositionGeometry geometry)
        {
            var shape = _compositor.CreateSpriteShape(geometry);
            _regionOutlines.Shapes.Add(shape);
            _regionShapes.Add((geometry, shape));
            return shape;
        }
        CompositionSpriteShape AddStroke(CompositionGeometry geometry)
        {
            var shape = AddShape(geometry);
            shape.StrokeBrush = _regionInk;
            shape.StrokeThickness = 2;
            return shape;
        }
    }

    private void ClearRegionOutlines()
    {
        if (_regionOutlines is not { } outlines) return;
        _visual.Children.Remove(outlines);
        outlines.Shapes.Clear();
        foreach (var (geometry, shape) in _regionShapes)
        {
            shape.Dispose();
            geometry.Dispose();
        }
        _regionShapes.Clear();
        outlines.Dispose();
        _regionInk?.Dispose();
        _regionTint?.Dispose();
        _regionOutlines = null;
        _regionPreview = null;
        _regionInk = _regionTint = null;
    }

    private void DrawRegionPreview(PixelRect rect = default)
    {
        if (_regionPreview is not { } preview) return;
        preview.Offset = new(rect.Left, rect.Top);
        preview.Size = new(rect.Width, rect.Height);
    }

    private nint? HandleEditorCursor(uint message)
    {
        if (message != 0x0020 || !IsEditingClearRegions || _regionMenuOpen || _regionCrosshair == 0)
            return null;
        SetCursor(_regionCrosshair);
        return 1; // WM_SETCURSOR handled. Skip the class cursor assigned by DefWindowProc.
    }

    private void HandleRegionEditorMessage(uint message, nint wParam)
    {
        if (_regionDraft is not { } regions) return;
        switch (message)
        {
            case 0x0201: // Left down
                _drawStart = EditorPoint();
                _regionFeedback?.HideHint();
                SetCapture(Handle);
                break;
            case 0x0200 when _drawStart is { } start:
                DrawRegionPreview(Between(start, EditorPoint()));
                break;
            case 0x0200 when !_regionMenuOpen:
                TrackEditorMouse();
                break;
            case 0x02A3: // WM_MOUSELEAVE
                _trackingEditorMouse = false;
                _regionFeedback?.HideHint();
                break;
            case 0x0202 when _drawStart is { } start: // Left up
                var rect = Between(start, EditorPoint());
                _drawStart = null;
                ReleaseSurfaceCapture();
                if (rect.Width >= 4 && rect.Height >= 4)
                {
                    EndClearRegionEdit(save: ClearArea.Add(regions, rect));
                }
                else DrawRegionPreview();
                break;
            case 0x001F or 0x0215 when _drawStart is not null: // Capture lost
                _drawStart = null;
                DrawRegionPreview();
                break;
            case 0x0205 when _drawStart is null: // Right up
                ShowClearRegionMenu(regions);
                break;
            case 0x0100 when wParam == 27:
                EndClearRegionEdit(save: false);
                break;
            case 0x0008 when !_regionMenuOpen: // Focus moved away
                _dispatcher.TryEnqueue(() =>
                {
                    if (!_regionMenuOpen && NativeMethods.GetForegroundWindow() != Handle)
                        EndClearRegionEdit(save: false);
                });
                break;
        }
    }

    private void ShowClearRegionMenu(List<ClearArea> regions)
    {
        if (_regionMenuOpen || !NativeMethods.GetCursorPos(out var point)) return;
        PixelPoint local = EditorPoint();
        int hit = regions.FindLastIndex(area => area.Contains(local));
        nint menu = CreatePopupMenu();
        if (menu == 0) return;
        int command;
        _regionMenuOpen = true;
        _regionFeedback?.HideHint();
        try
        {
            AppendMenuW(menu, hit < 0 ? 1u : 0u, 1, LocalizationService.Get("ClearRegionRemove"));
            AppendMenuW(menu, regions.Count == 0 ? 1u : 0u, 2, LocalizationService.Get("ClearRegionRemoveAll"));
            AppendMenuW(menu, 0x800, 0, null);
            AppendMenuW(menu, 0, 3, LocalizationService.Get("ClearRegionCancel"));
            command = TrackPopupMenu(menu, 0x180, point.X, point.Y, 0, Handle, 0);
        }
        finally
        {
            _regionMenuOpen = false;
            DestroyMenu(menu);
        }
        if (_regionDraft != regions) return;
        if (command == 1 && hit >= 0)
        {
            regions.RemoveAt(hit);
            EndClearRegionEdit(save: true);
        }
        else if (command == 2)
        {
            regions.Clear();
            EndClearRegionEdit(save: true);
        }
        else if (command == 3 || NativeMethods.GetForegroundWindow() != Handle) EndClearRegionEdit(save: false);
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int CombineRgn(nint destination, nint source1, nint source2, int mode);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(nint hwnd, ref NativeMethods.Point point);
    [DllImport("user32.dll")]
    private static extern nint GetCursor();
    [DllImport("user32.dll")]
    private static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(nint menu, uint flags, nuint item, string? text);
    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint menu);
}
