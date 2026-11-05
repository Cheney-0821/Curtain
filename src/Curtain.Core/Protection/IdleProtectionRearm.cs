namespace Curtain.Core.Protection;

/// <summary>Suppressed input may not advance Windows' last-input tick. Explicit restore starts a new idle period.</summary>
public sealed class IdleProtectionRearm
{
    private long? _resumedAt;
    public void Resume(long now) => _resumedAt = now;
    public bool AllowsActivation(long now, int minutes) =>
        _resumedAt is not { } resumed || now - resumed >= minutes * 60_000L;
}
