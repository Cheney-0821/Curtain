namespace Curtain.Core.Geometry;

/// <summary>Physical screen pixel rectangle. Right/Bottom are exclusive.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
    public bool IsValid => Right > Left && Bottom > Top;
}
