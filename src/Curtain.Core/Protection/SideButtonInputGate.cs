namespace Curtain.Core.Protection;

public enum PrivacyMouseButton { Off = 0, Back = 1, Forward = 2 }
public readonly record struct SideButtonResult(bool Suppress, bool Toggle);

/// <summary>Own a complete selected down/up pair; fire only once, on release.</summary>
public sealed class SideButtonInputGate
{
    private PrivacyMouseButton _held;
    public bool HasHeldButton => _held != PrivacyMouseButton.Off;

    public SideButtonResult Handle(uint message, uint mouseData, PrivacyMouseButton selected, bool accepting = true)
    {
        if (message is not (0x20B or 0x20C)) return default;
        var button = (PrivacyMouseButton)(mouseData >> 16);
        if (button is not (PrivacyMouseButton.Back or PrivacyMouseButton.Forward)) return default;
        if (message == 0x20B)
        {
            if (_held == button) return new(true, false);
            if (!accepting || selected != button || HasHeldButton) return default;
            _held = button;
            return new(true, false);
        }
        if (_held != button) return default;
        _held = PrivacyMouseButton.Off;
        return new(true, accepting && selected == button);
    }
}
