namespace Curtain.Core.Geometry;

public readonly record struct PixelPoint(int X, int Y);

public sealed class DragGesture
{
    private readonly PixelPoint _press;
    private readonly PixelRect _original;
    private readonly int _thresholdX, _thresholdY;
    public bool IsDragging { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DragGesture(PixelPoint press, PixelRect original, int thresholdX, int thresholdY)
    {
        if (!original.IsValid) throw new ArgumentException("Invalid drag bounds");
        _press = press; _original = original;
        _thresholdX = Math.Max(1, thresholdX); _thresholdY = Math.Max(1, thresholdY);
    }
    public PixelRect? Move(PixelPoint current)
    {
        if (!IsActive) return null;
        int dx = current.X - _press.X, dy = current.Y - _press.Y;
        if (!IsDragging && Math.Abs(dx) <= _thresholdX && Math.Abs(dy) <= _thresholdY) return null;
        IsDragging = true;
        return new PixelRect(_original.Left + dx, _original.Top + dy,
            _original.Right + dx, _original.Bottom + dy);
    }
    public void End() { IsActive = false; IsDragging = false; }
    public static PixelRect TranslateVisibleBounds(PixelRect currentRaw, PixelRect currentVisible, PixelRect nextRaw)
    {
        int dx = nextRaw.Left - currentRaw.Left, dy = nextRaw.Top - currentRaw.Top;
        return new PixelRect(currentVisible.Left + dx, currentVisible.Top + dy,
            currentVisible.Right + dx, currentVisible.Bottom + dy);
    }
    public static bool CanMove(PixelRect visible, PixelRect work, PixelRect monitor, bool maximized, int dpi)
    {
        if (maximized || (visible.Left <= monitor.Left && visible.Top <= monitor.Top &&
            visible.Right >= monitor.Right && visible.Bottom >= monitor.Bottom)) return false;
        int tolerance = Math.Max(2, (int)Math.Round(8 * dpi / 96.0));
        bool halfWidth = Math.Abs(visible.Width - work.Width / 2) <= tolerance;
        bool fullOrHalfHeight = Math.Abs(visible.Height - work.Height) <= tolerance ||
            Math.Abs(visible.Height - work.Height / 2) <= tolerance;
        bool onSide = Math.Abs(visible.Left - work.Left) <= tolerance || Math.Abs(visible.Right - work.Right) <= tolerance;
        bool onEnd = Math.Abs(visible.Top - work.Top) <= tolerance || Math.Abs(visible.Bottom - work.Bottom) <= tolerance;
        return !(halfWidth && fullOrHalfHeight && onSide && onEnd);
    }
    public static PixelRect KeepReachable(PixelRect bounds, PixelRect work, int horizontalVisible, int verticalVisible)
    {
        int x = Math.Clamp(bounds.Left, work.Left - bounds.Width + Math.Min(bounds.Width, horizontalVisible),
            work.Right - Math.Min(bounds.Width, horizontalVisible));
        int y = Math.Clamp(bounds.Top, work.Top, work.Bottom - Math.Min(bounds.Height, verticalVisible));
        return new PixelRect(x, y, x + bounds.Width, y + bounds.Height);
    }
}
