namespace Curtain.Core.Rendering;

/// <summary>
/// Experimental mapping from Curtain's "遮挡强度" setting to Windows Acrylic
/// material tint. This is NOT whole-window alpha and NOT blur radius.
/// </summary>
public readonly record struct AcrylicToneProfile(float TintOpacity, float LuminosityOpacity)
{
    public const int MinStrength = 45;
    public const int MaxStrength = 98;

    public static AcrylicToneProfile FromStrength(int strength)
    {
        float ratio = (Math.Clamp(strength, MinStrength, MaxStrength) - MinStrength)
            / (float)(MaxStrength - MinStrength);
        // Calibrated only as an A0.2 integration starting point; Windows visual QA decides values.
        return new AcrylicToneProfile(0.15f + 0.65f * ratio, 0.15f);
    }
}
