using Curtain.App.Interop;
using Curtain.App.Windows;
using Curtain.Core.Geometry;
using Curtain.Core.Selection;
using Curtain.Core.Sessions;
using Microsoft.UI.Dispatching;

namespace Curtain.App.Services;

internal sealed class WindowPicker : IDisposable
{
    private readonly CurtainManager _manager;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly nint _management;
    private nint _expectedForeground;
    private SelectionOutlineWindow? _outline;
    private SelectionSpotlightWindow? _spotlight;
    private SelectionMouseHook? _hook;
    private WindowIdentity? _candidate;
    private DateTime _until;
    private string _lastPreview = "";
    private bool _f8Down;
    private int _generation;
    public bool IsActive { get; private set; }
    public event Action<string>? PreviewChanged;
    public event Action<nint, string>? Finished;
    internal WindowPicker(CurtainManager manager, DispatcherQueue dispatcher, nint management)
    {
        _manager = manager; _dispatcher = dispatcher; _management = management;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(50); _timer.IsRepeating = true;
        _timer.Tick += OnTick;
    }
    internal void Start() => Start(_management);

    internal void Start(nint expectedForeground)
    {
        if (IsActive) return;
        if (expectedForeground == nint.Zero) throw new InvalidOperationException("无法确定选窗开始时的前台窗口。");
        _outline = new SelectionOutlineWindow();
        IsActive = true; _candidate = null; _until = DateTime.UtcNow.AddSeconds(45);
        _expectedForeground = expectedForeground;
        int generation = ++_generation;
        _f8Down = (NativeMethods.GetAsyncKeyState(0x77) & 0x8000) != 0;
        try
        {
            _hook = new SelectionMouseHook(_dispatcher, (outcome, point) =>
            {
                if (!IsActive || generation != _generation) return;
                try
                {
                    if (outcome == SelectionOutcome.Cancel) Stop(0, "已取消选择。");
                    else Confirm(point);
                }
                catch (Exception ex)
                {
                    DevelopmentDiagnostics.Write("picker-confirm-error", new { exception = ex.GetType().Name, ex.HResult });
                    Stop(0, "确认遇到异常，已安全结束，请重试。");
                }
            });
            if (!_hook.IsInstalled)
            {
                DevelopmentDiagnostics.Write("picker-hook-unavailable", new { _hook.InstallError });
                _hook.Dispose(); _hook = null;
            }
            _spotlight = new SelectionSpotlightWindow();
            _spotlight.Update(null);
            _timer.Start();
            Preview(_hook is null ? "移动鼠标选择窗口，按 F8 确认，Esc 取消。" : "移动鼠标选择窗口，单击确认，Esc 取消。");
            DevelopmentDiagnostics.Write("picker-start", new { safeMouseConfirmation = _hook is not null });
        }
        catch { Stop(0, "选窗未能启动。"); throw; }
    }
    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (!IsActive) return;
        try
        {
            if (DateTime.UtcNow >= _until || NativeMethods.GetForegroundWindow() != _expectedForeground)
            { Stop(0, "选择已结束，请重新开始。"); return; }
            if ((NativeMethods.GetAsyncKeyState(0x1B) & 0x8000) != 0) { Stop(0, "已取消选择。"); return; }
            if (!NativeMethods.GetCursorPos(out var cursor)) return;
            var point = new PixelPoint(cursor.X, cursor.Y);
            Update(point);
            bool f8 = (NativeMethods.GetAsyncKeyState(0x77) & 0x8000) != 0;
            if (f8 && !_f8Down) Confirm(point);
            _f8Down = f8;
        }
        catch (Exception ex)
        {
            DevelopmentDiagnostics.Write("picker-error", new { exception = ex.GetType().Name, ex.HResult });
            Stop(0, "选窗出现异常，已安全结束，请重试。");
        }
    }
    private nint RootAt(PixelPoint point)
    {
        nint hit = NativeMethods.WindowFromPoint(new NativeMethods.Point { X = point.X, Y = point.Y });
        nint root = NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOT);
        if (root == _outline?.Handle) root = (nint)(_candidate?.Handle ?? 0);
        return _manager.ResolveProtectedWindow(root);
    }
    private void Update(PixelPoint point)
    {
        nint root = RootAt(point);
        if (!NativeMethods.IsSelectableWindow(root) || TargetWindow.Read(root) is not { } identity ||
            NativeMethods.GetVisibleBounds(root) is not { } bounds)
        {
            _candidate = null; _spotlight?.Update(null); _outline?.Hide();
            Preview(_hook is null ? "移动到需要保护的窗口，按 F8 确认。" : "移动到需要保护的窗口，单击确认。");
            return;
        }
        _candidate = identity;
        int dpi = Math.Max(96, (int)NativeMethods.GetDpiForWindow(root));
        _spotlight?.Update(bounds, dpi);
        _outline?.Show(bounds, dpi);
        Preview($"{NativeMethods.TitleOf(root)} · " + (_hook is null ? "F8确认" : "单击确认"));
    }
    private void Confirm(PixelPoint point)
    {
        Update(point);
        if (_candidate is not { } identity || !TargetWindow.Matches(identity)) return;
        Stop((nint)identity.Handle, "窗口已确认。");
    }
    private void Preview(string text)
    {
        if (_lastPreview == text) return;
        _lastPreview = text; PreviewChanged?.Invoke(text);
    }
    private void Stop(nint selected, string message)
    {
        bool wasActive = IsActive;
        IsActive = false; ++_generation; _timer.Stop();
        _hook?.Dispose(); _hook = null;
        _outline?.Dispose(); _outline = null;
        _spotlight?.Dispose(); _spotlight = null;
        _candidate = null; _lastPreview = "";
        DevelopmentDiagnostics.Write("picker-ended", new { selected = selected.ToInt64() });
        if (wasActive) Finished?.Invoke(selected, message);
    }
    public void Dispose()
    { Stop(0, "选择已结束。"); _timer.Tick -= OnTick; }
}
