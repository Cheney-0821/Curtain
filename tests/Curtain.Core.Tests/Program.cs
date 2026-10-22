using Curtain.Core;
using Curtain.Core.Geometry;
using Curtain.Core.Updates;
using Curtain.Core.Rendering;
using Curtain.Core.Sessions;
using Curtain.Core.Selection;
using Curtain.Core.Settings;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception($"FAIL: {name}");
    Console.WriteLine($"PASS: {name}");
    passed++;
}

var bounds = new PixelRect(-1200, 20, -200, 820);
Check(bounds.Width == 1000 && bounds.Height == 800, "negative-coordinate secondary monitor");
var pin = PinPlacement.Calculate(bounds);
Check(pin.Left >= bounds.Left && pin.Right <= bounds.Right && pin.Top >= bounds.Top && pin.Bottom <= bounds.Bottom,
    "pin inside target frame");
Check(PinPlacement.Calculate(new PixelRect(0, 0, 18, 12)).Width == 18,
    "small target clamps pin width");
Check(!new PixelRect(9, 9, 4, 4).IsValid, "invalid rectangle rejected");
Check(PrivacyProfile.Locked.BlockPointer && !PrivacyProfile.Working.BlockPointer,
    "pointer policy independent of visual opacity");
try
{
    new PrivacyProfile(101, true).Validate();
    throw new Exception("FAIL: invalid opacity was accepted");
}
catch (ArgumentOutOfRangeException) { Check(true, "opacity validation"); }

string hash = new('a', 64);
var release = new ReleaseManifest(1, "0.2.0", "beta", "https://updates.example.com/curtain/0.2.0.exe",
    hash, 100_000, "key-2026", "not-yet-a-real-signature");
Check(release.Validate("updates.example.com").Count == 0, "release metadata format");
var malicious = release with { AssetUrl = "https://evil.example.com/payload.exe" };
Check(malicious.Validate("updates.example.com").Count > 0, "untrusted download host rejected");
var downgrade = release with { AssetUrl = "http://updates.example.com/payload.exe" };
Check(downgrade.Validate("updates.example.com").Count > 0, "non-HTTPS rejected");
var badhash = release with { Sha256 = "bad" };
Check(badhash.Validate("updates.example.com").Count > 0, "malformed hash rejected");
var clearAcrylic = AcrylicToneProfile.FromStrength(45);
var strongerAcrylic = AcrylicToneProfile.FromStrength(88);
var strongestAcrylic = AcrylicToneProfile.FromStrength(98);
Check(clearAcrylic.TintOpacity < strongerAcrylic.TintOpacity &&
      strongerAcrylic.TintOpacity < strongestAcrylic.TintOpacity,
    "acrylic tint increases monotonically with strength");
Check(AcrylicToneProfile.FromStrength(-1) == clearAcrylic &&
      AcrylicToneProfile.FromStrength(999) == strongestAcrylic,
    "acrylic settings clamp outside slider range");
Check(strongestAcrylic.TintOpacity <= 1.0f &&
      clearAcrylic.LuminosityOpacity >= 0.0f &&
      clearAcrylic.LuminosityOpacity <= 1.0f,
    "acrylic controller parameters within unit interval");
var monitor = new PixelRect(0, 0, 1920, 1080);
var work = new PixelRect(0, 0, 1920, 1040);
var ordinary = new PixelRect(400, 200, 1000, 800);
var dock = PinPlacement.Calculate(ordinary, work, monitor, 96, false);
Check(!dock.IsInside && dock.Bounds == new PixelRect(664, 170, 736, 196),
    "handle centered outside ordinary window with four pixel gap");
var atTop = PinPlacement.Calculate(ordinary with { Top = 0 }, work, monitor, 96, false);
Check(atTop.IsInside && atTop.Bounds.Top == 4, "top edge falls back inside work area");
var maximized = PinPlacement.Calculate(new PixelRect(-8, -8, 1928, 1048), work, monitor, 96, true);
Check(maximized.IsInside && maximized.Bounds.Top >= work.Top && maximized.Bounds.Right <= work.Right,
    "maximized handle stays reachable in work area");
var fullScreen = PinPlacement.Calculate(monitor, work, monitor, 96, false);
Check(fullScreen.IsFullScreen && fullScreen.IsInside && fullScreen.Bounds.Top == 4,
    "full screen handle uses inward placement");
