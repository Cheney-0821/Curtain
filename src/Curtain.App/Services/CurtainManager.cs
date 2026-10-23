using System.Collections.ObjectModel;
using Curtain.App.Interop;
using Curtain.Core.Sessions;
using Microsoft.UI.Dispatching;

namespace Curtain.App.Services;

internal sealed class CurtainManager : IDisposable
{
    private readonly TargetRegistry _registry = new();
    private readonly Dictionary<long, CurtainSession> _sessions = new();
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _watchdog;
    private bool _disposed;
    private bool _idleForcedClosed;
    internal bool HasWindows => _sessions.Count != 0;
    public ObservableCollection<ProtectedWindowItem> Windows { get; } = new();
    public event Action<string>? Notice;
    public event Action? CollectionChanged;
    internal CurtainManager(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _watchdog = dispatcher.CreateTimer();
        _watchdog.Interval = TimeSpan.FromMilliseconds(300);
        _watchdog.IsRepeating = true;
        _watchdog.Tick += OnTick;
        _watchdog.Start();
    }
    internal ProtectedWindowItem Attach(nint hwnd)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!NativeMethods.IsSelectableWindow(hwnd) || TargetWindow.Read(hwnd) is not { } identity)
            throw new InvalidOperationException("WindowNotSelectable");
        if (_sessions.TryGetValue(identity.Handle, out var previous))
        {
            if (!previous.IsDestroyed && previous.Target.Identity == identity)
            {
                return previous.Item;
            }
            Remove(previous);
        }
        var target = _registry.Attach(identity);
        CurtainSession? session = null;
        try
        {
            session = new(target, _dispatcher);
            if (_idleForcedClosed) session.SetIdleForcedClosed(true);
            session.Ended += OnEnded;
            session.Notice += OnNotice;
            _sessions.Add(identity.Handle, session);
            Windows.Add(session.Item);
            CollectionChanged?.Invoke();
            return session.Item;
        }
        catch { session?.Dispose(); _registry.Remove(target); throw; }
    }
    internal nint ResolveProtectedWindow(nint hwnd)
    {
        foreach (var session in _sessions.Values)
            if (session.OwnsOverlay(hwnd)) return (nint)session.Target.Identity.Handle;
        return hwnd;
    }
    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        foreach (var session in _sessions.Values.ToArray()) session.RefreshSafely();
    }
    internal void SetIdleForcedClosed(bool forced, bool animate = false)
    {
        if (_disposed || _idleForcedClosed == forced) return;
        _idleForcedClosed = forced;
        foreach (var session in _sessions.Values.ToArray()) session.SetIdleForcedClosed(forced, animate);
    }
    internal void ReapplyIdleForcedClosed()
    {
        if (_disposed || !_idleForcedClosed) return;
        foreach (var session in _sessions.Values.ToArray()) session.ReapplyIdleForcedClosed();
    }
    internal void SetAllClosed(bool closed)
    {
        if (_disposed) return;
        // During idle protection, bulk actions are temporary as well.
        // Keep the baseline manual state intact for "End automatic protection".
        if (!_idleForcedClosed) _registry.SetAllClosed(closed);
        foreach (var session in _sessions.Values.ToArray()) session.SetClosed(closed);
    }
    internal void Detach(ProtectedWindowItem item) => Remove(item.Session);
    private void OnNotice(string message) => Notice?.Invoke(message);
    private void OnEnded(CurtainSession session, string message)
    { Remove(session); Notice?.Invoke(message); }
    private void Remove(CurtainSession session)
    {
        if (!_registry.Remove(session.Target)) return;
        _sessions.Remove(session.Target.Identity.Handle);
        session.Ended -= OnEnded; session.Notice -= OnNotice;
        session.Dispose(); Windows.Remove(session.Item);
        CollectionChanged?.Invoke();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watchdog.Stop(); _watchdog.Tick -= OnTick;
        foreach (var session in _sessions.Values.ToArray()) Remove(session);
    }
}
