using Curtain.App.Interop;
using Microsoft.UI.Dispatching;

namespace Curtain.App.Services;

/// <summary>
/// Observes top-level window create/show/destroy events and performs a low-frequency
/// EnumWindows reconciliation to recover from missed WinEvents. All callbacks are marshalled
/// onto the UI DispatcherQueue before touching the manager or WinUI state.
/// </summary>
internal sealed class ProtectedAppRuleWatcher : IDisposable
{
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectShow = 0x8002;
    private const int ObjIdWindow = 0;

    private delegate bool EnumWindowsProc(nint hwnd, nint parameter);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    private readonly DispatcherQueue _dispatcher;
    private readonly Action<nint, uint> _windowEvent;
    private readonly NativeMethods.WinEventProc _winEventProc;
    private readonly EnumWindowsProc _enumProc;
    private readonly DispatcherQueueTimer _reconcileTimer;
    private nint _hook;
    private volatile bool _disposed;

    internal bool EventHookInstalled => _hook != nint.Zero;

    internal ProtectedAppRuleWatcher(DispatcherQueue dispatcher, Action<nint, uint> windowEvent)
    {
        _dispatcher = dispatcher;
        _windowEvent = windowEvent;
        _winEventProc = OnWinEvent;
        _enumProc = OnEnumWindow;
        _reconcileTimer = dispatcher.CreateTimer();
        _reconcileTimer.Interval = TimeSpan.FromSeconds(2);
        _reconcileTimer.IsRepeating = true;
        _reconcileTimer.Tick += OnReconcileTick;
    }

    internal void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ProtectedAppRuleWatcher));
        if (_hook != nint.Zero) return;

        _hook = NativeMethods.SetWinEventHook(EventObjectCreate, EventObjectShow, nint.Zero,
            _winEventProc, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        CurtainDiagnostics.Write("rule-watcher.started", stage: "win-event-hook",
            reasonCode: _hook == nint.Zero ? "HookUnavailablePollingFallback" : "HookInstalled",
            errorCode: _hook == nint.Zero ? System.Runtime.InteropServices.Marshal.GetLastPInvokeError() : 0);

        EnumerateNow();
        _reconcileTimer.Start();
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int objectId,
        int childId, uint eventThread, uint eventTime)
    {
        if (_disposed || hwnd == nint.Zero || objectId != ObjIdWindow || childId != 0) return;
        if (eventType is not (EventObjectCreate or EventObjectDestroy or EventObjectShow)) return;

        _dispatcher.TryEnqueue(() =>
        {
            if (!_disposed) _windowEvent(hwnd, eventType);
        });
    }

    private void OnReconcileTick(DispatcherQueueTimer sender, object args) => EnumerateNow();

    private void EnumerateNow()
    {
        if (_disposed) return;
        try
        {
            if (!EnumWindows(_enumProc, nint.Zero))
                CurtainDiagnostics.Write("rule-watcher.enumeration-failed", CurtainDiagnosticLevel.Warning,
                    stage: "enum-windows", reasonCode: "EnumWindowsReturnedFalse",
                    errorCode: System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
        }
        catch (Exception ex)
        {
            CurtainDiagnostics.WriteException("rule-watcher.enumeration-failed", ex,
                stage: "enum-windows", reasonCode: "EnumWindowsException");
        }
    }

    private bool OnEnumWindow(nint hwnd, nint parameter)
    {
        if (_disposed) return false;
        if (hwnd != nint.Zero) _windowEvent(hwnd, 0);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reconcileTimer.Stop();
        _reconcileTimer.Tick -= OnReconcileTick;
        if (_hook != nint.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = nint.Zero;
        }
        GC.KeepAlive(_winEventProc);
        GC.KeepAlive(_enumProc);
    }
}
