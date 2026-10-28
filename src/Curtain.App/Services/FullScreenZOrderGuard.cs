using Curtain.App.Interop;
using Microsoft.UI.Dispatching;

namespace Curtain.App.Services;

/// <summary>Checks new windows immediately and coalesces movement while full-screen protection is active.</summary>
internal sealed class FullScreenZOrderGuard : IDisposable
{
    private readonly DispatcherQueueTimer _pending;
    private readonly Action _check;
    private readonly NativeMethods.WinEventProc _callback;
    private readonly List<nint> _hooks = new();
    private bool _disposed;
    private bool _checking;

    internal FullScreenZOrderGuard(DispatcherQueue dispatcher, Action check)
    {
        _check = check;
        _pending = dispatcher.CreateTimer();
        _pending.IsRepeating = false;
        _pending.Interval = TimeSpan.FromMilliseconds(40);
        _pending.Tick += OnTick;
        _callback = OnEvent;
        Add(0x8002, 0x8004); // EVENT_OBJECT_SHOW through EVENT_OBJECT_REORDER.
        Add(NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, NativeMethods.EVENT_OBJECT_LOCATIONCHANGE);
        Add(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND);
    }

    private void Add(uint min, uint max)
    {
        nint hook = NativeMethods.SetWinEventHook(min, max, 0, _callback, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        if (hook != 0) _hooks.Add(hook);
        // The existing one-second screen refresh remains the fallback if registration fails.
    }

    private void OnEvent(nint hook, uint evt, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed || (evt != NativeMethods.EVENT_SYSTEM_FOREGROUND && (objectId != 0 || childId != 0))) return;
        if (evt == NativeMethods.EVENT_OBJECT_LOCATIONCHANGE &&
            (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64() & 8) == 0) return;
        if (evt is 0x8002 or NativeMethods.EVENT_SYSTEM_FOREGROUND)
        {
            CheckNow(); // Do not delay the first chance to cover a newly shown topmost popup.
            return;
        }
        // OUTOFCONTEXT callbacks arrive on the registering UI thread. Do not restart
        // an already pending check, so a stream of events cannot starve it.
        if (!_pending.IsRunning) _pending.Start();
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        CheckNow();
    }

    private void CheckNow()
    {
        if (_disposed || _checking) return;
        _checking = true;
        try { _check(); }
        finally { _checking = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pending.Stop();
        _pending.Tick -= OnTick;
        foreach (nint hook in _hooks) NativeMethods.UnhookWinEvent(hook);
        _hooks.Clear();
        GC.KeepAlive(_callback);
    }
}
