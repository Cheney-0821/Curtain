using Curtain.Core.Protection;

internal static class AutoBindWindowPolicyTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception($"FAIL: {name}");
            Console.WriteLine($"PASS: {name}");
            passed++;
        }

        string? Reject(string cls, long style, long exStyle = 0, bool owned = false)
            => AutoBindWindowPolicy.GetRejectionReason(cls, style, exStyle, owned);

        Check(Reject("CabinetWClass", 0x14CF0000, 0x100) is null, "Explorer main window retained");
        Check(Reject("CabinetWClass", 0x34CF0000, 0x100) is null, "minimized style does not become transient");
        Check(Reject("Microsoft.UI.Content.PopupWindowSiteBridge", 0x94000000, 0x08200000, true) ==
            "TransientPopupClass", "sampled Explorer popup bridge rejected");
        Check(Reject("Microsoft.UI.Content.PopupWindowSiteBridge", 0x94000000) != null,
            "popup bridge rejected while owner is not yet assigned");
        Check(Reject("#32768", 0x94000000) != null, "native menu rejected");
        Check(Reject("Auto-Suggest Dropdown", 0x94000000) != null, "address suggestions rejected");
        Check(Reject("Xaml_WindowedPopupClass", 0x94000000) != null, "XAML popup rejected");
        Check(Reject("CustomPopup", 0x94000000, 0, true) == "OwnedTransientPopup",
            "owned frameless popup rejected across applications");
        Check(Reject("CustomPopup", 0x94800000, 0, true) == "OwnedTransientPopup",
            "thin border alone does not make an owned popup independent");
        Check(Reject("#32770", 0x94C80000, 0, true) is null, "owned normal dialog retained");
        Check(Reject("CustomDialog", 0x94040000, 0, true) is null, "owned resizable window retained");
        Check(Reject("CustomWindow", 0x94000000, 0x40000, true) is null,
            "explicit owned app window retained");
        Check(Reject("Chrome_WidgetWin_1", 0x94000000) is null, "unowned borderless application retained");
        Check(Reject("CabinetWClass", 0x14CF0000, 0x100) is null,
            "valid window after reused popup handle has no cached rejection");
        return passed;
    }
}
