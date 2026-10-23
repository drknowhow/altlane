using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace Altlane;

enum HookKind { AltStart, AltRelease, WinStart, WinToggle, ShiftWinTab, Key, Direct, Panic, Expired }

/// <param name="Ts">Stopwatch timestamp taken on entry to the hook callback.</param>
/// <param name="Grave">AltStart only: the session began with Alt+` (same-group) rather than Alt+Tab.</param>
/// <param name="Num">Direct only: the number key pressed with Alt.</param>
readonly record struct HookEvent(HookKind Kind, ushort Vk, bool Shift, bool Ctrl, bool Grave, int Num, long Ts);

enum Sess { None, Alt, AltSticky, Win }

/// <summary>
/// WH_KEYBOARD_LL on its own thread with its own message loop. The callback only tracks modifiers,
/// decides swallow/pass, and posts an event to the UI dispatcher. Windows silently removes a
/// low-level hook that answers too slowly, so nothing here may block; HookWatchdog notices a removal.
/// Win+Tab opens the lanes overview, which takes every key until it closes; Shift+Win+Tab and the
/// WinTab setting turned off leave Windows' Task View.
/// </summary>
sealed class KeyboardHook
{
    /// <summary>dwExtraInfo on every key this process injects; the hook passes those through untouched.</summary>
    public static readonly IntPtr Magic = (IntPtr)0x414C5449; // "ALTI"

    /// <summary>A session that swallows every key (sticky filter) gives up after this long.</summary>
    const double SessionLimitMs = 30_000;
    /// <summary>The overview stays open as long as the UI answers; a key this long after an unanswered event ends it.</summary>
    const double UiHungMs = 3_000;
    const uint WM_REHOOK = N.WM_USER + 1, WM_DROP = N.WM_USER + 2;
    /// <summary>Scan code of the key above Tab, whatever the layout calls it.</summary>
    const uint ScanGrave = 0x29;

    readonly Dispatcher _ui;
    readonly Action<HookEvent> _handler;
    readonly N.HookProc _proc; // held in a field: a collected delegate means a crash inside user32
    IntPtr _hook;
    uint _tid;
    Thread? _thread;

    bool _lalt, _ralt, _lwin, _rwin, _lshift, _rshift, _lctrl, _rctrl;
    readonly bool[] _ateDown = new bool[256]; // a key whose down we swallowed: swallow its up too
    volatile Sess _sess;
    long _sessStart;
    long _posted, _acked; // timestamps of the last event posted to the UI and the last one it handled

    /// <summary>Settings the hook reads on every key. Written by the UI thread.</summary>
    public volatile bool Paused, SameGroupKey = true, DirectJump, WinTab = true;

    /// <summary>Event time (GetTickCount clock) of the last key the hook saw, injected keys included.
    /// The watchdog compares it with the event time of the last raw input message.</summary>
    public int LastSeen => Volatile.Read(ref _lastSeen);
    int _lastSeen;
    public long Seen => Interlocked.Read(ref _seen);
    long _seen;
    public int Reinstalls { get; private set; }

    // Callback durations in microseconds, for the p99/max report.
    readonly double[] _us = new double[1 << 16];
    int _n;

    public KeyboardHook(Dispatcher ui, Action<HookEvent> handler)
    {
        _ui = ui; _handler = handler;
        _proc = Proc;
    }

    bool Alt => _lalt || _ralt;
    bool WinKey => _lwin || _rwin;
    bool Shift => _lshift || _rshift;
    bool Ctrl => _lctrl || _rctrl;
    public Sess Session => _sess;

