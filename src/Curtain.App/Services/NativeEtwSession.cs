using System.Runtime.InteropServices;

namespace Curtain.App.Services;

internal static class NativeEtwSession
{
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ControlTraceW(ulong handle, string name, nint properties, uint control);

    internal static (uint Code, string OutputPath) Query(string name)
    {
        // EVENT_TRACE_PROPERTIES on this x64 application, followed by two UTF-16 buffers.
        const int size = 120 + 2048 + 65536;
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteInt32(buffer, 0, size);
            Marshal.WriteInt32(buffer, 116, 120);
            Marshal.WriteInt32(buffer, 112, 120 + 2048);
            uint code = ControlTraceW(0, name, buffer, 0);
            int offset = Marshal.ReadInt32(buffer, 112);
            string path = code == 0 && offset >= 120 && offset < size
                ? Marshal.PtrToStringUni(buffer + offset) ?? string.Empty : string.Empty;
            return (code, path);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
