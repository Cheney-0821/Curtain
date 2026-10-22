using System.ComponentModel;
using System.Diagnostics;
using Curtain.Core.Sessions;

namespace Curtain.App.Interop;

internal static class TargetWindow
{
    internal static WindowIdentity? Read(nint hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd)) return null;
        uint thread = NativeMethods.GetWindowThreadProcessId(hwnd, out uint process);
        string cls = NativeMethods.ClassOf(hwnd);
        if (process == 0 || cls.Length == 0) return null;
        try
        {
            using var instance = Process.GetProcessById((int)process);
            return new(hwnd.ToInt64(), process, thread, cls, instance.StartTime.ToUniversalTime().Ticks);
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (Win32Exception) { return null; }
    }
    internal static bool Matches(WindowIdentity identity) => Read((nint)identity.Handle) == identity;
    internal static bool Matches(WindowIdentity identity, Process instance)
    {
        try
        {
            nint hwnd = (nint)identity.Handle;
            if (instance.HasExited || !NativeMethods.IsWindow(hwnd)) return false;
            uint thread = NativeMethods.GetWindowThreadProcessId(hwnd, out uint process);
            return thread == identity.ThreadId && process == identity.ProcessId && NativeMethods.ClassOf(hwnd) == identity.ClassName;
        }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return false; }
    }
}
