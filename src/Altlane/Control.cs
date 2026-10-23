using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Threading;

namespace Altlane;

/// <summary>
/// A message-only window, found by its title. Other Altlane processes (the installer's --quit, a second
/// launch) post it registered messages; an elevated instance lets those through UIPI. It also receives
/// raw keyboard input for the hook watchdog.
/// </summary>
sealed class ControlWindow : NativeWindow, IDisposable
{
    public const string Title = "Altlane.Control", TestTitle = "Altlane.Control.Test";
    public static readonly uint MsgQuit = N.RegisterWindowMessage("Altlane.Quit");
    public static readonly uint MsgSettings = N.RegisterWindowMessage("Altlane.ShowSettings");
    /// <summary>Test mode only: remove the keyboard hook the way Windows would, so the self-test can watch the watchdog.</summary>
    public static readonly uint MsgDropHook = N.RegisterWindowMessage("Altlane.Test.DropHook");
    /// <summary>Test mode only: wParam 1 switches Alt+Tab to the columns layout, 0 back to the grid. Not saved.</summary>
    public static readonly uint MsgLayout = N.RegisterWindowMessage("Altlane.Test.Layout");
    /// <summary>Test mode only: wParam 1 sends Win+Tab to the lanes overview, 0 leaves it to Task View. Not saved.</summary>
    public static readonly uint MsgWinTab = N.RegisterWindowMessage("Altlane.Test.WinTab");

    public event Action? QuitRequested, SettingsRequested, DropHookRequested;
    public event Action<bool>? LayoutRequested, WinTabRequested;
    readonly bool _test;
    /// <summary>Event time (GetTickCount clock) of the last raw keyboard input, and how many arrived.</summary>
    public int LastRaw { get; private set; }
    public long RawCount { get; private set; }
    /// <summary>The window in front when the last raw key arrived: the watchdog must not judge keys typed into an admin window.</summary>
    public IntPtr LastRawFg { get; private set; }

    [DllImport("user32")] static extern int GetMessageTime();

    public ControlWindow(bool test)
    {
        _test = test;
        CreateHandle(new CreateParams { Caption = test ? TestTitle : Title, Parent = N.HWND_MESSAGE });
        N.ChangeWindowMessageFilterEx(Handle, MsgQuit, N.MSGFLT_ALLOW, IntPtr.Zero);
        N.ChangeWindowMessageFilterEx(Handle, MsgSettings, N.MSGFLT_ALLOW, IntPtr.Zero);
        var dev = new[] { new N.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 6, dwFlags = N.RIDEV_INPUTSINK, hwndTarget = Handle } };
        bool ok = N.RegisterRawInputDevices(dev, 1, Marshal.SizeOf<N.RAWINPUTDEVICE>());
        Log.Write("raw_input", new { ok, error = ok ? 0 : Marshal.GetLastWin32Error() });
    }

    /// <summary>Post a message to the running instance. False when none is running.</summary>
    public static bool Send(uint msg, bool test = false)
    {
        IntPtr h = N.FindWindowEx(N.HWND_MESSAGE, IntPtr.Zero, null, test ? TestTitle : Title);
        return h != IntPtr.Zero && N.PostMessage(h, (int)msg, IntPtr.Zero, IntPtr.Zero);
    }

    public static bool Running(bool test = false) => N.FindWindowEx(N.HWND_MESSAGE, IntPtr.Zero, null, test ? TestTitle : Title) != IntPtr.Zero;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == N.WM_INPUT) { LastRaw = GetMessageTime(); LastRawFg = N.GetForegroundWindow(); RawCount++; }
        else if ((uint)m.Msg == MsgQuit) QuitRequested?.Invoke();
        else if ((uint)m.Msg == MsgSettings) SettingsRequested?.Invoke();
        else if (_test && (uint)m.Msg == MsgDropHook) DropHookRequested?.Invoke();
        else if (_test && (uint)m.Msg == MsgLayout) LayoutRequested?.Invoke(m.WParam != IntPtr.Zero);
        else if (_test && (uint)m.Msg == MsgWinTab) WinTabRequested?.Invoke(m.WParam != IntPtr.Zero);
        base.WndProc(ref m);
    }

    public void Dispose() => DestroyHandle();
}

/// <summary>
/// Windows removes a low-level hook that is slow to answer, without telling it. Raw input keeps arriving
/// either way, so a key that raw input saw and the hook didn't means the hook is gone: install it again.
/// Skipped while an elevated window is in front and Altlane isn't elevated, because then the hook is
/// legitimately blind.
/// </summary>
sealed class HookWatchdog
{
    readonly KeyboardHook _hook;
    readonly ControlWindow _ctl;
    readonly DispatcherTimer _timer;
    readonly bool _selfElevated;
    public int Reinstalls { get; private set; }

    public HookWatchdog(KeyboardHook hook, ControlWindow ctl, Dispatcher ui)
    {
        _hook = hook; _ctl = ctl;
        _selfElevated = Win.IsElevated((uint)Environment.ProcessId) == true;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Check(), ui);
    }

    public void Start() => _timer.Start();

    void Check()
    {
        if (_ctl.RawCount == 0) return;
        int raw = _ctl.LastRaw, seen = _hook.LastSeen;
        int gap = unchecked(raw - seen), age = unchecked(Environment.TickCount - raw);
        if (gap < 1500 || age > 10_000) return;
        if (!_selfElevated && Win.IsElevated(Win.Pid(_ctl.LastRawFg)) != false) return;
        Reinstalls++;
        Log.Write("hook_reinstalled", new { gap_ms = gap, raw_age_ms = age, reinstalls = Reinstalls });
        _hook.Reinstall();
    }
}
