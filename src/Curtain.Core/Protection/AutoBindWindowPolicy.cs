namespace Curtain.Core.Protection;

public static class AutoBindWindowPolicy
{
    public static string? GetRejectionReason(string className, long style, long extendedStyle, bool hasOwner)
    {
        const long popup = 0x80000000L;
        const long caption = 0x00C00000L;
        const long thickFrame = 0x00040000L;
        const long systemMenu = 0x00080000L;
        const long appWindow = 0x00040000L;

        if (className is "Microsoft.UI.Content.PopupWindowSiteBridge" or "#32768" or
            "Auto-Suggest Dropdown" or "Xaml_WindowedPopupClass")
            return "TransientPopupClass";

        bool hasWindowFrame = (style & caption) == caption || (style & (thickFrame | systemMenu)) != 0;
        if (hasOwner && (style & popup) != 0 && !hasWindowFrame && (extendedStyle & appWindow) == 0)
            return "OwnedTransientPopup";

        return null;
    }
}
