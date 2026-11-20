using System.ComponentModel;
using System.Runtime.InteropServices;
using Curtain.App.Interop;
using Curtain.App.Services;
using Curtain.Core.Geometry;

namespace Curtain.App.Windows;

internal sealed partial class CurtainWindow
{
    private sealed class LiveAreaMask : IDisposable
    {
        internal nint Base, Moving, Protected;
        internal PixelPoint Location;
        internal bool Ready;

        public void Dispose()
        {
            if (Base != 0) HandleNative.DeleteObject(Base);
            if (Moving != 0) HandleNative.DeleteObject(Moving);
            if (Protected != 0) HandleNative.DeleteObject(Protected);
            Base = Moving = Protected = 0;
            Location = default;
        }
    }

    private async Task StartLiveAreaMaskAsync(AreaDrag drag)
    {
        var mask = new LiveAreaMask();
        drag.LiveMask = mask;
        try
        {
            _regionLayout = null;
            UpdateClearRegionSurface();
            // Keep the old native hole in place until its composition clip is gone.
            await _compositor.RequestCommitAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(250));
            if (!ReferenceEquals(_areaDrag, drag) || !ReferenceEquals(drag.LiveMask, mask)) return;
            if (!CanContinueAreaDrag(drag) || mask.Base == 0)
            {
                EndAreaDrag(save: false);
                return;
            }
            mask.Ready = true;
            ApplyLiveAreaPosition(drag);
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_areaDrag, drag)) FallBackFromLiveMask(drag, ex);
        }
    }

    private void ConfigureLiveAreaMask(LiveAreaMask mask, ClearArea area, PixelRect preview,
        PixelRect bounds, float scale, PixelRect[] staticHoles, PixelRect? popup)
    {
        mask.Dispose();
        mask.Base = MakeRegion(bounds);
        foreach (var tile in staticHoles) CombineRegionRectangle(mask.Base, tile, RegionDifference);

        mask.Moving = MakeRegion(default);
        var relative = new PixelRect(0, 0, preview.Width, preview.Height);
        foreach (var tile in area.Tiles)
        {
            var start = area.MapPoint(new(tile.Left, tile.Top), relative);
            var end = area.MapPoint(new(tile.Right, tile.Bottom), relative);
            var pixels = ClearArea.ScaleTile(new(start.X, start.Y, end.X, end.Y), scale);
            if (pixels.IsValid) CombineRegionRectangle(mask.Moving, pixels, RegionUnion);
        }

        // An independently blurred area or a protected popup still wins in an overlap.
        var protectedTiles = _clearRegions.Where(item => !item.IsClear)
            .SelectMany(item => item.VisibleTiles(bounds.Width, bounds.Height, scale));
        foreach (var tile in protectedTiles) Protect(tile);
        if (popup is { } cover)
        {
            var visible = new PixelRect(Math.Max(bounds.Left, cover.Left), Math.Max(bounds.Top, cover.Top),
                Math.Min(bounds.Right, cover.Right), Math.Min(bounds.Bottom, cover.Bottom));
            if (visible.IsValid) Protect(visible);
        }

        void Protect(PixelRect tile)
        {
            if (mask.Protected == 0) mask.Protected = MakeRegion(default);
            CombineRegionRectangle(mask.Protected, tile, RegionUnion);
        }
    }

    private void ApplyLiveAreaPosition(AreaDrag drag)
    {
        if (drag is not { LiveMask: { Ready: true } mask, Preview: { } preview }) return;
        try
        {
            var pixels = ClearArea.ScaleTile(preview, drag.Scale);
            var next = new PixelPoint(pixels.Left, pixels.Top);
            if (OffsetRgn(mask.Moving, next.X - mask.Location.X, next.Y - mask.Location.Y) == 0)
                throw new Win32Exception();
            mask.Location = next;

            nint output = MakeRegion(default);
            try
            {
                if (CombineRgn(output, mask.Base, mask.Moving, RegionDifference) == 0 ||
                    (mask.Protected != 0 && CombineRgn(output, output, mask.Protected, RegionUnion) == 0))
                    throw new Win32Exception();
                AssignWindowRegion(ref output);
            }
            finally { if (output != 0) HandleNative.DeleteObject(output); }
        }
        catch (Exception ex)
        {
            FallBackFromLiveMask(drag, ex);
        }
    }

    private void FallBackFromLiveMask(AreaDrag drag, Exception error)
    {
        drag.LiveMask?.Dispose();
        drag.LiveMask = null;
        _regionLayout = null;
        CurtainDiagnostics.WriteException("overlay.clear-area.live-preview-failed", error,
            operationId: _operationId, stage: "window-protection", sessionId: _sessionId, ruleId: _ruleId);
        UpdateClearRegionSurface();
    }

    private static nint MakeRegion(PixelRect rect)
    {
        nint region = CreateRectRgn(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return region != 0 ? region : throw new Win32Exception();
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int OffsetRgn(nint region, int x, int y);
}
