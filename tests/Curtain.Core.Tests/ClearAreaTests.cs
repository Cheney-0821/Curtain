using Curtain.Core.Geometry;

internal static class ClearAreaTests
{
    internal static void Run(Action<bool, string> check)
    {
        List<ClearArea> areas = [];
        check(ClearArea.Add(areas, new(20, 40, 80, 70)) && areas[0].IsClear,
            "new clear area starts revealed");
        var original = areas[0];
        original.IsClear = false;
        for (int i = 0; i < 20; i++)
            ClearArea.Add(areas, new(30, 50, 45, 60));
        check(areas.Count == 1 && areas[0] == original && original.Tiles.Count == 1 && !original.IsClear,
            "contained draws keep the existing closed area and do not accumulate tiles");
        check(ClearArea.Add(areas, new(50, 60, 100, 90)) && areas.Count == 1 && !areas[0].IsClear,
            "overlap merges into one area and preserves temporary blur");
        var merged = areas[0];
        check(merged.Contains(new(65, 64)) && !merged.Contains(new(90, 49)) && !merged.Contains(new(45, 85)),
            "union covers overlap without filling the concave corners");
        check(merged.Tiles.Sum(tile => tile.Width * tile.Height) == 3000 &&
            merged.Edges.Count == 8 && merged.Edges.All(edge =>
                !(edge.Start.Y == 60 && edge.End.Y == 60 && edge.Start.X < 80)),
            "union has only exterior outline segments and no shared edges");
        check(!ClearArea.Add(areas, new(52, 54, 76, 86)) && areas[0] == merged,
            "a draw covered by multiple tiles is also a no-op");
        var pin = merged.ControlBounds(new(-300, 100, 0, 350), 1.25f);
        check(pin is { Left: -283, Top: 110, Right: -193, Bottom: 142 },
            "control is centered above the top span with the scaled window handle gap");
        var windowPin = PinPlacement.Calculate(new(-300, 100, 0, 350), new(-500, 0, 0, 500),
            new(-500, 0, 0, 500), 120, false).Bounds;
        check(pin?.Width == windowPin.Width && pin?.Height == windowPin.Height,
            "area pin matches window pin dimensions at the same DPI");
        var popup = new PixelRect(62, 45, 91, 78);
        var visible = merged.VisibleTiles(92, 85, 1).ToArray();
        var overlap = new PixelPoint(66, 65);
        var covered = new PixelPoint(90, 49);
        check(visible.All(tile => tile.Right <= 92 && tile.Bottom <= 85) &&
            ClearArea.Contains(popup, overlap) && visible.Any(tile => ClearArea.Contains(tile, overlap)) &&
            ClearArea.Contains(popup, covered) && visible.All(tile => !ClearArea.Contains(tile, covered)) &&
            visible.Sum(tile => tile.Width * tile.Height) == 2550,
            "a popup crossing the clear boundary inherits both the clear and blurred parts");

        ClearArea.Add(areas, new(120, 90, 150, 115));
        check(areas.Count == 2 && areas[1].IsClear, "separate area starts clear independently");
        ClearArea.Add(areas, new(100, 90, 120, 110));
        check(areas.Count == 2, "corner contact alone leaves areas independent while a shared edge merges");
        ClearArea.Add(areas, new(96, 85, 103, 100));
        check(areas.Count == 1 && !areas[0].IsClear, "bridge merges both areas with privacy-preserving state");
        var hiddenPin = areas[0].ControlBounds(new(0, 0, 10, 10), 1);
        check(hiddenPin is null, "a fully clipped area has no hover control");
        var clippedPin = areas[0].ControlBounds(new(100, 100, 130, 150), 1);
        check(clippedPin is { Left: 100, Top: 108, Right: 130, Bottom: 134 },
            "control fits the clipped window and stays inside its frame");
        CheckDrag(check);
    }

    private static void CheckDrag(Action<bool, string> check)
    {
        List<ClearArea> areas = [];
        ClearArea.Add(areas, new(10, 10, 50, 50));
        ClearArea.Add(areas, new(30, 40, 80, 60));
        var area = areas[0];
        area.IsClear = false;
        check(area.ResizeEdgeAt(new(10, 10), 4) == (AreaResizeEdge.Left | AreaResizeEdge.Top) &&
            area.ResizeEdgeAt(new(30, 28), 4) == AreaResizeEdge.None &&
            area.ResizeEdgeAt(new(63, 40), 4) == AreaResizeEdge.Top,
            "corners and actual concave edges are resizable without hitting the interior");
        var viewport = new PixelRect(0, 0, 100, 100);
        var resized = area.AdjustBounds(viewport, new(20, 30), AreaResizeEdge.Right | AreaResizeEdge.Bottom);
        check(resized == new PixelRect(10, 10, 100, 90), "diagonal resize keeps opposite corner anchored");
        check(area.AdjustBounds(viewport, new(100, 0), AreaResizeEdge.Left).Width == 4,
            "inward resize respects the minimum region size");
        var transformed = ClearArea.MoveOrResize(areas, area, resized);
        check(!transformed.IsClear && transformed.Contains(new(79, 78)) && !transformed.Contains(new(85, 25)),
            "resizing preserves blur state and the concave shape");
        var translated = transformed.AdjustBounds(viewport, new(-90, -90), AreaResizeEdge.None);
        check(translated == new PixelRect(0, 0, 90, 80), "moving stays within the window");
        ClearArea.Add(areas, new(2, 2, 7, 7));
        var moved = ClearArea.MoveOrResize(areas, transformed, translated);
        check(areas.Count == 1 && !moved.IsClear, "moving into a clear area merges without revealing blurred content");

        List<ClearArea> clear = [];
        ClearArea.Add(clear, new(10, 10, 30, 20));
        var revealed = ClearArea.MoveOrResize(clear, clear[0], new(40, 30, 60, 40));
        check(revealed.IsClear && revealed.Contains(new(41, 31)) && !revealed.Contains(new(10, 10)),
            "moving a clear area preserves its state and removes its old footprint");
    }
}
