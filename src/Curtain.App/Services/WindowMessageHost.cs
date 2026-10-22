using Curtain.App.Interop;
using System.Runtime.InteropServices;

namespace Curtain.App.Services;

internal sealed class WindowMessageHost : IDisposable
{
    private const nuint SubclassId = 0x43555254;
    private readonly NativeMethods.SubclassProc _procedure;
    private readonly nint _handle;
    private bool _disposed;

    internal event Action<uint, nint, nint>? Message;

    internal WindowMessageHost(nint handle)
    {
        _handle = handle;
        _procedure = OnMessage;
        if (!NativeMethods.SetWindowSubclass(handle, _procedure, SubclassId, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
    }

    private nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam, nuint subclassId, nuint referenceData)
    {
        try { Message?.Invoke(message, wParam, lParam); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Curtain window message: {ex}"); }
        return NativeMethods.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Message = null;
        NativeMethods.RemoveWindowSubclass(_handle, _procedure, SubclassId);
    }
}
