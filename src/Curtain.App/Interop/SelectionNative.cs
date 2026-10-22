using System.Runtime.InteropServices;

namespace Curtain.App.Interop;

internal static class SelectionNative
{
    internal delegate nint MouseProc(int code, nuint message, nint data);
    [StructLayout(LayoutKind.Sequential)] internal struct MouseData
    { public NativeMethods.Point Point; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct Message
    { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public NativeMethods.Point Point; public uint Private; }
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookExW(int id, MouseProc proc, nint module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll")] internal static extern int GetMessageW(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] internal static extern bool PeekMessageW(out Message message, nint window, uint min, uint max, uint flags);
    [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] internal static extern nint DispatchMessageW(ref Message message);
    [DllImport("user32.dll")] internal static extern bool PostThreadMessageW(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern nuint SetTimer(nint window, nuint id, uint interval, nint callback);
    [DllImport("user32.dll")] internal static extern bool KillTimer(nint window, nuint id);
}
