using System.Runtime.InteropServices;
using Curtain.App.Interop;
using Curtain.Core.Geometry;
using Curtain.Core.Selection;
using Microsoft.UI.Dispatching;

namespace Curtain.App.Services;

internal sealed class SelectionMouseHook : IDisposable
{
    private const uint StopMessage = 0x8001;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly SelectionInputGate _gate = new();
    private readonly SelectionNative.MouseProc _procedure;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<SelectionOutcome, PixelPoint> _finished;
    private nint _hook;
    private uint _threadId;
    private volatile bool _stopping;
    private PixelPoint _press;
    private int _started, _readyDisposed, _disposed;
    public bool IsInstalled { get; private set; }
    public int InstallError { get; private set; }

    internal SelectionMouseHook(DispatcherQueue dispatcher, Action<SelectionOutcome, PixelPoint> finished)
    {
        _dispatcher = dispatcher; _finished = finished; _procedure = OnMouse;
        _thread = new Thread(Run) { IsBackground = true, Name = "Curtain temporary window picker" };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(2))) { InstallError = 1460; Dispose(); }
        Volatile.Write(ref _started, 1);
        if (!_thread.IsAlive) DisposeReady();
    }
    private void Run()
    {
        nuint drainTimer = 0;
        DateTime drainUntil = DateTime.MaxValue;
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
                    drainUntil = DateTime.UtcNow.AddSeconds(2);
                    drainTimer = SelectionNative.SetTimer(0, 0, 50, 0);
                    if (drainTimer == 0) break;
                }
                if (_stopping && (!_gate.HasHeldButton || DateTime.UtcNow >= drainUntil)) break;
                SelectionNative.TranslateMessage(ref message);
                SelectionNative.DispatchMessageW(ref message);
            }
        }
        catch (Exception ex)
        {
            InstallError = ex.HResult;
            _ready.Set();
            DevelopmentDiagnostics.Write("picker-hook-error", new { exception = ex.GetType().Name, ex.HResult });
        }
        finally
        {
            if (drainTimer != 0) SelectionNative.KillTimer(0, drainTimer);
            if (_hook != 0) SelectionNative.UnhookWindowsHookEx(_hook);
            _hook = 0; IsInstalled = false;
            DevelopmentDiagnostics.Write("picker-hook-ended", new { thread = _threadId });
            if (Volatile.Read(ref _started) != 0) DisposeReady();
            GC.KeepAlive(_procedure);
        }
    }
    private nint OnMouse(int code, nuint message, nint data)
    {
        if (code < 0) return SelectionNative.CallNextHookEx(_hook, code, message, data);
        try
        {
            uint id = (uint)message;
            if (id == 0x201)
            {
                var input = Marshal.PtrToStructure<SelectionNative.MouseData>(data);
                _press = new(input.Point.X, input.Point.Y);
            }
            var result = _gate.Handle(id, !_stopping);
            if (result.Outcome != SelectionOutcome.None)
            {
                var point = _press;
                _dispatcher.TryEnqueue(() => { if (!_stopping) _finished(result.Outcome, point); });
            }
            if (result.Suppress) return 1;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        return SelectionNative.CallNextHookEx(_hook, code, message, data);
    }
    private void DisposeReady()
    { if (Interlocked.Exchange(ref _readyDisposed, 1) == 0) _ready.Dispose(); }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping = true;
        if (_threadId != 0) SelectionNative.PostThreadMessageW(_threadId, StopMessage, 0, 0);
        if (_thread != Thread.CurrentThread && _thread.Join(100) && Volatile.Read(ref _started) != 0) DisposeReady();
    }
}