    public void Start()
    {
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _tid = N.GetCurrentThreadId();
            Install("start");
            ready.Set();
            while (N.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.hwnd == IntPtr.Zero && msg.message == WM_REHOOK) { N.UnhookWindowsHookEx(_hook); Install("watchdog"); Reinstalls++; continue; }
                if (msg.hwnd == IntPtr.Zero && msg.message == WM_DROP) { N.UnhookWindowsHookEx(_hook); Log.Write("hook_dropped_for_test"); continue; }
                N.TranslateMessage(ref msg); N.DispatchMessage(ref msg);
            }
            N.UnhookWindowsHookEx(_hook);
        }) { IsBackground = true, Name = "kbhook", Priority = ThreadPriority.Highest };
        _thread.Start();
        ready.Wait();
    }

    void Install(string why)
    {
        // Modifier state may be stale after a gap without a hook; start clean.
        _lalt = _ralt = _lwin = _rwin = _lshift = _rshift = _lctrl = _rctrl = false;
        Array.Clear(_ateDown);
        _sess = Sess.None;
        _hook = N.SetWindowsHookEx(N.WH_KEYBOARD_LL, _proc, N.GetModuleHandle(null), 0);
        Volatile.Write(ref _lastSeen, Environment.TickCount);
        Log.Write("hook_installed", new { ok = _hook != IntPtr.Zero, why });
    }

    public void Stop()
    {
        if (_tid != 0) N.PostThreadMessage(_tid, (uint)N.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
    }

    /// <summary>Remove and install the hook again, on its own thread.</summary>
    public void Reinstall() { if (_tid != 0) N.PostThreadMessage(_tid, WM_REHOOK, IntPtr.Zero, IntPtr.Zero); }

    /// <summary>Self-test only: remove the hook without telling anyone, the way Windows does after a timeout.</summary>
    public void DropForTest() { if (_tid != 0) N.PostThreadMessage(_tid, WM_DROP, IntPtr.Zero, IntPtr.Zero); }

    /// <summary>Called by the UI when a session ends on its side (commit, cancel, click, timeout).</summary>
    public void EndSession() => _sess = Sess.None;

    /// <summary>Called by the UI as it handles each event, so the hook can tell a slow overview from a hung one.</summary>
    public void Ack(long ts) => Volatile.Write(ref _acked, ts);

    public (int n, double p50, double p99, double max) Stats()
    {
        int n = Math.Min(_n, _us.Length);
        if (n == 0) return (0, 0, 0, 0);
        var a = new double[n]; Array.Copy(_us, a, n); Array.Sort(a);
        return (n, a[n / 2], a[Math.Min(n - 1, (int)(n * 0.99))], a[n - 1]);
    }

    [DllImport("user32")] static extern short GetAsyncKeyState(int vk);

    unsafe IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        long ts = Stopwatch.GetTimestamp();
        if (nCode < 0) return N.CallNextHookEx(_hook, nCode, wParam, lParam);
        var k = *(N.KBDLLHOOKSTRUCT*)lParam;
        Volatile.Write(ref _lastSeen, (int)k.time);
        Interlocked.Increment(ref _seen);
        if (k.dwExtraInfo == Magic) return N.CallNextHookEx(_hook, nCode, wParam, lParam);

        int msg = (int)wParam;
        bool down = msg is N.WM_KEYDOWN or N.WM_SYSKEYDOWN;
        bool swallow;
        // An exception escaping a hook callback would take the process down with every key it held.
        try { swallow = Decide((ushort)k.vkCode, k.scanCode, down, ts); }
        catch (Exception e) { swallow = false; _sess = Sess.None; Log.Write("hook_error", new { error = e.Message }); }

        int i = Interlocked.Increment(ref _n) - 1;
        if (i < _us.Length) _us[i] = Clock.Between(ts, Stopwatch.GetTimestamp()) * 1000.0;
        return swallow ? (IntPtr)1 : N.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    void Post(HookKind kind, long ts, ushort vk = 0, bool grave = false, int num = 0)
    {
        var ev = new HookEvent(kind, vk, Shift, Ctrl, grave, num, ts);
        Volatile.Write(ref _posted, ts);
        _ui.BeginInvoke(DispatcherPriority.Send, _handler, ev);
    }

    bool Eat(ushort vk) { _ateDown[vk & 0xFF] = true; return true; }

    bool Decide(ushort vk, uint scan, bool down, long ts)
    {
        // A session that swallows everything must not outlive a hung or crashed UI.
        if (_sess == Sess.AltSticky && Clock.Between(_sessStart, ts) > SessionLimitMs)
        {
            _sess = Sess.None;
            Post(HookKind.Expired, ts);
        }
        // The overview has no time limit, so a hung UI must not keep it swallowing keys.
        if (_sess == Sess.Win && Volatile.Read(ref _acked) < Volatile.Read(ref _posted) && Clock.Between(Volatile.Read(ref _posted), ts) > UiHungMs)
        {
            _sess = Sess.None;
            Post(HookKind.Expired, ts);
        }

        switch (vk)
        {
            case N.VK_MENU or N.VK_LMENU or N.VK_RMENU:
                if (vk == N.VK_RMENU) _ralt = down; else _lalt = down;
                if (!down && !Alt && _sess == Sess.Alt)
                {
                    _sess = Sess.None;
                    Post(HookKind.AltRelease, ts);
                }
                return false; // S1: Alt-up always reaches the system; the switch happens after it
            case N.VK_LWIN: _lwin = down; return false;
            case N.VK_RWIN: _rwin = down; return false;
            case N.VK_SHIFT or N.VK_LSHIFT: _lshift = down; return false;
            case N.VK_RSHIFT: _rshift = down; return false;
            case N.VK_CONTROL or N.VK_LCONTROL: _lctrl = down; return false;
            case N.VK_RCONTROL: _rctrl = down; return false;
        }

        if (!down && _ateDown[vk & 0xFF]) { _ateDown[vk & 0xFF] = false; return true; }

        // Panic: Ctrl+Alt+Shift+Q quits, from any state, paused or not.
        if (down && vk == 'Q' && Ctrl && Alt && Shift) { _sess = Sess.None; Post(HookKind.Panic, ts); return Eat(vk); }

        if (Paused) { _sess = Sess.None; return false; }

        // An Alt-up we never saw (the hook was gone for a moment) would leave a session swallowing
        // every key. Ask the system whether Alt is really down before treating a key as a session key.
        if (_sess == Sess.Alt && down && (GetAsyncKeyState(N.VK_MENU) & 0x8000) == 0)
        {
            _lalt = _ralt = false;
            _sess = Sess.None;
            Post(HookKind.AltRelease, ts);
        }

        bool grave = scan == ScanGrave || vk == N.VK_OEM_3;
        if (down && Alt && !WinKey && !Ctrl && _sess == Sess.None)
        {
            if (vk == N.VK_TAB || (grave && SameGroupKey))
            {
                _sess = Sess.Alt; _sessStart = ts;
                Keys.Mask(Magic); // Alt now has a key between its down and up: no menu bar on release
                Post(HookKind.AltStart, ts, vk, grave: vk != N.VK_TAB);
                return Eat(vk);
            }
            if (DirectJump && !Shift && vk is >= '1' and <= '9')
            {
                Keys.Mask(Magic);
                Post(HookKind.Direct, ts, vk, num: vk - '0');
                return Eat(vk);
            }
        }

        if (down && vk == N.VK_TAB && WinKey && !Alt && !Ctrl && WinTab && _sess is Sess.None or Sess.AltSticky or Sess.Win)
        {
            // Win now has a key between its down and up: no Start menu on release.
            Keys.Mask(Magic);
            if (Shift)
            {
                // Shift+Win+Tab is the way to Windows' Task View: release Shift as far as the system knows
                // and replay a plain Win+Tab, all marked so this hook passes it through.
                var seq = new System.Collections.Generic.List<N.INPUT>();
                if (_lshift) seq.Add(Keys.Key(N.VK_LSHIFT, true, Magic));
                if (_rshift) seq.Add(Keys.Key(N.VK_RSHIFT, true, Magic));
                seq.Add(Keys.Key(N.VK_TAB, false, Magic)); seq.Add(Keys.Key(N.VK_TAB, true, Magic));
                Keys.Send(seq.ToArray());
                _sess = Sess.None;
                Post(HookKind.ShiftWinTab, ts);
            }
            else if (_sess == Sess.Win) { _sess = Sess.None; Post(HookKind.WinToggle, ts); }
            else
            {
                _sess = Sess.Win; _sessStart = ts;
                Post(HookKind.WinStart, ts);
            }
            return Eat(vk);
        }

        if (_sess == Sess.None) return false;

        // Inside a session every non-modifier key belongs to the switcher.
        if (down)
        {
            // Typing a filter pins the view open after Alt is released (Enter commits).
            if (_sess == Sess.Alt && !Ctrl && (vk is >= 'A' and <= 'Z' || vk == N.VK_SPACE)) { _sess = Sess.AltSticky; _sessStart = ts; }
            // Alt+Shift and Ctrl+Shift switch the keyboard layout when nothing else is pressed in between.
            // Windows never sees the key we swallow, so give it one it can see.
            // Win+key with the key swallowed would leave Win pressed alone, which opens Start.
            if (Shift || Ctrl || WinKey) Keys.Mask(Magic);
            Post(HookKind.Key, ts, vk, grave: grave);
            return Eat(vk);
        }
        return true;
    }
}
