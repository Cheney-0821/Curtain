using Curtain.App.Interop;
using Microsoft.UI.Dispatching;

namespace Curtain.App.Services;

/// <summary>
/// WinEvent callbacks are short, no WinUI access before dispatching. Delegate stays rooted.
/// Install on the UI thread because that thread owns the message loop.
/// </summary>
internal sealed class WindowHooks : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _refresh;
    private readonly nint _target;
    private readonly NativeMethods.WinEventProc _callback;
    private readonly List<nint> _hooks = new();
    private volatile bool _disposed;
    public bool IsDestroyed { get; private set; }

    public WindowHooks(DispatcherQueue dispatcher, nint target, Action refresh)
    {
        _dispatcher = dispatcher;
        _refresh = refresh;
        _target = target;
        _callback = OnEvent;
        Add(NativeMethods.EVENT_OBJECT_DESTROY, NativeMethods.EVENT_OBJECT_LOCATIONCHANGE);
        Add(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND);
        Add(NativeMethods.EVENT_SYSTEM_MINIMIZESTART, NativeMethods.EVENT_SYSTEM_MINIMIZEEND);
        if (_hooks.Count != 3)
        {
            Dispose();
            throw new InvalidOperationException("Unable to install WinEvent hooks");
        }
    }

    private void Add(uint min, uint max)
    {
        nint handle = NativeMethods.SetWinEventHook(min, max, nint.Zero, _callback, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        if (handle != nint.Zero) _hooks.Add(handle);
    }

    private void OnEvent(nint hook, uint evt, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed || objectId != 0) return; // OBJID_WINDOW; ignore child UI accessibility events
        if (evt != NativeMethods.EVENT_SYSTEM_FOREGROUND && hwnd != _target) return;
        if (evt == NativeMethods.EVENT_OBJECT_DESTROY && hwnd == _target) IsDestroyed = true;
        _dispatcher.TryEnqueue(() => { if (!_disposed) _refresh(); });
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (nint hook in _hooks) NativeMethods.UnhookWinEvent(hook);
        _hooks.Clear();
        GC.KeepAlive(_callback);
    }
}
