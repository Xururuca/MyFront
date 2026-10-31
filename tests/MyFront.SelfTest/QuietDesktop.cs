using System.Runtime.InteropServices;

namespace MyFront.SelfTest;

/// <summary>
/// Keeps the UI tests out of the way of whoever works at the PC: the test windows never take the keyboard focus (a CBT
/// hook on the test thread refuses to activate them), they open on a monitor other than the one with the window in
/// front when the run starts, screenshots raise them without activating, and the clipboard text is put back afterwards.
/// Set MYFRONT_UITEST_INTERACTIVE=1 for the old behaviour (activation, real-mouse tests).
/// </summary>
internal sealed class QuietDesktop : IDisposable
{
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("MYFRONT_UITEST_INTERACTIVE") != "1";

    /// <summary>Working area of the monitor the tests use.</summary>
    public Rectangle Area { get; }
    private readonly Rectangle _userArea;
    private readonly HookProc _proc;
    private readonly IntPtr _hook;
    private readonly string? _clipboard;

    private QuietDesktop()
    {
        var front = GetForegroundWindow();
        var userScreen = front != IntPtr.Zero ? Screen.FromHandle(front) : Screen.PrimaryScreen!;
        var testScreen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName != userScreen.DeviceName) ?? userScreen;
        _userArea = userScreen.Bounds;
        Area = testScreen.WorkingArea;
        try { _clipboard = Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch (ExternalException) { }
        _proc = CbtProc;
        _hook = SetWindowsHookEx(WH_CBT, _proc, IntPtr.Zero, GetCurrentThreadId());
        Console.WriteLine($"      [quiet] test windows on {testScreen.DeviceName} {Area}, never activated" +
                          (testScreen == userScreen ? " (one monitor: same screen)" : $"; you work on {userScreen.DeviceName}"));
    }

    /// <summary>Call on the UI thread that creates the test windows.</summary>
    public static QuietDesktop? Start() => Enabled ? new QuietDesktop() : null;

    public void Place(Form f)
    {
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(Area.X + 20, Area.Y + 20);
    }

    private IntPtr CbtProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HCBT_ACTIVATE)
        {
            MoveOffUserScreen(wParam);
            return (IntPtr)1; // refuse: the focus stays where the user is
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>A dialog centred on something on the user's monitor is moved over to the test monitor.</summary>
    private void MoveOffUserScreen(IntPtr hwnd)
    {
        if (_userArea == Area || GetAncestor(hwnd, GA_ROOT) != hwnd || !GetWindowRect(hwnd, out var r)) return;
        var center = new Point((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        if (!_userArea.Contains(center)) return;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        int x = Area.X + Math.Max(0, (Area.Width - w) / 2), y = Area.Y + Math.Max(0, (Area.Height - h) / 2);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>On top for a screenshot, without taking the focus.</summary>
    public static void Raise(IntPtr hwnd, bool topmost) =>
        SetWindowPos(hwnd, topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        try { if (_clipboard is not null) Clipboard.SetText(_clipboard); } catch (ExternalException) { }
    }

    private const int WH_CBT = 5, HCBT_ACTIVATE = 5, GA_ROOT = 2;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    private static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
