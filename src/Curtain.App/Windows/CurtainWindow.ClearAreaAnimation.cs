using System.Diagnostics;
using System.Numerics;
using Curtain.Core.Geometry;
using Curtain.Core.Rendering;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.UI.Composition;

namespace Curtain.App.Windows;

internal sealed partial class CurtainWindow
{
    private SpriteVisual? _mainBlurVisual, _areaBlurVisual;
    private CompositionEffectBrush? _areaBlurBrush;
    private AreaTransition? _areaTransition;

    private SpriteVisual MainBlurSurface => _mainBlurVisual ?? _visual;

    private readonly record struct AreaTransition(ClearArea Area, double From, double To, long Started)
    {
        internal double Duration => OverlayFadeCurve.DurationFor(From, To);
        internal double Sample(long now) =>
            OverlayFadeCurve.Sample(From, To, Stopwatch.GetElapsedTime(Started, now).TotalMilliseconds, Duration);
    }

    private void StartAreaTransition(ClearArea area)
    {
        long now = Stopwatch.GetTimestamp();
        double from = area.IsClear ? 0 : 255;
        if (_areaTransition is { } current)
        {
            if (current.Area == area) from = current.Sample(now);
            else FinishAreaTransition();
        }
        area.IsClear = !area.IsClear;
        _areaTransition = new(area, from, area.IsClear ? 0 : 255, now);
        UpdateAreaTimerInterval();
        _regionLayout = null;
        UpdateClearRegionSurface();
    }

    private void TickAreaTransition()
    {
        if (_areaTransition is not { } transition) return;
        long now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(transition.Started, now).TotalMilliseconds >= transition.Duration)
            FinishAreaTransition();
        else
            ApplyAreaBlur(transition.Sample(now));
    }

    private void FinishAreaTransition()
    {
        if (_areaTransition is null) return;
        ResetAreaTransition();
        UpdateClearRegionSurface();
    }

    private void ResetAreaTransition()
    {
        _areaTransition = null;
        _regionLayout = null;
        UpdateAreaTimerInterval();
    }

    private void ApplyAreaBlur(double amount) =>
        _areaBlurBrush?.Properties.InsertScalar("Blur.BlurAmount",
            (float)(_settings.BlurAmount * _ramp / 255 * amount / 255));

    private void UpdateAreaBlurClips(PixelRect bounds, PixelRect[] holes, PixelRect[] animated)
    {
        if (_mainBlurVisual is null)
        {
            if (holes.Length == 0 && animated.Length == 0) return;
            _mainBlurVisual = _compositor.CreateSpriteVisual();
            _mainBlurVisual.RelativeSizeAdjustment = Vector2.One;
            _mainBlurVisual.Brush = _visual.Brush;
            _visual.Brush = null;
            _visual.Children.InsertAtBottom(_mainBlurVisual);
        }

        // Keep rendering and native hit-testing aligned, even as the HWND region changes.
        SetBlurClip(_mainBlurVisual, holes.Concat(animated), bounds);
        if (animated.Length == 0)
        {
            if (_areaBlurVisual is { } previous)
            {
                previous.IsVisible = false;
                ClearBlurClip(previous);
            }
            return;
        }

        if (_areaBlurVisual is null)
        {
            _areaBlurBrush = _factory.CreateBrush();
            _areaBlurBrush.SetSourceParameter("Source", _backdrop);
            _areaBlurVisual = _compositor.CreateSpriteVisual();
            _areaBlurVisual.RelativeSizeAdjustment = Vector2.One;
            _areaBlurVisual.Brush = _areaBlurBrush;
            _visual.Children.InsertAbove(_areaBlurVisual, _mainBlurVisual);
        }
        SetBlurClip(_areaBlurVisual, animated);
        if (_areaTransition is { } transition) ApplyAreaBlur(transition.Sample(Stopwatch.GetTimestamp()));
        _areaBlurVisual.IsVisible = true;
    }

    private void SetBlurClip(SpriteVisual visual, IEnumerable<PixelRect> tiles, PixelRect? outer = null)
    {
        using var rectangles = tiles.GetEnumerator();
        if (!rectangles.MoveNext())
        {
            ClearBlurClip(visual);
            return;
        }

        using var path = new CanvasPathBuilder(null);
        path.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Alternate);
        if (outer is { } bounds) AddRectangle(bounds);
        do { AddRectangle(rectangles.Current); } while (rectangles.MoveNext());
        using var geometry = CanvasGeometry.CreatePath(path);
        var compositionPath = new CompositionPath(geometry);
        if (visual.Clip is CompositionGeometricClip { Geometry: CompositionPathGeometry existing })
            existing.Path = compositionPath;
        else
            visual.Clip = _compositor.CreateGeometricClip(_compositor.CreatePathGeometry(compositionPath));

        void AddRectangle(PixelRect rect)
        {
            path.BeginFigure(rect.Left, rect.Top);
            path.AddLine(rect.Right, rect.Top);
            path.AddLine(rect.Right, rect.Bottom);
            path.AddLine(rect.Left, rect.Bottom);
            path.EndFigure(CanvasFigureLoop.Closed);
        }
    }

    private static void ClearBlurClip(SpriteVisual visual)
    {
        if (visual.Clip is not CompositionGeometricClip clip) return;
        var geometry = clip.Geometry;
        visual.Clip = null;
        clip.Dispose();
        geometry.Dispose();
    }

    private void CloseAreaBlurVisuals()
    {
        if (_areaBlurVisual is { } animated)
        {
            ClearBlurClip(animated);
            animated.Brush = null;
            _visual.Children.Remove(animated);
            animated.Dispose();
            _areaBlurBrush?.Dispose();
        }
        if (_mainBlurVisual is { } primary)
        {
            ClearBlurClip(primary);
            primary.Brush = null;
            _visual.Children.Remove(primary);
            primary.Dispose();
        }
    }
}
