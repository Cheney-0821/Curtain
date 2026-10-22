namespace Curtain.Core;

public sealed record PrivacyProfile(int TintOpacityPercent, bool BlockPointer)
{
    public static PrivacyProfile Locked => new(88, true);
    public static PrivacyProfile Working => new(42, false);

    public PrivacyProfile Validate()
    {
        if (TintOpacityPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(TintOpacityPercent));
        return this;
    }
}
