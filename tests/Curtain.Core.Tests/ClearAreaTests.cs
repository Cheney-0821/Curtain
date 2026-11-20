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
        check(pin is { Left: -283, Top: 118, Right: -193, Bottom: 150 },
            "control is centered above the top span at mixed DPI and negative screen coordinates");
        var windowPin = PinPlacement.Calculate(new(-300, 100, 0, 350), new(-500, 0, 0, 500),
            new(-500, 0, 0, 500), 120, false).Bounds;
        check(pin?.Width == windowPin.Width && pin?.Height == windowPin.Height,
            "area pin matches window pin dimensions at the same DPI");
        var protectedPopup = new PixelRect(62, 62, 72, 68);
        var visible = merged.VisibleTiles(92, 85, 1, protectedPopup).ToArray();
        check(visible.All(tile => tile.Right <= 92 && tile.Bottom <= 85) &&
            visible.All(tile => !ClearArea.Contains(tile, new(66, 65))) &&
            visible.Any(tile => ClearArea.Contains(tile, new(61, 65))) &&
            visible.Sum(tile => tile.Width * tile.Height) == 2490,
            "rendering and input use the same clipped union without revealing protected popups");

        ClearArea.Add(areas, new(120, 90, 150, 115));
        check(areas.Count == 2 && areas[1].IsClear, "separate area starts clear independently");
        ClearArea.Add(areas, new(100, 90, 120, 110));
        check(areas.Count == 2, "corner contact alone leaves areas independent while a shared edge merges");
        ClearArea.Add(areas, new(96, 85, 103, 100));
        check(areas.Count == 1 && !areas[0].IsClear, "bridge merges both areas with privacy-preserving state");
        var hiddenPin = areas[0].ControlBounds(new(0, 0, 10, 10), 1);
        check(hiddenPin is null, "a fully clipped area has no hover control");
        var clippedPin = areas[0].ControlBounds(new(100, 100, 130, 150), 1);
        check(clippedPin is { Left: 100, Top: 114, Right: 130, Bottom: 140 },
            "control fits the clipped window and stays inside its frame");
    }
}
