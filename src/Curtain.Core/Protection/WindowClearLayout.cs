using Curtain.Core.Geometry;

namespace Curtain.Core.Protection;

public sealed record SavedClearArea(IReadOnlyList<PixelRect> Tiles, bool IsClear);

public sealed record WindowClearLayout(string WindowClass, int Slot, IReadOnlyList<SavedClearArea> Areas,
    bool RequiresConfirmation = false)
{
    public const int MaxLayouts = 32;

    internal bool IsValid =>
        !string.IsNullOrWhiteSpace(WindowClass) && WindowClass.Length <= 256 &&
        Slot is >= 0 and < MaxLayouts && Areas is { Count: > 0 and <= 16 } &&
        Areas.All(area => area is { Tiles.Count: > 0 and <= 256 } && area.Tiles.All(tile =>
            tile.IsValid && tile.Left >= 0 && tile.Top >= 0 && tile.Right <= 65536 && tile.Bottom <= 65536)) &&
        Areas.Sum(area => area.Tiles.Count) <= 256;

    public bool Matches(string windowClass, int slot) =>
        Slot == slot && string.Equals(WindowClass, windowClass, StringComparison.OrdinalIgnoreCase);

    public List<ClearArea> Restore(bool ambiguous)
    {
        if (!IsValid) return [];
        return Areas.Select(area => ClearArea.Restore(area.Tiles, area.IsClear && !ambiguous && !RequiresConfirmation)).ToList();
    }
}
