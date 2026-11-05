using Curtain.App.Interop;
using Curtain.App.Services;
using Curtain.Core.Settings;

// Real Win32 registration, message-only HWND; no keyboard/mouse injection or visible UI.
if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
nint window = NativeMethods.CreateWindowExW(0, "STATIC", "Curtain input assertions", 0, 0, 0, 0, 0, -3, 0, 0, 0);
if (window == 0) throw new Exception("Cannot create test message window.");
int passed = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name); passed++;
}
const uint mods = HotkeyModifier.Control | HotkeyModifier.Alt | HotkeyModifier.Shift;
var free = new List<uint>();
try
{
    for (uint key = 0x41; key <= 0x5A && free.Count < 3; key++)
        if (NativeMethods.RegisterHotKey(window, 6000, mods | 0x4000, key))
        { free.Add(key); NativeMethods.UnregisterHotKey(window, 6000); }
    Check(free.Count == 3, "found temporary free chords without replacing any existing binding");
    var old = new GlobalHotkeySettings(true, mods, free[0]);
    var next = new GlobalHotkeySettings(true, mods, free[1]);
    var busy = new GlobalHotkeySettings(true, mods, free[2]);
    using var registration = new GlobalHotkey(window);
    Check(registration.Apply(old), "initial real Win32 hotkey registration");
    int presses = 0;
    registration.Pressed += () => presses++;
    Check(registration.HandleMessage(NativeMethods.WM_HOTKEY, 0x4352) && presses == 1,
        "active hotkey ID dispatches while main UI is absent");
    Check(registration.Apply(old), "unchanged combination does not re-register");
    Check(NativeMethods.RegisterHotKey(window, 6000, mods | 0x4000, busy.VirtualKey), "reserve conflicting combination");
    bool committed = false;
    Check(!registration.TryApply(busy, () => { committed = true; return true; }) && !committed && registration.IsRegistered,
        "occupied new chord cannot save or remove previous hotkey");
    Check(registration.HandleMessage(NativeMethods.WM_HOTKEY, 0x4352) && presses == 2,
        "old binding still dispatches after registration rejection");
    bool oldPresentDuringSave = false;
    Check(!registration.TryApply(next, () =>
    {
        oldPresentDuringSave = !NativeMethods.RegisterHotKey(window, 6001, mods | 0x4000, old.VirtualKey);
        return false;
    }) && oldPresentDuringSave, "failed persistence retains old reservation throughout commit");
    Check(NativeMethods.RegisterHotKey(window, 6001, mods | 0x4000, next.VirtualKey),
        "failed persistence releases only proposed reservation");
    NativeMethods.UnregisterHotKey(window, 6001);
    Check(registration.TryApply(next, () => true) && registration.HandleMessage(NativeMethods.WM_HOTKEY, 0x4452),
        "successful save transfers dispatch to reserved alternate ID");
    Check(!registration.HandleMessage(NativeMethods.WM_HOTKEY, 0x4352), "stale queued hotkey ID is ignored");
    Check(NativeMethods.RegisterHotKey(window, 6001, mods | 0x4000, old.VirtualKey), "successful transfer releases old combination");
    NativeMethods.UnregisterHotKey(window, 6001);
    Check(registration.Apply(next with { Enabled = false }) && !registration.IsRegistered,
        "disabling unregisters active combination");
    Check(registration.Apply(old), "re-enable registers again");
    registration.Dispose();
    Check(NativeMethods.RegisterHotKey(window, 6001, mods | 0x4000, old.VirtualKey), "dispose releases OS reservation");
}
finally
{
    NativeMethods.UnregisterHotKey(window, 6000);
    NativeMethods.UnregisterHotKey(window, 6001);
    NativeMethods.DestroyWindow(window);
}
Console.WriteLine($"OK: {passed} input assertions passed. Physical input and WinUI remain manual checks.");
