namespace Curtain.Core.Settings;

public sealed record WindowProtectionSettings(double BlurAmount = 20, bool AllowClickThrough = false,
    double GentleAmount = 1.5);

public static class WindowBlurScale
{
    public static double Normalize(double amount)
    {
        if (!double.IsFinite(amount)) return 20;
        if (amount <= 3) return Math.Round(Math.Max(0, amount), 2);
        return amount <= 10 ? 10 : 20;
    }

    public static WindowProtectionSettings Normalize(WindowProtectionSettings settings)
    {
        double amount = Normalize(settings.BlurAmount);
        double gentle = amount <= 3 ? amount : double.IsFinite(settings.GentleAmount)
            ? Math.Round(Math.Clamp(settings.GentleAmount, 0, 3), 2) : 1.5;
        return settings with { BlurAmount = amount, GentleAmount = gentle };
    }

    public static WindowProtectionSettings Resolve(WindowProtectionSettings defaults, WindowProtectionSettings? custom) => custom ?? defaults;
}
