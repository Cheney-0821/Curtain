namespace Curtain.Core.Geometry;

public readonly record struct DockedPin(PixelRect Bounds, bool IsInside, bool IsFullScreen);

public static class PinPlacement
{
    internal const int WindowPinWidth = 72;
    internal const int WindowPinHeight = 26;
    public const int WindowPinGap = 6;
    // Reach past the invisible resize border at a snapped window's top edge.
    private const int WindowPinRevealHeight = 10;

    public static DockedPin Calculate(PixelRect target, PixelRect workArea,
        PixelRect monitor, int dpi, bool maximized, bool outsideOccupied = false)
    {
        if (!target.IsValid || !workArea.IsValid || !monitor.IsValid)
            throw new ArgumentException("Invalid placement bounds");
        bool fullScreen = target.Left <= monitor.Left && target.Top <= monitor.Top &&
            target.Right >= monitor.Right && target.Bottom >= monitor.Bottom;
        var area = fullScreen ? monitor : workArea;
        int width = Math.Min(Scale(WindowPinWidth, dpi), Math.Min(target.Width, area.Width));
        int height = Math.Min(Scale(WindowPinHeight, dpi), Math.Min(target.Height, area.Height));
        int gap = Scale(WindowPinGap, dpi);
        int left = Math.Clamp(target.Left + (target.Width - width) / 2, area.Left, area.Right - width);
        int outsideTop = target.Top - height - gap;
        bool inside = maximized || fullScreen || outsideOccupied || outsideTop < area.Top || outsideTop + height > area.Bottom;
        int top = inside ? Math.Clamp(target.Top, area.Top, area.Bottom - 1) : outsideTop;
        if (inside) height = Math.Max(1, Math.Min(height, Math.Min(target.Bottom, area.Bottom) - top));
        return new DockedPin(new PixelRect(left, top, left + width, top + height), inside, fullScreen);
    }

    public static PixelRect RevealBounds(DockedPin pin, PixelRect target, PixelRect usableArea, int dpi)
    {
        if (!pin.IsInside) return default;
        int top = Math.Max(target.Top, usableArea.Top);
        return new(Math.Max(target.Left, usableArea.Left), top,
            Math.Min(target.Right, usableArea.Right),
            Math.Min(Math.Min(target.Bottom, usableArea.Bottom), top + Scale(WindowPinRevealHeight, dpi)));
    }

    private static int Scale(int dip, int dpi) => Math.Max(1, (int)Math.Round(dip * (dpi > 0 ? dpi : 96) / 96.0));

    // Pin stays inside the top edge; this also works for maximized windows.
    public static PixelRect Calculate(PixelRect target, int pinWidth = 42, int pinHeight = 30)
    {
        if (!target.IsValid) throw new ArgumentException("Invalid target bounds", nameof(target));
        if (pinWidth <= 0 || pinHeight <= 0) throw new ArgumentOutOfRangeException(nameof(pinWidth));
        int width = Math.Min(pinWidth, target.Width);
        int height = Math.Min(pinHeight, target.Height);
        int x = target.Left + Math.Max(0, (target.Width - width) / 2);
        int y = target.Top + Math.Min(5, Math.Max(0, target.Height - height));
        return new PixelRect(x, y, x + width, y + height);
    }
}
