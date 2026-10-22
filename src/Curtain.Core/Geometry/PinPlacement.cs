namespace Curtain.Core.Geometry;

public readonly record struct DockedPin(PixelRect Bounds, bool IsInside, bool IsFullScreen);

public static class PinPlacement
{
    public static DockedPin Calculate(PixelRect target, PixelRect workArea,
        PixelRect monitor, int dpi, bool maximized)
    {
        if (!target.IsValid || !workArea.IsValid || !monitor.IsValid)
            throw new ArgumentException("Invalid placement bounds");
        int Scale(int dip) => Math.Max(1, (int)Math.Round(dip * (dpi > 0 ? dpi : 96) / 96.0));
        bool fullScreen = target.Left <= monitor.Left && target.Top <= monitor.Top &&
            target.Right >= monitor.Right && target.Bottom >= monitor.Bottom;
        var area = fullScreen ? monitor : workArea;
        int width = Math.Min(Scale(72), Math.Min(target.Width, area.Width));
        int height = Math.Min(Scale(26), Math.Min(target.Height, area.Height));
        int gap = Scale(4);
        int left = Math.Clamp(target.Left + (target.Width - width) / 2, area.Left, area.Right - width);
        int outsideTop = target.Top - height - gap;
        bool inside = maximized || fullScreen || outsideTop < area.Top || outsideTop + height > area.Bottom;
        int top = inside ? Math.Clamp(target.Top + gap, area.Top, area.Bottom - height) : outsideTop;
        if (inside && top + height > target.Bottom) top = Math.Max(area.Top, target.Bottom - height);
        return new DockedPin(new PixelRect(left, top, left + width, top + height), inside, fullScreen);
    }

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
