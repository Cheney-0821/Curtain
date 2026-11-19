using System.Runtime.InteropServices;
using Curtain.App.Interop;
using Curtain.Core.Geometry;

NativeZOrderCheck.Run();

internal static class NativeZOrderCheck
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string title,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);

    private static void Check(bool passed, string name)
    {
        if (!passed) throw new InvalidOperationException(name);
        Console.WriteLine("PASS: " + name);
    }

    internal static void Run()
    {
        nint foreground = NativeMethods.GetForegroundWindow();
        var display = PrivacyNative.Displays()[0];
        var area = new ScreenRect(display.Monitor, display.Left + 80, display.Top + 80, 120, 80);
        nint overlay = CreateWindowExW(0x08000080, "STATIC", "Curtain Z-order check", 0x80000000,
            area.Left, area.Top, area.Width, area.Height, 0, 0, 0, 0);
        nint popup = CreateWindowExW(0x08000080, "STATIC", "Controlled popup", 0x80000000,
            area.Left, area.Top, area.Width, area.Height, 0, 0, 0, 0);
        try
        {
            Check(overlay != 0 && popup != 0, "controlled HWND creation");
            PrivacyNative.PositionFullScreen(overlay, area, showWindow: false);
            Check(!NativeMethods.IsWindowVisible(overlay), "hidden positioning does not show overlay");
            NativeMethods.ShowWindow(overlay, 4);
            Check(PrivacyNative.RaiseFullScreen(overlay), "visible overlay can be raised without activation");
            for (int i = 0; i < 10; i++)
            {
                PrivacyNative.PositionFullScreen(popup, area);
                nint detected = PrivacyNative.FindFullScreenOccluder(overlay, area);
                Check(detected == popup, $"new topmost popup is detected ({i + 1}/10)");
                Check(PrivacyNative.RaiseFullScreen(overlay) &&
                    PrivacyNative.FindFullScreenOccluder(overlay, area) == 0,
                    $"non-activating Z-order repair ({i + 1}/10)");
            }
            Check(NativeMethods.GetForegroundWindow() == foreground, "foreground unchanged");
            NativeMethods.GetWindowRect(overlay, out var rect);
            Check(rect.Left == area.Left && rect.Top == area.Top &&
                rect.Right - rect.Left == area.Width && rect.Bottom - rect.Top == area.Height,
                "repair preserves geometry");
            PrivacyNative.PositionFullScreen(popup, area with { Left = area.Left + 200 });
            Check(PrivacyNative.FindFullScreenOccluder(overlay, area) == 0, "non-overlapping popup ignored");
            PrivacyNative.PositionFullScreen(popup, area with { Width = 1, Height = 1 });
            Check(PrivacyNative.FindFullScreenOccluder(overlay, area) == 0, "1x1 shell-style helper ignored");
            NativeMethods.ShowWindow(overlay, NativeMethods.SW_HIDE);
            Check(PrivacyNative.RaiseFullScreen(overlay) && !NativeMethods.IsWindowVisible(overlay),
                "raising never reveals a hidden overlay");
        }
        finally
        {
            if (popup != 0) DestroyWindow(popup);
            if (overlay != 0) DestroyWindow(overlay);
        }
        Check(!NativeMethods.IsWindow(overlay) && !NativeMethods.IsWindow(popup), "controlled HWNDs released");
        CheckContainedToolPopup(new(area.Left, area.Top, area.Left + 300, area.Top + 220));
        Console.WriteLine("Native HWND checks only; Acrylic and WinEvent guard GUI acceptance remain separate.");
    }

    private static void CheckContainedToolPopup(PixelRect bounds)
    {
        const uint noActivate = 0x08000000;
        const uint toolWindow = 0x00000080;
        const uint popupStyle = 0x80000000;
        PixelRect inside = new(bounds.Left + 40, bounds.Top + 40, bounds.Left + 150, bounds.Top + 120);
        nint target = CreateWindowExW(noActivate, "STATIC", "", popupStyle, bounds.Left, bounds.Top,
            bounds.Width, bounds.Height, 0, 0, 0, 0);
        nint overlay = CreateWindowExW(noActivate | toolWindow, "STATIC", "", popupStyle, bounds.Left,
            bounds.Top, bounds.Width, bounds.Height, target, 0, 0, 0);
        nint pin = CreateWindowExW(noActivate | toolWindow, "STATIC", "", popupStyle, bounds.Left,
            bounds.Top, 18, 12, target, 0, 0, 0);
        nint popup = CreateWindowExW(noActivate | toolWindow, "BUTTON", "", popupStyle, inside.Left,
            inside.Top, inside.Width, inside.Height, 0, 0, 0, 0);
        nint above = CreateWindowExW(noActivate | toolWindow | 0x8, "STATIC", "", popupStyle, inside.Left,
            inside.Top, 12, 12, 0, 0, 0, 0);
        try
        {
            Check(target != 0 && overlay != 0 && pin != 0 && popup != 0 && above != 0,
                "window and ownerless tool popup created");
            nint foreground = NativeMethods.GetForegroundWindow();
            uint thread = NativeMethods.GetWindowThreadProcessId(target, out uint process);
            NativeMethods.ShowWindow(target, 4);
            Check(NativeMethods.PositionAbove(overlay, target, bounds), "window overlay initially covers target");
            NativeMethods.ShowWindow(popup, 4);
            Check(NativeMethods.SetWindowPos(popup, 0, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE |
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE), "tool popup opens above target");
            string popupClass = NativeMethods.ClassOf(popup);
            Check(NativeMethods.FindContainedToolPopup(target, bounds, process, thread, popupClass, true)?.Handle == popup,
                "matching ownerless tool popup detected");
            Check(NativeMethods.FindContainedToolPopup(target, bounds, process, thread, "OTHER", true) is null &&
                NativeMethods.FindContainedToolPopup(target, bounds, 0, thread, popupClass, true) is null &&
                NativeMethods.FindContainedToolPopup(target, bounds, process, thread, popupClass, false) is null,
                "unrelated class and process rejected");
            Check(NativeMethods.PositionAbove(overlay, popup, bounds) &&
                NativeMethods.GetWindow(popup, NativeMethods.GW_HWNDPREV) == overlay,
                "overlay covers foreground-positioned popup");
            Check(NativeMethods.GetForegroundWindow() == foreground &&
                NativeMethods.GetWindow(overlay, NativeMethods.GW_OWNER) == target &&
                NativeMethods.GetVisibleBounds(popup) == inside, "focus, owner and popup position preserved");
            NativeMethods.SetWindowPos(popup, 0, bounds.Right - 20, inside.Top, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            Check(NativeMethods.FindContainedToolPopup(target, bounds, process, thread, popupClass, true) is null,
                "partially overlapping popup does not raise the main window overlay");
            NativeMethods.ShowWindow(popup, NativeMethods.SW_HIDE);
            Check(NativeMethods.FindContainedToolPopup(target, bounds, process, thread, popupClass, true) is null,
                "hidden popup does not raise the overlay");

            NativeMethods.ShowWindow(pin, 4);
            NativeMethods.ShowWindow(above, 4);
            Check(NativeMethods.KeepAboveNormalWindows(overlay, pin), "overlay and pin raised before popup opens");
            Check(IsAbove(above, pin) && IsAbove(pin, overlay), "unrelated always-on-top window stays above the pin and overlay");
            NativeMethods.SetWindowPos(popup, 0, inside.Left, inside.Top, inside.Width, inside.Height,
                NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);
            Check(IsAbove(overlay, popup), "newly shown popup starts below overlay without a correction");
            Check(NativeMethods.SetOverlayOwner(popup, target) &&
                NativeMethods.FindContainedToolPopup(target, bounds, process, thread, popupClass, false)?.Handle == popup,
                "owned tool popup matches the stricter owner rule");
            Check(NativeMethods.PositionAbove(overlay, target, bounds) &&
                NativeMethods.PositionAbove(pin, overlay, bounds) &&
                (NativeMethods.GetWindowLongPtr(overlay, NativeMethods.GWL_EXSTYLE).ToInt64() & 0x8) == 0 &&
                (NativeMethods.GetWindowLongPtr(pin, NativeMethods.GWL_EXSTYLE).ToInt64() & 0x8) == 0 &&
                NativeMethods.GetForegroundWindow() == foreground,
                "overlay and pin return to normal Z-order without changing foreground");
        }
        finally
        {
            if (above != 0) DestroyWindow(above);
            if (popup != 0) DestroyWindow(popup);
            if (pin != 0) DestroyWindow(pin);
            if (overlay != 0) DestroyWindow(overlay);
            if (target != 0) DestroyWindow(target);
        }
    }

    private static bool IsAbove(nint upper, nint lower)
    {
        for (nint window = NativeMethods.GetWindow(lower, NativeMethods.GW_HWNDPREV);
             window != 0; window = NativeMethods.GetWindow(window, NativeMethods.GW_HWNDPREV))
        {
            if (window == upper) return true;
        }
        return false;
    }
}

// The linked NativeMethods file references a title fallback. These tests never
// read window titles or call that fallback, and do not need WinUI resources.
namespace Curtain.App.Localization
{
    internal static class LocalizationService
    {
        internal static string Get(string key, params object?[] args) => key;
    }
}
