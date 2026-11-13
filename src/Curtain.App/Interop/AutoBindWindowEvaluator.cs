using Curtain.Core.Protection;

namespace Curtain.App.Interop;

internal static class AutoBindWindowEvaluator
{
    internal static WindowSelectionResult Evaluate(nint hwnd)
    {
        WindowSelectionResult selectable = WindowSelectionEvaluator.Evaluate(hwnd);
        if (!selectable.IsSelectable) return selectable;

        string? reason = AutoBindWindowPolicy.GetRejectionReason(
            NativeMethods.ClassOf(hwnd),
            NativeMethods.GetWindowLongPtr(hwnd, -16).ToInt64(),
            NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64(),
            NativeMethods.GetWindow(hwnd, 4) != nint.Zero);

        return reason is null ? selectable : WindowSelectionResult.Reject(reason);
    }
}
