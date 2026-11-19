using System.ComponentModel;
using System.Runtime.InteropServices;
using Curtain.App.Interop;
using Curtain.App.Services;
using Curtain.Core.Geometry;

namespace Curtain.App.Windows;

internal sealed class RegionEditorFeedback : IDisposable
{
    private const uint AddTool = 0x0432;
    private const uint TrackActivate = 0x0411;
    private const uint TrackPosition = 0x0412;
    private SelectionSpotlightWindow? _spotlight;
    private SelectionOutlineWindow? _outline;
    private nint _tooltip;
    private ToolInfo _tool;
    private (PixelRect Bounds, int Dpi)? _target;
    private PixelPoint? _lastPointer;
    private bool _hintVisible;

    internal RegionEditorFeedback(nint owner, string hint)
    {
        try
        {
            _spotlight = new SelectionSpotlightWindow();
            _outline = new SelectionOutlineWindow();
        }
        catch
        {
            Dispose();
            throw;
        }

        try
        {
            InitCommonControls();
            _tooltip = HandleNative.CreateWindowExW(0x080000A8, "tooltips_class32", "", 0x80000003,
                0, 0, 1, 1, owner, 0, HandleNative.GetModuleHandleW(null), 0);
            if (_tooltip == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            _tool = new ToolInfo
            {
                Size = (uint)Marshal.SizeOf<ToolInfo>(), Flags = 0x01 | 0x20 | 0x80 | 0x100,
                Window = owner, Id = owner, Text = Marshal.StringToHGlobalUni(hint)
            };
            if (SendToolMessage(_tooltip, AddTool, 0, ref _tool) == 0)
            {
                // ComCtl32 v5 accepts TOOLINFO through lParam, without lpReserved.
                _tool.Size = (uint)Marshal.OffsetOf<ToolInfo>(nameof(ToolInfo.Reserved)).ToInt64();
                if (SendToolMessage(_tooltip, AddTool, 0, ref _tool) == 0)
                    throw new InvalidOperationException("Unable to register the drawing tooltip.");
            }
        }
        catch (Exception ex)
        {
            CurtainDiagnostics.WriteException("overlay.region-feedback.failed", ex, stage: "cursor-hint");
            DisposeTooltip();
        }
    }

    internal void UpdateTarget(PixelRect bounds, int dpi)
    {
        if (_target == (bounds, dpi)) return;
        _spotlight?.Update(bounds, dpi);
        _outline?.Show(bounds, dpi);
        _target = (bounds, dpi);
        if (_hintVisible) UpdateHint();
    }

    internal void UpdateHint()
    {
        if (_tooltip == 0) return;
        if (_target is not { } target || !NativeMethods.GetCursorPos(out var point) ||
            point.X < target.Bounds.Left || point.X >= target.Bounds.Right ||
            point.Y < target.Bounds.Top || point.Y >= target.Bounds.Bottom)
        {
            HideHint();
            return;
        }
        var pointer = new PixelPoint(point.X, point.Y);
        if (_hintVisible && _lastPointer == pointer) return;
        _lastPointer = pointer;
        int offset = (int)Math.Ceiling(16d * target.Dpi / 96);
        int x = point.X + offset, y = point.Y + offset;
        var workArea = NativeMethods.GetWorkAreaAt(pointer, target.Bounds);
        if (x + 150 * target.Dpi / 96 > workArea.Right) x = point.X - 150 * target.Dpi / 96;
        if (y + 32 * target.Dpi / 96 > workArea.Bottom) y = point.Y - 32 * target.Dpi / 96;
        SendMessageW(_tooltip, TrackPosition, 0, (nint)((y << 16) | (x & 0xffff)));
        if (_hintVisible) return;
        SendToolMessage(_tooltip, TrackActivate, 1, ref _tool);
        _hintVisible = true;
    }

    internal void HideHint()
    {
        if (!_hintVisible) return;
        SendToolMessage(_tooltip, TrackActivate, 0, ref _tool);
        _hintVisible = false;
    }

    public void Dispose()
    {
        DisposeTooltip();
        _outline?.Dispose();
        _outline = null;
        _spotlight?.Dispose();
        _spotlight = null;
    }

    private void DisposeTooltip()
    {
        HideHint();
        if (_tooltip != 0) HandleNative.DestroyWindow(_tooltip);
        _tooltip = 0;
        if (_tool.Text != 0) Marshal.FreeHGlobal(_tool.Text);
        _tool.Text = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ToolInfo
    {
        public uint Size, Flags;
        public nint Window, Id;
        public NativeMethods.Rect Bounds;
        public nint Instance, Text, Parameter, Reserved;
    }

    [DllImport("comctl32.dll")]
    private static extern void InitCommonControls();
    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendToolMessage(nint hwnd, uint message, nint wParam, ref ToolInfo tool);
}
