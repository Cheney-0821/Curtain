using Curtain.Core.Geometry;
using Curtain.Core.Protection;
using Curtain.Core.Selection;
using Curtain.Core.Settings;

internal static class FullscreenInteractionTests
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL: " + name);
            Console.WriteLine("PASS: " + name); count++;
        }
        var state = new FullscreenControlState();
        var initial = new PixelPoint(100, 0);
        state.Reset(initial);
        Check(state.VisibleMonitor == 0 && state.Update(10, initial, 1, true, false, false) == 0,
            "entry stays hidden until actual pointer motion");
        Check(state.Update(20, new(200, 600), 1, false, false, false) == 0,
            "movement below upper quarter does not reveal control");
        Check(state.Update(50, new(201, 200), 1, true, false, false) == 1,
            "upper-quarter motion reveals control without reaching the button");
        Check(state.Update(1049, new(201, 200), 1, true, false, false) == 1 &&
              state.Update(1050, new(201, 200), 1, true, false, false) == 0,
            "stationary cursor in reveal area hides at exactly 1000 ms");
        Check(state.Update(1100, new(202, 200), 1, true, false, false) == 1,
            "motion after stationary hide reveals control again");
        Check(state.Update(1150, new(250, 600), 1, false, false, false) == 1 &&
              state.Update(2149, new(251, 600), 1, false, false, false) == 1 &&
              state.Update(2150, new(252, 600), 1, false, false, false) == 0,
            "leaving hides after 1000 ms even while pointer keeps moving below region");
        Check(state.Update(2200, new(253, 200), 1, true, false, false) == 1 &&
              state.Update(3199, new(254, 200), 1, true, false, false) == 1 &&
              state.Update(4198, new(254, 200), 1, true, false, false) == 1 &&
              state.Update(4199, new(254, 200), 1, true, false, false) == 0,
            "movement in reveal area restarts the stationary deadline");
        Check(state.Update(4300, new(50, 40), 1, true, true, false) == 1 &&
              state.Update(5300, new(50, 40), 1, true, true, false) == 0,
            "stationary button hover also hides after 1000 ms");
        Check(state.Update(5500, new(51, 40), 1, true, true, true) == 1 &&
              state.Update(9000, new(51, 40), 1, true, true, true) == 1,
            "holding restore button cannot be interrupted by idle hide");
        Check(state.Update(9999, new(51, 40), 1, true, true, false) == 1 &&
              state.Update(10000, new(51, 40), 1, true, true, false) == 0,
            "release resumes stationary deadline after held interaction");
        Check(state.Update(10500, new(52, 40), 1, true, false, false) == 1 &&
              state.Update(10550, new(-500, -100), 2, true, false, false) == 2,
            "moving to another display's upper quarter replaces the previous control");
        state.Reset(initial);
        Check(state.VisibleMonitor == 0, "re-entry resets previous display reveal");
        var rearm = new IdleProtectionRearm();
        Check(rearm.AllowsActivation(0, 1), "initial idle activation follows Windows input time");
        rearm.Resume(2000);
        Check(!rearm.AllowsActivation(2000, 1) && !rearm.AllowsActivation(61999, 1) && rearm.AllowsActivation(62000, 1),
            "explicit resume prevents immediate idle re-entry until a complete new period");
        Check(!rearm.AllowsActivation(62000, 3) && rearm.AllowsActivation(182000, 3),
            "changed idle setting applies to restore cooldown");
        var display = new PixelRect(-1920, -200, 0, 880);
        Check(FullscreenControlState.IsRevealArea(display, new(-1900, 69)) &&
              !FullscreenControlState.IsRevealArea(display, new(-1900, 70)),
            "1080 pixel display has exact 270 pixel upper-quarter boundary at negative origin");
        Check(!FullscreenControlState.IsRevealArea(display, new(0, -200)) &&
              !FullscreenControlState.IsRevealArea(display, new(-1900, -201)),
            "reveal area excludes adjacent displays and points above monitor");
        var scaledDisplay = new PixelRect(-2880, -300, 0, 1320);
        Check(FullscreenControlState.IsRevealArea(scaledDisplay, new(-2800, 104)) &&
              !FullscreenControlState.IsRevealArea(scaledDisplay, new(-2800, 105)),
            "physical display scaling preserves the upper-quarter proportion");
        var gate = new SideButtonInputGate();
        uint back = 1u << 16, forward = 2u << 16;
        Check(gate.Handle(0x20B, back, PrivacyMouseButton.Off) == default &&
              gate.Handle(0x20C, back, PrivacyMouseButton.Off) == default &&
              gate.Handle(0x20B, forward, PrivacyMouseButton.Off) == default,
            "default Off passes both side buttons");
        Check(gate.Handle(0x20B, forward, PrivacyMouseButton.Back) == default,
            "unselected side button passes through");
        Check(gate.Handle(0x20B, back, PrivacyMouseButton.Back) == new SideButtonResult(true, false) &&
              gate.Handle(0x20B, back, PrivacyMouseButton.Back) == new SideButtonResult(true, false) &&
              gate.Handle(0x20C, back, PrivacyMouseButton.Back) == new SideButtonResult(true, true) &&
              gate.Handle(0x20C, back, PrivacyMouseButton.Back) == default,
            "selected down repeats and up duplicates toggle once only");
        gate.Handle(0x20B, back, PrivacyMouseButton.Back);
        Check(gate.Handle(0x20C, back, PrivacyMouseButton.Off, false) == new SideButtonResult(true, false) && !gate.HasHeldButton,
            "disable drains consumed release without triggering toggle");
        Check(gate.Handle(0x20C, forward, PrivacyMouseButton.Forward) == default,
            "orphan side-button release cannot toggle");
        gate.Handle(0x20B, forward, PrivacyMouseButton.Forward);
        Check(gate.Handle(0x20C, forward, PrivacyMouseButton.Forward).Toggle,
            "Forward maps to same release toggle");
        var duplicateGate = new SideButtonInputGate();
        duplicateGate.Handle(0x20B, forward, PrivacyMouseButton.Forward, timestampMilliseconds: 1000);
        Check(duplicateGate.Handle(0x20C, forward, PrivacyMouseButton.Forward, timestampMilliseconds: 1020).Toggle,
            "first complete Forward click toggles immediately");
        duplicateGate.Handle(0x20B, forward, PrivacyMouseButton.Forward, timestampMilliseconds: 1060);
        Check(duplicateGate.Handle(0x20C, forward, PrivacyMouseButton.Forward, timestampMilliseconds: 1100) == new SideButtonResult(true, false),
            "duplicate complete side-key pulse within 120 ms is consumed without reversing protection");
        duplicateGate.Handle(0x20B, forward, PrivacyMouseButton.Forward, timestampMilliseconds: 1210);
        Check(duplicateGate.Handle(0x20C, forward, PrivacyMouseButton.Forward, timestampMilliseconds: 1220).Toggle,
            "next click at the 120 ms boundary remains responsive");
        var wrapGate = new SideButtonInputGate();
        wrapGate.Handle(0x20B, back, PrivacyMouseButton.Back, timestampMilliseconds: uint.MaxValue - 25);
        wrapGate.Handle(0x20C, back, PrivacyMouseButton.Back, timestampMilliseconds: uint.MaxValue - 20);
        wrapGate.Handle(0x20B, back, PrivacyMouseButton.Back, timestampMilliseconds: 5);
        Check(!wrapGate.Handle(0x20C, back, PrivacyMouseButton.Back, timestampMilliseconds: 10).Toggle,
            "duplicate filtering survives Windows input timestamp wraparound");
        var picker = new SelectionInputGate();
        Check(!picker.Handle(0x20B, true).Suppress && picker.Handle(0x20C, true).Outcome == SelectionOutcome.None &&
              gate.Handle(0x201, back, PrivacyMouseButton.Back) == default,
            "picker and privacy side-key policies do not consume each other's messages");
        string dir = Path.Combine(Path.GetTempPath(), "Curtain-Fullscreen-Test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CurtainUserSettingsStore(dir);
            Check(store.Load().Settings.PrivacyMouseButton == PrivacyMouseButton.Off, "missing settings default side key Off");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"PrivacyMouseButton\":99}");
            var loaded = store.Load();
            Check(!loaded.RecoveredFromInvalidFile && loaded.Settings.PrivacyMouseButton == PrivacyMouseButton.Off,
                "unknown new enum normalizes without discarding old preferences");
            var saved = loaded.Settings with { PrivacyMouseButton = PrivacyMouseButton.Forward,
                PrivacyHotkey = new(true, HotkeyModifier.Alt | HotkeyModifier.Shift, 0x42) };
            store.Save(saved);
            Check(store.Load().Settings == saved, "side key and custom privacy shortcut persist together");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        return count;
    }
}