var negativeMonitor = new PixelRect(-1920, -100, 0, 980);
var negativeDock = PinPlacement.Calculate(bounds, negativeMonitor, negativeMonitor, 96, false);
Check(!negativeDock.IsInside && negativeDock.Bounds.Left == -736 && negativeDock.Bounds.Top == -10,
    "negative screen coordinates preserve outside center");
var scaled = PinPlacement.Calculate(ordinary, work, monitor, 144, false);
Check(scaled.Bounds.Width == 108 && scaled.Bounds.Height == 39 && scaled.Bounds.Bottom == 194,
    "150 percent dpi scales hit area and gap");
var tiny = PinPlacement.Calculate(new PixelRect(0, 0, 18, 12), work, monitor, 96, false);
Check(tiny.IsInside && tiny.Bounds == new PixelRect(0, 0, 18, 12),
    "tiny target clamps handle dimensions and position");
var moved = PinPlacement.Calculate(new PixelRect(500, 300, 1100, 900), work, monitor, 96, false);
Check(moved.Bounds.Left - dock.Bounds.Left == 100 && moved.Bounds.Top - dock.Bounds.Top == 100,
    "handle follows target translation");
var resized = PinPlacement.Calculate(ordinary with { Right = 1200 }, work, monitor, 96, false);
Check(resized.Bounds.Left == dock.Bounds.Left + 100 && resized.Bounds.Top == dock.Bounds.Top,
    "handle recenters after horizontal resize");
Check(PinPlacement.Calculate(ordinary, work, monitor, 96, false) == dock,
    "restored ordinary position returns handle outside");
var registry = new TargetRegistry();
var idA = new WindowIdentity(101, 7, 9, "TestWindow", 1000);
var a = registry.Attach(idA);
var b = registry.Attach(idA with { Handle = 102 });
var c = registry.Attach(new WindowIdentity(103, 8, 10, "Browser", 2000));
Check(registry.Targets.Count == 3 && registry.Targets.All(t => t.IsClosed), "three independent sessions initially closed");
Check(ReferenceEquals(a, registry.Attach(idA)) && registry.Targets.Count == 3, "duplicate hwnd reuses existing session");
a.SetClosed(false);
Check(!a.IsClosed && b.IsClosed && c.IsClosed, "same process windows have independent curtain state");
b.SetSuspended(true);
Check(b.IsSuspended && !a.IsSuspended && b.IsClosed, "minimize suspends one target without losing closed state");
registry.SetAllClosed(false);
Check(registry.Targets.All(t => !t.IsClosed), "open all changes every registered session");
registry.SetAllClosed(true);
Check(registry.Targets.All(t => t.IsClosed) && b.IsSuspended, "close all preserves suspension state");
Check(registry.Remove(c) && registry.Targets.Count == 2 && a.IsClosed && b.IsClosed, "detach leaves other targets intact");
var replacement = registry.Attach(idA with { ProcessId = 99, ProcessStartTicks = 5000 });
Check(!ReferenceEquals(replacement, a) && registry.Targets.Count == 2, "hwnd reused by different process replaces stale identity");
Check(!registry.Remove(a) && registry.Targets.Contains(replacement), "late stale cleanup cannot remove new session");
var changedClass = registry.Attach(replacement.Identity with { ClassName = "NewWindow" });
Check(!ReferenceEquals(changedClass, replacement), "class change invalidates same hwnd identity");
var restarted = registry.Attach(changedClass.Identity with { ProcessStartTicks = 6000 });
Check(!ReferenceEquals(restarted, changedClass), "recycled pid with new process start invalidates identity");
Check(registry.Remove(restarted) && registry.Remove(b) && registry.Targets.Count == 0, "all resources can be removed from model");

var gesture = new DragGesture(new PixelPoint(30, 40), new PixelRect(-500, 100, 100, 700), 2, 3);
Check(gesture.Move(new PixelPoint(32, 43)) is null && !gesture.IsDragging, "press jitter within system thresholds is a click");
Check(gesture.Move(new PixelPoint(33, 40)) == new PixelRect(-497, 100, 103, 700), "horizontal threshold starts physical coordinate drag");
Check(gesture.Move(new PixelPoint(-20, -10)) == new PixelRect(-550, 50, 50, 650), "drag uses original rect and absolute screen delta");
gesture.End();
Check(!gesture.IsActive && !gesture.IsDragging && gesture.Move(new PixelPoint(500, 500)) is null, "release or lost capture ends further movement");
var vertical = new DragGesture(new PixelPoint(0, 0), ordinary, 4, 4);
Check(vertical.Move(new PixelPoint(0, 5)) == new PixelRect(400, 205, 1000, 805), "vertical threshold starts drag independently");
var reachable = DragGesture.KeepReachable(new PixelRect(-2500, -500, -1900, 100), negativeMonitor, 64, 32);
Check(reachable.Left == -2456 && reachable.Top == -100 && reachable.Width == 600, "offscreen drag retains reachable strip on negative monitor");
var bottomDrag = DragGesture.KeepReachable(new PixelRect(2200, 1200, 2800, 1800), work, 64, 32);
Check(bottomDrag.Left == 1856 && bottomDrag.Top == 1008, "drag clamps right and bottom reachability");
Check(DragGesture.CanMove(ordinary, work, monitor, false, 96), "ordinary window can drag");
Check(!DragGesture.CanMove(monitor, work, monitor, false, 96) &&
      !DragGesture.CanMove(ordinary, work, monitor, true, 96), "maximized and full screen do not partially move");
