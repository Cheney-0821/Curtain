using Curtain.Core.Geometry;

namespace Curtain.Core.Protection;

/// <summary>Motion in a display's upper quarter reveals its control; leaving or resting hides it.</summary>
public sealed class FullscreenControlState
{
    public const long HideDelayMs = 1000;
    public long VisibleMonitor { get; private set; }
    private long _lastMotion;
    private long? _leftAt;
    private PixelPoint _previousPoint;
    private bool _waitingForMovement;

    public void Reset(PixelPoint point)
    {
        VisibleMonitor = 0;
        _previousPoint = point;
        _waitingForMovement = true;
        _leftAt = null;
    }

    public long Update(long now, PixelPoint point, long monitor, bool inRevealArea, bool overControl, bool pressed)
    {
        bool moved = point != _previousPoint;
        _previousPoint = point;
        if (_waitingForMovement && !moved) return 0;
        _waitingForMovement = false;
        if (moved) _lastMotion = now;
        if (monitor != 0 && pressed)
        {
            VisibleMonitor = monitor;
            _lastMotion = now;
            _leftAt = null;
            return VisibleMonitor;
        }
        if (monitor != 0 && (inRevealArea || overControl))
        {
            _leftAt = null;
            if (moved) VisibleMonitor = monitor;
        }
        else if (VisibleMonitor != 0) _leftAt ??= now;
        if (now - _lastMotion >= HideDelayMs || _leftAt is { } left && now - left >= HideDelayMs)
            VisibleMonitor = 0;
        return VisibleMonitor;
    }

    public static bool Contains(PixelRect display, PixelPoint point) =>
        point.X >= display.Left && point.X < display.Right && point.Y >= display.Top && point.Y < display.Bottom;

    public static bool IsRevealArea(PixelRect display, PixelPoint point) =>
        Contains(display, point) && point.Y - display.Top < display.Height / 4d;
}
