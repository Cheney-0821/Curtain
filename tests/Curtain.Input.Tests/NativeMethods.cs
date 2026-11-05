using System.Runtime.InteropServices;

namespace Curtain.App.Interop;

internal static class NativeMethods
{
    internal const uint WM_HOTKEY = 0x312;
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint hwnd);
}
