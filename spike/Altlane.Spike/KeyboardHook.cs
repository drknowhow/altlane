using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;

namespace Altlane.Spike;

enum HookKind { AltStart, AltRelease, WinStart, WinToggle, ShiftWinTab, Key, Panic, Expired }

/// <param name="Ts">Stopwatch timestamp taken on entry to the hook callback.</param>
/// <param name="S2">Commit strategy for this Alt session: false = S1 (Alt-up passes), true = S2 (Alt-up swallowed).</param>
/// <param name="SwallowedAltUp">The hook ate a physical Alt-up; the UI must inject a masked Alt-up, whatever else happens.</param>
readonly record struct HookEvent(HookKind Kind, ushort Vk, bool Shift, bool Ctrl, bool S2, bool SwallowedAltUp, long Ts);

enum Sess { None, Alt, AltSticky, Win }

/// <summary>
/// WH_KEYBOARD_LL on its own thread with its own message loop. The callback only tracks modifiers,
/// decides swallow/pass, and posts an event to the UI dispatcher. Windows silently removes a
/// low-level hook that answers too slowly, so nothing here may block.
/// </summary>
sealed class KeyboardHook
{
    /// <summary>dwExtraInfo on every key this process injects; the hook passes those through untouched.</summary>
    public static readonly IntPtr Magic = (IntPtr)0x414C5449; // "ALTI"

    /// <summary>A session that swallows every key (Win overview, sticky filter) gives up after this long.</summary>
    const double SessionLimitMs = 30_000;

    readonly Dispatcher _ui;
    readonly Action<HookEvent> _handler;
    readonly N.HookProc _proc; // held in a field: a collected delegate means a crash inside user32
    readonly string _strategy;
    IntPtr _hook;
    uint _tid;
    Thread? _thread;

    bool _lalt, _ralt, _lwin, _rwin, _lshift, _rshift, _lctrl, _rctrl, _tabSwallowed;
    volatile Sess _sess;
    long _sessStart;
    bool _sessS2;
    int _sessions;

    // Callback durations in microseconds, for the p99/max report.
    readonly double[] _us = new double[1 << 16];
    int _n;

