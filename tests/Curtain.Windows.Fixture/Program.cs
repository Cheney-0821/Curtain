using System.Runtime.InteropServices;
using System.Text.Json;

Fixture.Run(args);

internal static class Fixture
{
    private delegate nint Procedure(nint hwnd, uint message, nint wParam, nint lParam);
    private static readonly Procedure Callback = OnMessage;
    private sealed class Sample(string name) { public string Name = name; public int Clicks; public nint Button; }
    private static readonly Dictionary<nint, Sample> Windows = new();
    private const int ReservedHotkeyId = 0xB2;
    private static bool _reserveHotkey;
    private static bool _hotkeyRegistered;
    private static nint _reservationWindow;
    private static readonly string Metrics = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CurtainB1Fixture", $"fixture-{Environment.ProcessId}.json");
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    { public uint Size, Style; public nint Proc; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? Menu, Name; public nint SmallIcon; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Message
    { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Point; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] private struct Paint
    { public nint DC; public int Erase; public Rect Rect; public int Restore, Update; public uint R0, R1, R2, R3, R4, R5, R6, R7; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern nint LoadCursorW(nint instance, nint name);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref Message message);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern nint BeginPaint(nint window, out Paint paint);
    [DllImport("user32.dll")] private static extern bool EndPaint(nint window, ref Paint paint);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref Rect rect, nint brush);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool TextOutW(nint dc, int x, int y, string text, int length);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(nint window, string text);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);

    internal static void Run(string[] args)
    {
        _reserveHotkey = args.Any(argument => string.Equals(argument, "--reserve-default-hotkey", StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(Path.GetDirectoryName(Metrics)!);
        var cls = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Style = 3,
            Name = "Curtain.B1.TestTarget", Proc = Marshal.GetFunctionPointerForDelegate(Callback),
            Instance = GetModuleHandleW(null), Cursor = LoadCursorW(0, (nint)32512), Background = 6 };
        if (RegisterClassExW(ref cls) == 0) return;
        Add("A", 830, 90); Add("B", 1350, 220);
        if (_reserveHotkey && Windows.Count > 0)
        {
            _reservationWindow = Windows.Keys.First();
            _hotkeyRegistered = RegisterHotKey(_reservationWindow, ReservedHotkeyId, 0x0002 | 0x0004 | 0x4000, 0x78);
            if (_hotkeyRegistered) SetWindowTextW(_reservationWindow, "Curtain B2 测试 · 已占用 Ctrl+Shift+F9");
            else SetWindowTextW(_reservationWindow, $"Curtain B2 测试 · 注册快捷键失败 {Marshal.GetLastPInvokeError()}");
        }
        Save();
        while (GetMessageW(out var message, 0, 0, 0) > 0)
        { TranslateMessage(ref message); DispatchMessageW(ref message); }
    }
    private static void Add(string name, int x, int y)
    {
        nint hwnd = CreateWindowExW(0, "Curtain.B1.TestTarget", $"Curtain B1 sample {name}", 0x00CF0000,
            x, y, 500, 460, 0, 0, GetModuleHandleW(null), 0);
        if (hwnd == 0) return;
        var sample = new Sample(name);
        Windows.Add(hwnd, sample);
        sample.Button = CreateWindowExW(0, "BUTTON", "测试按钮 · 点击次数 0", 0x50000000,
            20, 18, 220, 36, hwnd, (nint)501, GetModuleHandleW(null), 0);
        ShowWindow(hwnd, 5);
    }
    private static nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (Windows.TryGetValue(hwnd, out var sample))
        {
            if (message == 0x0312 && wParam.ToInt32() == ReservedHotkeyId) return 0;
            if (message == 0x111 && (wParam.ToInt64() & 0xFFFF) == 501)
            { sample.Clicks++; SetWindowTextW(sample.Button, $"测试按钮 · 点击次数 {sample.Clicks}"); Save(); return 0; }
            if (message == 0xF)
            {
                nint dc = BeginPaint(hwnd, out var paint);
                try
                {
                    GetClientRect(hwnd, out var area);
                    area.Top = 75; area.Right /= 2;
                    nint left = CreateSolidBrush(0xAACD83), right = CreateSolidBrush(0xA0B7E8);
                    FillRect(dc, ref area, left);
                    area.Left = area.Right; area.Right *= 2; FillRect(dc, ref area, right);
                    DeleteObject(left); DeleteObject(right); SetBkMode(dc, 1);
                    for (int line = 0; line < 10; line++)
                    {
                        string text = $"B1 sample {sample.Name} 123456  MMMM 0000 XXXXX";
                        TextOutW(dc, 15, 90 + line * 25, text, text.Length);
                    }
                }
                finally { EndPaint(hwnd, ref paint); }
                return 0;
            }
            if (message == 0x2E0)
            {
                var rect = Marshal.PtrToStructure<Rect>(lParam);
                SetWindowPos(hwnd, 0, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, 0x14);
                return 0;
            }
            if (message == 0x10) { DestroyWindow(hwnd); return 0; }
            if (message == 2)
            {
                if (_hotkeyRegistered && hwnd == _reservationWindow) UnregisterHotKey(hwnd, ReservedHotkeyId);
                Windows.Remove(hwnd); Save(); if (Windows.Count == 0) PostQuitMessage(0); return 0;
            }
            if (message is 3 or 5) Save();
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }
    private static void Save()
    {
        var values = Windows.Select(pair =>
        {
            GetWindowRect(pair.Key, out var bounds);
            return new { sample = pair.Value.Name, hwnd = pair.Key.ToInt64(), clicks = pair.Value.Clicks,
                dpi = GetDpiForWindow(pair.Key), bounds = new { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom } };
        }).ToArray();
        File.WriteAllText(Metrics, JsonSerializer.Serialize(new { pid = Environment.ProcessId, windows = values }));
    }
}
