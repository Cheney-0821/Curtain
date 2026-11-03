using Curtain.App.Interop;
using Curtain.Core.Settings;
using System.Runtime.InteropServices;

namespace Curtain.App.Services;

internal sealed class GlobalHotkey : IDisposable
{
    private readonly int _id;
    private const uint NoRepeat = 0x4000;
    private readonly nint _window;
    private bool _registered;
    private bool _disposed;

    internal event Action? Pressed;
    internal bool IsRegistered => _registered;
    internal int LastError { get; private set; }

    internal GlobalHotkey(nint window, int id = 0x4352) { _window = window; _id = id; }

    internal bool Apply(GlobalHotkeySettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_window, _id);
            _registered = false;
        }
        LastError = 0;
        if (!settings.Enabled) return true;
        uint modifiers = settings.Modifiers | NoRepeat;
        _registered = NativeMethods.RegisterHotKey(_window, _id, modifiers, settings.VirtualKey);
        if (!_registered) LastError = Marshal.GetLastPInvokeError();
        return _registered;
    }

    internal bool HandleMessage(uint message, nint wParam)
    {
        if (message != NativeMethods.WM_HOTKEY || wParam.ToInt32() != _id) return false;
        Pressed?.Invoke();
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_registered) NativeMethods.UnregisterHotKey(_window, _id);
        _registered = false;
        Pressed = null;
    }
}
