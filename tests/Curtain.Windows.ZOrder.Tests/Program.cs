using System.Runtime.InteropServices;
using Curtain.App.Interop;

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
        Console.WriteLine("Native HWND checks only; Acrylic and WinEvent guard GUI acceptance remain separate.");
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
