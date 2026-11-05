using Curtain.App.Interop;
using Curtain.Core.Settings;
using System.Runtime.InteropServices;

namespace Curtain.App.Services;

internal sealed class GlobalHotkey : IDisposable
{
    private readonly int _id;
    private int _activeId;
    private const uint NoRepeat = 0x4000;
    private readonly nint _window;
    private GlobalHotkeySettings? _settings;
    private bool _disposed;

    internal event Action? Pressed;
    internal bool IsRegistered => _activeId != 0;
    internal int LastError { get; private set; }

    internal GlobalHotkey(nint window, int id = 0x4352) { _window = window; _id = id; }

    internal bool Apply(GlobalHotkeySettings settings) => TryApply(settings, () => true);

    // Reserve a second ID first. Failed registration or persistence leaves the old ID live.
    internal bool TryApply(GlobalHotkeySettings settings, Func<bool> commit)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LastError = 0;
        if (settings == _settings) return commit();
        int candidate = 0;
        if (settings.Enabled)
        {
            candidate = _activeId == _id ? _id + 0x100 : _id;
            if (!NativeMethods.RegisterHotKey(_window, candidate, settings.Modifiers | NoRepeat, settings.VirtualKey))
            {
                LastError = Marshal.GetLastPInvokeError();
                return false;
            }
        }
        bool committed = false;
        try { committed = commit(); }
        finally
        {
            if (!committed && candidate != 0) NativeMethods.UnregisterHotKey(_window, candidate);
        }
        if (!committed) return false;
        if (_activeId != 0) NativeMethods.UnregisterHotKey(_window, _activeId);
        _activeId = candidate;
        _settings = settings;
        return true;
    }

    internal bool HandleMessage(uint message, nint wParam)
    {
        if (_activeId == 0 || message != NativeMethods.WM_HOTKEY || wParam.ToInt32() != _activeId) return false;
        Pressed?.Invoke();
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_activeId != 0) NativeMethods.UnregisterHotKey(_window, _activeId);
        _activeId = 0;
        Pressed = null;
    }
}
