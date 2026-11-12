using System.ComponentModel;
using System.Runtime.InteropServices;
using Curtain.App.Interop;
using Curtain.App.Localization;
using Curtain.App.Services;
using Curtain.Core.Geometry;
using Curtain.Core.Settings;
using Microsoft.UI.Dispatching;

namespace Curtain.App.Windows;

/// <summary>Full-display protection sharing the window backdrop and native restore handle.</summary>
internal sealed class ScreenPrivacyWindow
{
    private const double RestoreHandleTop = 32;
    private readonly CurtainWindow _gaussian;
    private readonly PinWindow _nativeControl;
    private readonly DispatcherQueueTimer _controlFadeTimer;
    private bool _disposed;
    private double _controlOpacity, _controlFrom, _controlTo, _controlDuration;
    private long _controlStarted;

    internal nint Handle => _gaussian.Handle;
    internal nint ControlHandle => _nativeControl.Handle;
    internal bool IsVisible => _gaussian.IsVisible;
    internal double DisplayScale => Math.Max(96, NativeMethods.GetDpiForWindow(Handle)) / 96d;
    internal bool ControlPressed => _nativeControl.IsPressed;
    internal event Action? ResumeRequested;
    internal event Action<ScreenPrivacyWindow, bool>? VisibilityTransitionCompleted;

    internal ScreenPrivacyWindow(DispatcherQueue dispatcher)
    {
        try
        {
            _gaussian = new CurtainWindow(dispatcher, 0, 0,
                CurtainDiagnostics.NewOperationId(), Guid.NewGuid().ToString("N"), null);
            _gaussian.SetTargetMaximized(true);
            _gaussian.ConfigureWorkingProtection(new WindowProtectionSettings { BlurAmount = 20 });
            _gaussian.VisibilityTransitionCompleted += OnVisibilityTransitionCompleted;
            _gaussian.RenderFailed += OnRenderFailed;
            _nativeControl = new PinWindow(fullScreen: true);
            _nativeControl.ToggleRequested += OnResume;
            if (!NativeMethods.SetOverlayOwner(ControlHandle, Handle, out int errorCode))
                throw new Win32Exception(errorCode, "Unable to attach fullscreen restore control.");
            _controlFadeTimer = dispatcher.CreateTimer();
            _controlFadeTimer.Interval = TimeSpan.FromMilliseconds(16);
            _controlFadeTimer.IsRepeating = true;
            _controlFadeTimer.Tick += OnControlFade;
            RefreshLocalization();
        }
        catch
        {
            _nativeControl?.Close();
            _gaussian?.CloseOwnedWindow();
            throw;
        }
    }

    internal void Cover(ScreenRect display)
    {
        PrivacyNative.PositionFullScreen(Handle, display, showWindow: false);
        double scale = DisplayScale;
        int width = (int)Math.Round(120 * scale), height = (int)Math.Round(24 * scale);
        int x = display.Left + (display.Width - width) / 2;
        int y = display.Top + (int)Math.Round((RestoreHandleTop - 3) * scale);
        _nativeControl.UpdatePlacement(new DockedPin(new PixelRect(x, y, x + width, y + height), false, true),
            (int)Math.Round(96 * scale));
        if (!NativeMethods.SetWindowPos(ControlHandle, (nint)(-1), x, y, width, height,
            NativeMethods.SWP_NOACTIVATE))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to position fullscreen restore control.");
    }

    internal bool IsOverControl(NativeMethods.Point point) =>
        NativeMethods.IsWindowVisible(ControlHandle) && _nativeControl.ContainsPoint(point);

    internal void SetControlVisible(bool visible, bool animate = true)
    {
        if (_disposed) return;
        double target = visible ? 1 : 0;
        if (animate && _controlTo == target) return;
        _controlFadeTimer.Stop();
        _controlTo = target;
        if (!animate)
        {
            _controlOpacity = target;
            _nativeControl.SetOpacity(target);
            NativeMethods.ShowWindow(ControlHandle, visible ? 4 : 0);
            return;
        }
        _controlFrom = _controlOpacity;
        _controlDuration = Curtain.Core.Rendering.OverlayFadeCurve.DurationFor(_controlFrom * 255, target * 255);
        _controlStarted = Environment.TickCount64;
        _nativeControl.SetOpacity(_controlFrom);
        NativeMethods.ShowWindow(ControlHandle, 4);
        _controlFadeTimer.Start();
    }

    private void OnControlFade(DispatcherQueueTimer sender, object args)
    {
        double elapsed = Environment.TickCount64 - _controlStarted;
        _controlOpacity = Curtain.Core.Rendering.OverlayFadeCurve.Sample(_controlFrom, _controlTo, elapsed, _controlDuration);
        _nativeControl.SetOpacity(_controlOpacity);
        if (elapsed < _controlDuration) return;
        _controlFadeTimer.Stop();
        if (_controlTo == 0) NativeMethods.ShowWindow(ControlHandle, 0);
    }

    internal void SetProtectedVisible(bool visible, bool animate, long? batchStart = null)
    {
        if (_disposed) return;
        if (!visible) SetControlVisible(false, animate: false);
        _gaussian.SetProtectedVisible(visible, animate, batchStart);
    }

    internal void CancelAndHide()
    {
        if (_disposed) return;
        SetControlVisible(false, animate: false);
        _gaussian.CancelAndHide();
    }

    private void OnResume() => ResumeRequested?.Invoke();
    private void OnRenderFailed()
    {
        CancelAndHide();
        ResumeRequested?.Invoke();
    }
    private void OnVisibilityTransitionCompleted(bool visible)
    {
        if (visible)
            CurtainDiagnostics.Write("fullscreen.backdrop.state", stage: "visible-backdrop",
                reasonCode: "GaussianBackdrop", hwnd: Handle.ToInt64());
        VisibilityTransitionCompleted?.Invoke(this, visible);
    }

    internal void RefreshLocalization()
    {
        SetWindowTextW(Handle, LocalizationService.Get("ScreenPrivacyTitle"));
        SetWindowTextW(ControlHandle, LocalizationService.Get("ResumeUse"));
    }

    internal void CloseOverlay()
    {
        if (_disposed) return;
        CancelAndHide();
        _disposed = true;
        _controlFadeTimer.Tick -= OnControlFade;
        _nativeControl.ToggleRequested -= OnResume;
        _gaussian.VisibilityTransitionCompleted -= OnVisibilityTransitionCompleted;
        _gaussian.RenderFailed -= OnRenderFailed;
        _nativeControl.Close();
        _gaussian.CloseOwnedWindow();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowTextW(nint hwnd, string text);
}
