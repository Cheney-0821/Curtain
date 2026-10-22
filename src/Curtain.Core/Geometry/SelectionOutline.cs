namespace Curtain.Core.Geometry;

public static class SelectionOutline
{
    public static int StrokePixels(int dpi) => Math.Max(1, (int)Math.Ceiling(2 * (dpi > 0 ? dpi : 96) / 96.0));
    public static bool IsBorderPixel(int x, int y, int width, int height, int stroke) =>
        x >= 0 && y >= 0 && x < width && y < height &&
        (x < stroke || y < stroke || x >= width - stroke || y >= height - stroke);
}
