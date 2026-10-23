using Curtain.App.Interop;
using Curtain.App.Localization;
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
    private LocalizationMessage? _lastPreview;
    private bool _f8Down;
    private int _generation;
    public bool IsActive { get; private set; }
    public event Action<LocalizationMessage>? PreviewChanged;
    public event Action<nint, LocalizationMessage>? Finished;
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
        if (expectedForeground == nint.Zero) throw new InvalidOperationException("PickerNoForeground");
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
                    if (outcome == SelectionOutcome.Cancel) Stop(0, "PickerCancelled");
                    else Confirm(point);
                }
                catch (Exception ex)
                {
                    DevelopmentDiagnostics.Write("picker-confirm-error", new { exception = ex.GetType().Name, ex.HResult });
                    Stop(0, "PickerConfirmationFailed");
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
            Preview(_hook is null ? "PickerPromptF8" : "PickerPromptClick");
            DevelopmentDiagnostics.Write("picker-start", new { safeMouseConfirmation = _hook is not null });
        }
        catch { Stop(0, "PickerFailedStart"); throw; }
    }
    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (!IsActive) return;
        try
        {
            if (DateTime.UtcNow >= _until || NativeMethods.GetForegroundWindow() != _expectedForeground)
            { Stop(0, "PickerEnded"); return; }
            if ((NativeMethods.GetAsyncKeyState(0x1B) & 0x8000) != 0) { Stop(0, "PickerCancelled"); return; }
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
            Stop(0, "PickerFailedSafely");
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
            Preview(_hook is null ? "PickerMoveF8" : "PickerMoveClick");
            return;
        }
        _candidate = identity;
        int dpi = Math.Max(96, (int)NativeMethods.GetDpiForWindow(root));
        _spotlight?.Update(bounds, dpi);
        _outline?.Show(bounds, dpi);
        Preview(_hook is null ? "PickerTargetF8" : "PickerTargetClick", NativeMethods.TitleOf(root));
    }
    private void Confirm(PixelPoint point)
    {
        Update(point);
        if (_candidate is not { } identity || !TargetWindow.Matches(identity)) return;
        Stop((nint)identity.Handle, "PickerConfirmed");
    }
    private void Preview(string key, params object?[] arguments)
    {
        if (_lastPreview is { } last && last.Key == key && last.Arguments.SequenceEqual(arguments)) return;
        _lastPreview = LocalizationMessage.Create(key, arguments);
        PreviewChanged?.Invoke(_lastPreview);
    }
    private void Stop(nint selected, string key)
    {
        bool wasActive = IsActive;
        IsActive = false; ++_generation; _timer.Stop();
        _hook?.Dispose(); _hook = null;
        _outline?.Dispose(); _outline = null;
        _spotlight?.Dispose(); _spotlight = null;
        _candidate = null; _lastPreview = null;
        DevelopmentDiagnostics.Write("picker-ended", new { selected = selected.ToInt64() });
        if (wasActive) Finished?.Invoke(selected, LocalizationMessage.Create(key));
    }
    public void Dispose()
    { Stop(0, "PickerSelectionEnded"); _timer.Tick -= OnTick; }
}