Check(!DragGesture.CanMove(new PixelRect(0, 0, 960, 1040), work, monitor, false, 96), "half screen snap is conservatively refused");
Check(SelectionOutline.StrokePixels(96) == 2 && SelectionOutline.StrokePixels(120) == 3 &&
      SelectionOutline.StrokePixels(144) == 3, "outline thickness scales at 100 125 150 percent dpi");
Check(SelectionOutline.StrokePixels(0) == 2, "outline unknown dpi falls back to 96");
Check(SelectionOutline.IsBorderPixel(0, 20, 100, 100, 2) &&
      SelectionOutline.IsBorderPixel(99, 20, 100, 100, 2) &&
      !SelectionOutline.IsBorderPixel(50, 50, 100, 100, 2), "outline is only border with transparent interior");
Check(!SelectionOutline.IsBorderPixel(-1, 0, 100, 100, 2), "outline excludes out of bounds pixels");

var gate = new SelectionInputGate();
Check(gate.Handle(0x201, true) == new SelectionMouseResult(true, SelectionOutcome.None), "selection consumes left down without binding early");
Check(gate.Handle(0x200, true).Suppress == false && gate.HasHeldButton, "selection passes movement and retains down state");
Check(gate.Handle(0x202, true) == new SelectionMouseResult(true, SelectionOutcome.Confirm) && !gate.HasHeldButton,
    "selection consumes matching left up and confirms once");
Check(!gate.Handle(0x202, true).Suppress, "selection does not consume unmatched button release");
Check(gate.Handle(0x204, true).Suppress && gate.Handle(0x205, true).Outcome == SelectionOutcome.Cancel,
    "right down and up are swallowed before cancel");
gate.Handle(0x201, true);
Check(gate.Handle(0x202, false) == new SelectionMouseResult(true, SelectionOutcome.None), "cancel drains held left release without confirmation");
Check(!gate.Handle(0x201, false).Suppress && !gate.Handle(0x20A, true).Suppress,
    "inactive selector and unrelated wheel are not intercepted");
gate.Handle(0x201, true); gate.Handle(0x204, true);
Check(gate.Handle(0x202, true).Outcome == SelectionOutcome.None && gate.HasHeldButton,
    "right button held prevents accidental left confirmation");
Check(gate.Handle(0x205, true).Outcome == SelectionOutcome.Cancel && !gate.HasHeldButton,
    "mixed button cancellation drains both buttons");
var threadChanged = registry.Attach(idA with { ThreadId = 100 });
var threadReused = registry.Attach(threadChanged.Identity with { ThreadId = 101 });
Check(!ReferenceEquals(threadChanged, threadReused) && !registry.Remove(threadChanged),
    "window thread identity change protects new session from stale cleanup");
var rawFrame = new PixelRect(-508, 92, 108, 708);
var visibleFrame = new PixelRect(-500, 100, 100, 700);
var translatedFrame = DragGesture.TranslateVisibleBounds(rawFrame, visibleFrame, new PixelRect(-608, 292, 8, 908));
Check(translatedFrame == new PixelRect(-600, 300, 0, 900),
    "batched drag preserves raw to DWM frame offsets at negative coordinates");
Check(translatedFrame.Width == visibleFrame.Width && translatedFrame.Height == visibleFrame.Height,
    "drag translation keeps visible size rather than copying invisible frame margins");
Check(DragGesture.TranslateVisibleBounds(rawFrame, visibleFrame, rawFrame) == visibleFrame,
    "zero drag delta leaves DWM visible geometry unchanged");

