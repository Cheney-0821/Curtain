namespace Curtain.App.Interop;

internal readonly record struct WindowSelectionResult(
    bool IsSelectable,
    string ReasonCode,
    int Win32Error = 0,
    int HResult = 0)
{
    internal static WindowSelectionResult Accept() => new(true, "Accepted");
    internal static WindowSelectionResult Reject(string reason, int win32Error = 0, int hresult = 0) =>
        new(false, reason, win32Error, hresult);
}
