using System.Diagnostics;
using System.Runtime.InteropServices;
using Curtain.Core.Rendering;
using Microsoft.UI.Dispatching;

namespace Curtain.App.Services;

/// <summary>Animates one overlay HWND while leaving its system Acrylic backdrop intact.</summary>
internal sealed class OverlayOpacityFade : IDisposable
{
    private const uint Layered = 0x00080000;
    private const uint LayeredAlpha = 0x00000002;
    private const uint FrameChanged = 0x0020;
    private const int ExStyleIndex = -20;
    private const int Hide = 0;
    private const int ShowNoActivate = 4;

    private readonly nint _hwnd;
    private readonly uint _threadId;
    private readonly uint _processId;
    private readonly DispatcherQueueTimer _timer;
    private bool _ownedLayeredStyle;
    private bool _layeredReady;
    private bool _disposed;
    private bool _running;
    private bool _desiredVisible;
    private double _alpha;
    private double _from;
    private double _to;
    private double _durationMs;
    private long _startedAt;

    internal bool IsVisible => IsOwnedWindow() && IsWindowVisible(_hwnd);
    internal static long CaptureBatchStart() => Stopwatch.GetTimestamp();

    internal OverlayOpacityFade(nint hwnd, DispatcherQueue dispatcher)
    {
        if (hwnd == nint.Zero || !IsWindow(hwnd))
            throw new ArgumentException("A valid overlay HWND is required.", nameof(hwnd));
        ArgumentNullException.ThrowIfNull(dispatcher);
        _hwnd = hwnd;
        _threadId = GetWindowThreadProcessId(hwnd, out _processId);
        if (_threadId == 0 || _threadId != GetCurrentThreadId())
            throw new InvalidOperationException("Overlay opacity changes must run on the HWND's UI thread.");
        _desiredVisible = IsWindowVisible(hwnd);
        _alpha = _desiredVisible ? 255 : 0;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.IsRepeating = true;
        _timer.Tick += OnTick;
    }

    /// <summary>Call after updating geometry without SWP_SHOWWINDOW.</summary>
    internal void SetVisible(bool visible, bool animate, long? batchStart = null)
    {
        if (_disposed || !IsOwnedWindow()) return;
        EnsureThread();

        if (!animate)
        {
            if (_desiredVisible == visible && !_running && IsWindowVisible(_hwnd) == visible) return;
            Immediate(visible);
            return;
        }

        if (_desiredVisible == visible && (_running || IsWindowVisible(_hwnd) == visible))
            return; // A 300 ms refresh must not restart an active transition.

        try
        {
            if (!EnsureLayered()) { Immediate(visible); return; }
            long now = Stopwatch.GetTimestamp();
            // The timer can lag behind input. Sample at the direction change instead of
            // using the previous Tick's stale alpha (especially before the first Tick).
            if (_running) _alpha = SampleAt(now);
            if (!visible && OverlayFadeCurve.NearlyTransparent(_alpha))
            {
                Immediate(false);
                return;
            }
            _desiredVisible = visible;
            _from = _alpha;
            _to = visible ? 255 : 0;
            _durationMs = OverlayFadeCurve.DurationFor(_from, _to);
            _startedAt = batchStart ?? now;
            if (visible)
            {
                // Never expose an alpha-zero HWND, which would pass mouse input through.
                byte first = (byte)Math.Clamp((int)Math.Round(_alpha), 1, 255);
                if (!SetLayeredWindowAttributes(_hwnd, 0, first, LayeredAlpha))
                {
                    Immediate(true);
                    return;
                }
                ShowWindow(_hwnd, ShowNoActivate);
            }
            _running = true;
            _timer.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Curtain opacity fade fallback: {ex.GetType().Name}: {ex.Message}");
            Immediate(visible);
        }
    }

    internal void CancelAndHide() => Immediate(false);