string settingsDirectory = Path.Combine(Path.GetTempPath(), $"Curtain-settings-test-{Guid.NewGuid():N}");
try
{
    var settingsStore = new CurtainUserSettingsStore(settingsDirectory);
    var defaults = settingsStore.Load();
    Check(!defaults.RecoveredFromInvalidFile && !defaults.Settings.StartWithWindows && defaults.Settings.Hotkey.Enabled &&
          defaults.Settings.Hotkey.VirtualKey == 0x78 && defaults.Settings.AutoDownloadUpdates,
        "first-run preferences default to disabled startup, Ctrl+Shift+F9, and automatic update downloads");
    Check(CurtainUserSettingsStore.IsValidHotkey(defaults.Settings.Hotkey), "default global hotkey is valid");
    Check(!CurtainUserSettingsStore.IsValidHotkey(new GlobalHotkeySettings(true, 0, 0x78)),
        "global hotkey requires a modifier and a supported key");
    Check(!CurtainUserSettingsStore.IsValidHotkey(new GlobalHotkeySettings(true, HotkeyModifier.Windows, 0x78)),
        "global hotkey rejects the Windows key modifier");
    var settings = defaults.Settings with
    {
        StartWithWindows = true,
        AutoDownloadUpdates = false,
        Hotkey = new GlobalHotkeySettings(true, HotkeyModifier.Control | HotkeyModifier.Shift, 0x77)
    };
    settingsStore.Save(settings);
    var saved = settingsStore.Load();
    string json = File.ReadAllText(Path.Combine(settingsDirectory, "settings.json"));
    Check(saved.Settings.StartWithWindows && !saved.Settings.AutoDownloadUpdates && saved.Settings.Hotkey.VirtualKey == 0x77,
        "user preferences, including the automatic update toggle, survive reload");
    Check(!json.Contains("Private Canary Chat Title", StringComparison.Ordinal) &&
          !json.Contains("WindowHandle", StringComparison.OrdinalIgnoreCase) &&
          !json.Contains("ProcessId", StringComparison.OrdinalIgnoreCase) &&
          !json.Contains("SavedTargets", StringComparison.Ordinal) &&
          !json.Contains("WindowClass", StringComparison.Ordinal) &&
          !json.Contains("MinimizeToTray", StringComparison.Ordinal),
        "settings contain no window identity or tray behavior fields");
    File.WriteAllText(Path.Combine(settingsDirectory, "settings.json"),
        "{\"SchemaVersion\":1,\"StartWithWindows\":false,\"Hotkey\":{\"Enabled\":true,\"Modifiers\":6,\"VirtualKey\":120}}");
    var legacySettings = settingsStore.Load();
    Check(!legacySettings.RecoveredFromInvalidFile && legacySettings.Settings.AutoDownloadUpdates,
        "legacy settings without the update preference default to enabled");
    File.WriteAllText(Path.Combine(settingsDirectory, "settings.json"), "{ invalid json");
    var recovered = settingsStore.Load();
    Check(recovered.RecoveredFromInvalidFile && !recovered.Settings.StartWithWindows,
        "invalid settings recover to safe defaults without restoring stale windows");
}
finally
{
    if (Directory.Exists(settingsDirectory)) Directory.Delete(settingsDirectory, true);
}


// B3.8: keep the old settings schema compatible and validate idle options.
var autoDefaults = new CurtainUserSettingsStore(Path.Combine(Path.GetTempPath(), "curtain-b38-default-" + Guid.NewGuid().ToString("N"))).Load().Settings.AutoProtection;
Check(!autoDefaults.Enabled && autoDefaults.IdleMinutes == 3 && autoDefaults.Scope == AutoProtectionScope.AddedWindows,
    "idle protection opt-in defaults");
var b38Directory = Path.Combine(Path.GetTempPath(), "curtain-b38-settings-" + Guid.NewGuid().ToString("N"));
try
{
    var b38Store = new CurtainUserSettingsStore(b38Directory);
    var custom = new CurtainUserSettings { AutoProtection = new AutoProtectionSettings(true, 5, AutoProtectionScope.AllDisplays) };
    b38Store.Save(custom);
    Check(b38Store.Load().Settings.AutoProtection == custom.AutoProtection, "idle protection preferences roundtrip");
    try { b38Store.Save(custom with { AutoProtection = new AutoProtectionSettings(true, -1, AutoProtectionScope.AllDisplays) });
        throw new Exception("FAIL: invalid idle minutes"); }
    catch (ArgumentException) { Check(true, "invalid idle period rejected"); }
}
finally { if (Directory.Exists(b38Directory)) Directory.Delete(b38Directory, true); }

Console.WriteLine($"OK: {passed} assertions passed.");