    public KeyboardHook(Dispatcher ui, Action<HookEvent> handler, string strategy)
    {
        _ui = ui; _handler = handler; _strategy = strategy;
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
            _hook = N.SetWindowsHookEx(N.WH_KEYBOARD_LL, _proc, N.GetModuleHandle(null), 0);
            Log.Write("hook_installed", new { ok = _hook != IntPtr.Zero });
            ready.Set();
            while (N.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { N.TranslateMessage(ref msg); N.DispatchMessage(ref msg); }
            N.UnhookWindowsHookEx(_hook);
        }) { IsBackground = true, Name = "kbhook", Priority = ThreadPriority.Highest };
        _thread.Start();
        ready.Wait();
    }

    public void Stop()
    {
        if (_tid != 0) N.PostThreadMessage(_tid, (uint)N.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
    }

    /// <summary>Called by the UI when a session ends on its side (commit, cancel, click, timeout).</summary>
    public void EndSession() => _sess = Sess.None;

    public (int n, double p50, double p99, double max) Stats()
    {
        int n = Math.Min(_n, _us.Length);
        if (n == 0) return (0, 0, 0, 0);
        var a = new double[n]; Array.Copy(_us, a, n); Array.Sort(a);
        return (n, a[n / 2], a[Math.Min(n - 1, (int)(n * 0.99))], a[n - 1]);
    }

    unsafe IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        long ts = Stopwatch.GetTimestamp();
        if (nCode < 0) return N.CallNextHookEx(_hook, nCode, wParam, lParam);
        var k = *(N.KBDLLHOOKSTRUCT*)lParam;
        if (k.dwExtraInfo == Magic) return N.CallNextHookEx(_hook, nCode, wParam, lParam);

        int msg = (int)wParam;
        bool down = msg is N.WM_KEYDOWN or N.WM_SYSKEYDOWN;
        bool swallow = Decide((ushort)k.vkCode, down, ts);

        int i = Interlocked.Increment(ref _n) - 1;
        if (i < _us.Length) _us[i] = Clock.Between(ts, Stopwatch.GetTimestamp()) * 1000.0;
        return swallow ? (IntPtr)1 : N.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    void Post(HookKind kind, long ts, ushort vk = 0, bool s2 = false, bool swallowedAltUp = false)
    {
        var ev = new HookEvent(kind, vk, Shift, Ctrl, s2, swallowedAltUp, ts);
        _ui.BeginInvoke(DispatcherPriority.Send, _handler, ev);
    }

    bool Decide(ushort vk, bool down, long ts)
    {
        // A session that swallows everything must not outlive a hung or crashed UI.
        if (_sess is Sess.Win or Sess.AltSticky && Clock.Between(_sessStart, ts) > SessionLimitMs)
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
                    bool s2 = _sessS2;
                    _sess = Sess.None;
                    Post(HookKind.AltRelease, ts, s2: s2, swallowedAltUp: s2);
                    return s2;
                }
                return false;
            case N.VK_LWIN: _lwin = down; return false;
            case N.VK_RWIN: _rwin = down; return false;
            case N.VK_SHIFT or N.VK_LSHIFT: _lshift = down; return false;
            case N.VK_RSHIFT: _rshift = down; return false;
            case N.VK_CONTROL or N.VK_LCONTROL: _lctrl = down; return false;
            case N.VK_RCONTROL: _rctrl = down; return false;
        }

        // Panic: Ctrl+Alt+Shift+Q quits, from any state.
        if (down && vk == 'Q' && Ctrl && Alt && Shift) { _sess = Sess.None; Post(HookKind.Panic, ts); return true; }

        if (vk == N.VK_TAB)
        {
            if (!down && _tabSwallowed) { _tabSwallowed = false; return true; }
            if (down && Alt && !WinKey && !Ctrl && _sess is Sess.None or Sess.Alt or Sess.AltSticky)
            {
                _tabSwallowed = true;
                if (_sess == Sess.None)
                {
                    _sess = Sess.Alt; _sessStart = ts;
                    _sessS2 = _strategy == "s2" || (_strategy == "alt" && (_sessions & 1) == 1);
                    _sessions++;
                    Keys.Mask(Magic); // Alt now has a key between its down and up: no menu bar on release
                    Post(HookKind.AltStart, ts, s2: _sessS2);
                }
                else Post(HookKind.Key, ts, vk);
                return true;
            }
            if (down && WinKey && !Alt && !Ctrl)
            {
                _tabSwallowed = true;
                if (Shift)
                {
                    // Shift+Win+Tab is the way back to the real Task View: release Shift as far as the
                    // system knows and replay a plain Win+Tab, all marked so this hook ignores it.
                    var m = Magic;
                    var seq = new System.Collections.Generic.List<N.INPUT>();
                    if (_lshift) seq.Add(Keys.Key(N.VK_LSHIFT, true, m));
                    if (_rshift) seq.Add(Keys.Key(N.VK_RSHIFT, true, m));
                    seq.Add(Keys.Key(N.VK_TAB, false, m)); seq.Add(Keys.Key(N.VK_TAB, true, m));
                    Keys.Send(seq.ToArray());
                    Post(HookKind.ShiftWinTab, ts);
                }
                else if (_sess == Sess.Win) { _sess = Sess.None; Post(HookKind.WinToggle, ts); }
                else
                {
                    _sess = Sess.Win; _sessStart = ts;
                    Keys.Mask(Magic); // Win now has a key between its down and up: no Start menu on release
                    Post(HookKind.WinStart, ts);
                }
                return true;
            }
        }

        if (_sess == Sess.None) return false;

        // Inside a session every non-modifier key belongs to the switcher.
        if (down)
        {
            // Typing a filter pins the Alt view open after Alt is released (Enter commits).
            if (_sess == Sess.Alt && vk is >= 'A' and <= 'Z') { _sess = Sess.AltSticky; _sessStart = ts; }
            Post(HookKind.Key, ts, vk);
        }
        return true;
    }
}