    private double SampleAt(long now) => OverlayFadeCurve.Sample(_from, _to,
        (now - _startedAt) * 1000.0 / Stopwatch.Frequency, _durationMs);

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || !IsOwnedWindow()) { Stop(); return; }
        if (!_running) return;
        long now = Stopwatch.GetTimestamp();
        _alpha = SampleAt(now);
        if (!_desiredVisible && OverlayFadeCurve.NearlyTransparent(_alpha))
        {
            // At this opacity the overlay is effectively invisible but can still
            // intercept pointer input. Hide before resetting its layered alpha.
            Immediate(false);
            return;
        }
        byte value = (byte)Math.Clamp((int)Math.Round(_alpha), 1, 255);
        if (!SetLayeredWindowAttributes(_hwnd, 0, value, LayeredAlpha))
        {
            Debug.WriteLine($"Curtain opacity fade Win32 error: {Marshal.GetLastPInvokeError()}");
            Immediate(_desiredVisible);
            return;
        }
        double elapsed = (now - _startedAt) * 1000.0 / Stopwatch.Frequency;
        if (elapsed < _durationMs) return;
        _alpha = _to;
        Stop();
    }

    private void Immediate(bool visible)
    {
        if (_disposed || !IsOwnedWindow()) return;
        EnsureThread();
        Stop();
        _desiredVisible = visible;
        _alpha = visible ? 255 : 0;
        if (visible)
        {
            if (_layeredReady && !SetLayeredWindowAttributes(_hwnd, 0, 255, LayeredAlpha))
                RestoreLayeredStyle();
            ShowWindow(_hwnd, ShowNoActivate);
        }
        else
        {
            ShowWindow(_hwnd, Hide);
            if (_layeredReady && !SetLayeredWindowAttributes(_hwnd, 0, 0, LayeredAlpha))
                RestoreLayeredStyle();
        }
    }

    private bool EnsureLayered()
    {
        if (_layeredReady) return true;
        long before = GetWindowLongPtr(_hwnd, ExStyleIndex).ToInt64();
        if ((before & Layered) != 0) return false; // Preserve styles owned by WinUI or another subsystem.

        SetLastError(0);
        SetWindowLongPtr(_hwnd, ExStyleIndex, (nint)(before | Layered));
        // Record ownership before readback so a partially applied style is still restored.
        _ownedLayeredStyle = true;
        long applied = GetWindowLongPtr(_hwnd, ExStyleIndex).ToInt64();
        if ((applied & Layered) == 0)
        {
            Debug.WriteLine($"Failed to set WS_EX_LAYERED: {Marshal.GetLastPInvokeError()}");
            RestoreLayeredStyle();
            return false;
        }
        if (SetWindowPos(_hwnd, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | FrameChanged) &&
            SetLayeredWindowAttributes(_hwnd, 0, 255, LayeredAlpha))
        {
            _layeredReady = true;
            return true;
        }

        Debug.WriteLine($"Failed to initialize layered alpha: {Marshal.GetLastPInvokeError()}");
        RestoreLayeredStyle();
        return false;
    }

    private void RestoreLayeredStyle()
    {
        if (!_ownedLayeredStyle || !IsOwnedWindow())
        {
            _layeredReady = false;
            return;
        }
        long style = GetWindowLongPtr(_hwnd, ExStyleIndex).ToInt64();
        if ((style & Layered) != 0)
        {
            SetLastError(0);
            SetWindowLongPtr(_hwnd, ExStyleIndex, (nint)(style & ~Layered));
            if ((GetWindowLongPtr(_hwnd, ExStyleIndex).ToInt64() & Layered) != 0)
            {
                Debug.WriteLine($"Failed to restore overlay extended style: {Marshal.GetLastPInvokeError()}");
                _layeredReady = false;
                return;
            }
            SetWindowPos(_hwnd, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | FrameChanged);
        }
        _ownedLayeredStyle = false;
        _layeredReady = false;
    }

    private bool IsOwnedWindow() => IsWindow(_hwnd) &&
        GetWindowThreadProcessId(_hwnd, out uint processId) == _threadId && processId == _processId;

    private void Stop()
    {
        _running = false;
        _timer.Stop();
    }

    private void EnsureThread()
    {
        if (_threadId != GetCurrentThreadId())
            throw new InvalidOperationException("Overlay opacity changes must run on the HWND's UI thread.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        EnsureThread();
        Stop();
        _timer.Tick -= OnTick;
        if (IsOwnedWindow())
        {
            ShowWindow(_hwnd, Hide);
            RestoreLayeredStyle();
        }
        _disposed = true;
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern void SetLastError(uint error);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);
}
