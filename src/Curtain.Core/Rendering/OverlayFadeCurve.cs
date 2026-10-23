namespace Curtain.Core.Rendering;

/// <summary>Pure 180 ms smoothstep curve shared by HWND fades and Core regression tests.</summary>
public static class OverlayFadeCurve
{
    public const double FullDurationMs = 180.0;
    private const double MaxAlpha = 255.0;

    // A one-percent-opacity overlay is effectively invisible but may still intercept input.
    public static bool NearlyTransparent(double alpha) => alpha <= 3.0;

    // A reversal travels only the remaining opacity distance, so a short fade doesn't
    // leave an almost-invisible input-blocking HWND on screen for a full 180 ms.
    public static double DurationFor(double from, double to) =>
        FullDurationMs * Math.Abs(to - from) / MaxAlpha;

    public static double Sample(double from, double to, double elapsedMs, double durationMs)
    {
        if (durationMs <= 0) return to;
        double t = Math.Clamp(elapsedMs / durationMs, 0.0, 1.0);
        double eased = t * t * (3.0 - 2.0 * t);
        return from + (to - from) * eased;
    }
}
