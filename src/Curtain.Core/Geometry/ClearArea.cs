namespace Curtain.Core.Geometry;

public readonly record struct AreaEdge(PixelPoint Start, PixelPoint End);

// Coordinates are relative to the protected window, in logical pixels.
public sealed class ClearArea
{
    public IReadOnlyList<PixelRect> Tiles { get; }
    public IReadOnlyList<AreaEdge> Edges { get; }
    public bool IsClear { get; set; }

    private ClearArea(IReadOnlyList<PixelRect> tiles, bool isClear)
    {
        Tiles = tiles;
        Edges = FindEdges(tiles);
        IsClear = isClear;
    }

    public static bool Contains(PixelRect rect, PixelPoint point) =>
        point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;

    public bool Contains(PixelPoint point) => Tiles.Any(tile => Contains(tile, point));

    public static PixelRect ScaleTile(PixelRect tile, float scale) =>
        new((int)MathF.Round(tile.Left * scale), (int)MathF.Round(tile.Top * scale),
            (int)MathF.Round(tile.Right * scale), (int)MathF.Round(tile.Bottom * scale));

    public PixelRect? ControlBounds(PixelRect viewport, float scale)
    {
        var visibleTiles = VisibleTiles(viewport.Width, viewport.Height, scale).ToArray();
        if (visibleTiles.Length == 0) return null;

        // A merged area can have an inset top edge. Anchor to an actual top span.
        int center = (visibleTiles.Min(tile => tile.Left) + visibleTiles.Max(tile => tile.Right)) / 2;
        var top = visibleTiles.OrderBy(tile => tile.Top).ThenByDescending(tile => tile.Width)
            .ThenBy(tile => Math.Abs((tile.Left + tile.Right) / 2 - center)).First();
        int width = Math.Clamp((int)MathF.Round(PinPlacement.WindowPinWidth * scale), 1, viewport.Width);
        int height = Math.Clamp((int)MathF.Round(PinPlacement.WindowPinHeight * scale), 1, viewport.Height);
        int left = Math.Clamp((top.Left + top.Right - width) / 2, 0, viewport.Width - width);
        int y = Math.Clamp(top.Top - height, 0, viewport.Height - height);
        return new(viewport.Left + left, viewport.Top + y,
            viewport.Left + left + width, viewport.Top + y + height);
    }

    public IEnumerable<PixelRect> VisibleTiles(int width, int height, float scale, PixelRect? popup = null)
    {
        foreach (var tile in Tiles)
        {
            var pixels = ScaleTile(tile, scale);
            var clipped = new PixelRect(Math.Max(0, pixels.Left), Math.Max(0, pixels.Top),
                Math.Min(width, pixels.Right), Math.Min(height, pixels.Bottom));
            if (!clipped.IsValid) continue;
            if (popup is not { } cover)
            {
                yield return clipped;
                continue;
            }

            int left = Math.Max(clipped.Left, cover.Left), top = Math.Max(clipped.Top, cover.Top);
            int right = Math.Min(clipped.Right, cover.Right), bottom = Math.Min(clipped.Bottom, cover.Bottom);
            if (left >= right || top >= bottom)
            {
                yield return clipped;
                continue;
            }
            if (clipped.Top < top) yield return clipped with { Bottom = top };
            if (clipped.Bottom > bottom) yield return clipped with { Top = bottom };
            if (clipped.Left < left) yield return new(clipped.Left, top, left, bottom);
            if (clipped.Right > right) yield return new(right, top, clipped.Right, bottom);
        }
    }

    public static bool Add(List<ClearArea> areas, PixelRect draw)
    {
        if (!draw.IsValid) return false;
        var joined = areas.Where(area => area.Tiles.Any(tile => Connects(tile, draw))).ToArray();
        if (joined.Length == 0 && areas.Count >= 16) return false;
        var tiles = joined.SelectMany(area => area.Tiles).ToArray();
        if (tiles.Sum(tile => IntersectionArea(tile, draw)) == (long)draw.Width * draw.Height)
            return false;

        var merged = Union([.. tiles, draw]);
        if (merged.Count + areas.Sum(area => area.Tiles.Count) - tiles.Length > 256) return false;
        var area = new ClearArea(merged, joined.All(area => area.IsClear));
        foreach (var old in joined) areas.Remove(old);
        areas.Add(area);
        return true;
    }

    private static bool Connects(PixelRect a, PixelRect b)
    {
        int x = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        int y = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        return x >= 0 && y >= 0 && (x > 0 || y > 0);
    }

    private static long IntersectionArea(PixelRect a, PixelRect b) =>
        (long)Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) *
        Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));

    private static List<PixelRect> Union(PixelRect[] input)
    {
        // Merge horizontal spans in each band, then coalesce identical spans vertically.
        int[] rows = input.SelectMany(tile => new[] { tile.Top, tile.Bottom }).Distinct().Order().ToArray();
        var ordered = input.OrderBy(tile => tile.Left).ToArray();
        var result = new List<PixelRect>();
        Dictionary<(int Left, int Right), int> previous = [];
        for (int row = 0; row + 1 < rows.Length; row++)
        {
            int top = rows[row], bottom = rows[row + 1];
            Dictionary<(int Left, int Right), int> current = [];
            int left = 0, right = 0;
            bool hasSpan = false;
            foreach (var tile in ordered)
            {
                if (tile.Top > top || tile.Bottom < bottom) continue;
                if (hasSpan && tile.Left <= right)
                {
                    right = Math.Max(right, tile.Right);
                    continue;
                }
                if (hasSpan) AddSpan();
                (left, right, hasSpan) = (tile.Left, tile.Right, true);
            }
            if (hasSpan) AddSpan();
            previous = current;

            void AddSpan()
            {
                var key = (left, right);
                if (previous.TryGetValue(key, out int index))
                    result[index] = result[index] with { Bottom = bottom };
                else
                {
                    index = result.Count;
                    result.Add(new(left, top, right, bottom));
                }
                current.Add(key, index);
            }
        }
        return result;
    }

    private static List<AreaEdge> FindEdges(IReadOnlyList<PixelRect> tiles)
    {
        // Shared edges cancel, including when only part of a longer edge is shared.
        var horizontal = new Dictionary<int, SortedSet<int>>();
        var vertical = new Dictionary<int, SortedSet<int>>();
        foreach (var tile in tiles)
        {
            Add(horizontal, tile.Top, tile.Left, tile.Right);
            Add(horizontal, tile.Bottom, tile.Left, tile.Right);
            Add(vertical, tile.Left, tile.Top, tile.Bottom);
            Add(vertical, tile.Right, tile.Top, tile.Bottom);
        }

        var result = new List<AreaEdge>();
        Append(horizontal, isHorizontal: true);
        Append(vertical, isHorizontal: false);
        return result;

        void Append(Dictionary<int, SortedSet<int>> lines, bool isHorizontal)
        {
            foreach (var (coordinate, endpoints) in lines)
            {
                int? start = null;
                foreach (int end in endpoints)
                {
                    if (start is not int from) { start = end; continue; }
                    result.Add(isHorizontal ? new(new(from, coordinate), new(end, coordinate))
                        : new(new(coordinate, from), new(coordinate, end)));
                    start = null;
                }
            }
        }

        static void Add(Dictionary<int, SortedSet<int>> lines, int coordinate, int start, int end)
        {
            if (!lines.TryGetValue(coordinate, out var endpoints)) lines.Add(coordinate, endpoints = []);
            if (!endpoints.Add(start)) endpoints.Remove(start);
            if (!endpoints.Add(end)) endpoints.Remove(end);
        }
    }
}
