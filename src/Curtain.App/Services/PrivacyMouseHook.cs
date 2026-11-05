using System.Runtime.InteropServices;
using Curtain.App.Interop;
using Curtain.Core.Protection;
using Microsoft.UI.Dispatching;

namespace Curtain.App.Services;

/// <summary>A dedicated message thread keeps the optional side-key hook out of UI work.</summary>
internal sealed class PrivacyMouseHook : IDisposable
{
    private const uint StopMessage = 0x8002;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly SelectionNative.MouseProc _procedure;
    private readonly SideButtonInputGate _gate = new();
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _toggle;
    private readonly PrivacyMouseButton _selected;
    private nint _hook;
    private uint _threadId;
    private volatile bool _stopping;
    private int _disposed;
    private int _started, _readyDisposed;
    internal bool IsInstalled { get; private set; }
    internal int InstallError { get; private set; }

    internal PrivacyMouseHook(DispatcherQueue dispatcher, PrivacyMouseButton selected, Action toggle)
    {
        if (selected is not (PrivacyMouseButton.Back or PrivacyMouseButton.Forward))
            throw new ArgumentOutOfRangeException(nameof(selected));
        _dispatcher = dispatcher; _selected = selected; _toggle = toggle; _procedure = OnMouse;
        _thread = new Thread(Run) { IsBackground = true, Name = "Curtain privacy side key" };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(2))) { InstallError = 1460; Dispose(); }
        Volatile.Write(ref _started, 1);
        if (!_thread.IsAlive) DisposeReady();
    }

    private void Run()
    {
        nuint drainTimer = 0;
        long drainUntil = long.MaxValue;
        try
        {
            _threadId = SelectionNative.GetCurrentThreadId();
            SelectionNative.PeekMessageW(out _, 0, 0, 0, 0);
            _hook = SelectionNative.SetWindowsHookExW(14, _procedure, HandleNative.GetModuleHandleW(null), 0);
            IsInstalled = _hook != 0;
            if (!IsInstalled) InstallError = Marshal.GetLastPInvokeError();
            _ready.Set();
            if (!IsInstalled || _stopping) return;
            while (SelectionNative.GetMessageW(out var message, 0, 0, 0) > 0)
            {
                if (message.Id == StopMessage)
                {
                    if (!_gate.HasHeldButton) break;
                    drainUntil = Environment.TickCount64 + 2000;
                    drainTimer = SelectionNative.SetTimer(0, 0, 25, 0);
                    if (drainTimer == 0) break;
                }
                if (_stopping && (!_gate.HasHeldButton || Environment.TickCount64 >= drainUntil)) break;
                SelectionNative.TranslateMessage(ref message);
                SelectionNative.DispatchMessageW(ref message);
            }
        }
        catch (Exception ex)
        {
            InstallError = ex.HResult;
            _ready.Set();
            CurtainDiagnostics.WriteException("fullscreen.side-key.failed", ex,
                stage: "mouse-hook", reasonCode: "HookThreadFailed");
        }
        finally
        {
            if (drainTimer != 0) SelectionNative.KillTimer(0, drainTimer);
            if (_hook != 0) SelectionNative.UnhookWindowsHookEx(_hook);
            _hook = 0; IsInstalled = false;
            CurtainDiagnostics.Write("fullscreen.side-key.stopped", stage: "mouse-hook", reasonCode: "Stopped");
            if (Volatile.Read(ref _started) != 0) DisposeReady();
            GC.KeepAlive(_procedure);
        }
    }

    private nint OnMouse(int code, nuint message, nint data)
    {
        if (code >= 0 && (uint)message is 0x20B or 0x20C)
        {
            var input = Marshal.PtrToStructure<SelectionNative.MouseData>(data);
            var result = _gate.Handle((uint)message, input.Data, _selected, !_stopping);
            if (result.Toggle)
                _dispatcher.TryEnqueue(() => { if (!_stopping) _toggle(); });
            if (result.Suppress) return 1;
        }
        return SelectionNative.CallNextHookEx(_hook, code, message, data);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping = true;
        if (_threadId != 0) SelectionNative.PostThreadMessageW(_threadId, StopMessage, 0, 0);
        // Allow a consumed down to drain its up, without blocking the UI for two seconds.
        if (_thread.Join(100) && Volatile.Read(ref _started) != 0) DisposeReady();
    }
    private void DisposeReady()
    { if (Interlocked.Exchange(ref _readyDisposed, 1) == 0) _ready.Dispose(); }
}
