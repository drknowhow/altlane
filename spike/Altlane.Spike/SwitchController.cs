using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Threading;

namespace Altlane.Spike;

/// <summary>
/// The switching state machine, on the UI thread. Alt+Tab: a quick tap switches to the previous
/// window without drawing anything; holding past ShowDelayMs shows the grid. Win+Tab: the
/// full-screen overview, which stays up until Enter, Esc, a click or the session limit.
/// </summary>
sealed class SwitchController
{
    const int ShowDelayMs = 120;
    enum Mode { Idle, Alt, Win }

    readonly Overlay _ov;
    readonly WindowTracker _wt;
    readonly DispatcherTimer _showTimer, _winLimit;
    KeyboardHook? _hook;

    Mode _mode;
    List<WinInfo> _all = new(), _view = new();
    int _sel, _sid;
    string _filter = "";
    bool _shown, _sticky;
    long _startTs;

    public SwitchController(Overlay ov, WindowTracker wt)
    {
        _ov = ov; _wt = wt;
        _showTimer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(ShowDelayMs) };
        _showTimer.Tick += (_, _) => { _showTimer.Stop(); if (_mode == Mode.Alt && !_shown) Show(); };
        _winLimit = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _winLimit.Tick += (_, _) => Cancel("session_limit");
        _ov.TileClicked += i => { if (_mode != Mode.Idle && i < _view.Count) { _sel = i; Commit("click", false); } };
        _ov.BackdropClicked += () => { if (_mode == Mode.Win) Cancel("backdrop"); };
        _ov.Composed += ms =>
        {
            Log.Write("overlay_composed", new
            {
                sid = _sid, show_ms = Math.Round(ms, 2),
                from_key_ms = Math.Round(Clock.Between(_startTs, Stopwatch.GetTimestamp()), 2),
            });
            // One screenshot per mode, taken after timing is logged so it can't skew the numbers.
            if (ShotDir != null && _mode != Mode.Idle && _shots.Add(_mode))
                _ov.Capture(System.IO.Path.Combine(ShotDir, $"overlay-{_mode.ToString().ToLowerInvariant()}.png"));
        };
    }

    /// <summary>When set, save one PNG of the Alt overlay and one of the Win overview here.</summary>
    public string? ShotDir;
    readonly HashSet<Mode> _shots = new();

    public void Attach(KeyboardHook hook) => _hook = hook;

    public void OnHook(HookEvent e)
    {
        double lag = Clock.Between(e.Ts, Stopwatch.GetTimestamp());
        if (lag > 30) Log.Write("dispatch_lag", new { kind = e.Kind.ToString(), ms = Math.Round(lag, 2) });
        switch (e.Kind)
        {
            case HookKind.AltStart: Start(Mode.Alt, e); break;
            case HookKind.WinStart: Start(Mode.Win, e); break;
            case HookKind.AltRelease: OnAltRelease(e); break;
            case HookKind.WinToggle: Cancel("win_toggle"); break;
            case HookKind.ShiftWinTab: Log.Write("shift_win_tab_replayed"); break;
            case HookKind.Key: OnKey(e); break;
            case HookKind.Expired: Cancel("hook_session_limit"); break;
            case HookKind.Panic: Cancel("panic"); Program.Quit("panic"); break;
        }
    }

    void Start(Mode mode, HookEvent e)
    {
        if (_mode != Mode.Idle) Cancel("restart");
        _sid++;
        _mode = mode; _startTs = e.Ts; _filter = ""; _shown = false; _sticky = false;
        long t = Stopwatch.GetTimestamp();
        _all = _wt.Snapshot();
        double snapMs = Clock.Between(t, Stopwatch.GetTimestamp());
        _view = _all;
        // Alt+Tab starts on the previous window (index 1); the overview starts on the current one.
        _sel = mode == Mode.Alt ? (e.Shift ? _view.Count - 1 : Math.Min(1, _view.Count - 1)) : 0;
        Log.Write(mode == Mode.Alt ? "alt_start" : "win_start", new
        {
            sid = _sid, n = _all.Count, s2 = e.S2, snapshot_ms = Math.Round(snapMs, 2),
            first = _all.Count > 0 ? _all[0].Title : null,
        });
        if (mode == Mode.Alt) _showTimer.Start();
        else { Show(); _winLimit.Start(); }
    }

    void Show()
    {
        if (_mode == Mode.Idle) return;
        string head = (_mode == Mode.Alt ? $"Alt+Tab · {_view.Count} windows" : $"Overview · {_view.Count} windows")
                      + (_filter.Length > 0 ? $" · filter: {_filter}" : "");
        _ov.ShowItems(_view, _sel, head, full: _mode == Mode.Win);
        _shown = true;
    }

    void OnKey(HookEvent e)
    {
        if (_mode == Mode.Idle) return;
        int n = _view.Count;
        switch (e.Vk)
        {
            case N.VK_TAB: case N.VK_RIGHT: case N.VK_LEFT:
                if (n == 0) break;
                int step = e.Vk == N.VK_LEFT || (e.Vk == N.VK_TAB && e.Shift) ? -1 : 1;
                _sel = ((_sel + step) % n + n) % n;
                if (_shown) _ov.SetSelection(_sel);
                break;
            case N.VK_ESCAPE:
                if (_filter.Length > 0) { _filter = ""; Refilter(); }
                else Cancel("esc");
                break;
            case N.VK_RETURN: Commit("enter", false); break;
            case N.VK_BACK:
                if (_filter.Length > 0) { _filter = _filter[..^1]; Refilter(); }
                break;
            default:
                if (e.Vk is >= 'A' and <= 'Z')
                {
                    if (_mode == Mode.Alt) _sticky = true;
                    _filter += char.ToLowerInvariant((char)e.Vk);
                    Refilter();
                }
                break;
        }
    }

    void Refilter()
    {
        _view = _filter.Length == 0 ? _all : _all.Where(w =>
            w.Title.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
            w.Process.Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToList();
        _sel = 0;
        _showTimer.Stop();
        Show();
    }

    void OnAltRelease(HookEvent e)
    {
        if (_mode == Mode.Alt && !_sticky) Commit("alt_up", e.S2);
        else if (e.SwallowedAltUp) ReleaseAlt(); // session already gone; the physical Alt-up still has to land
    }

    static void ReleaseAlt() => Keys.Send(
        Keys.Key(N.VK_MASK, false, KeyboardHook.Magic), Keys.Key(N.VK_MASK, true, KeyboardHook.Magic),
        Keys.Key(N.VK_LMENU, true, KeyboardHook.Magic), Keys.Key(N.VK_RMENU, true, KeyboardHook.Magic));

    void Commit(string how, bool s2)
    {
        if (_mode == Mode.Idle) return;
        if (_view.Count == 0 || _sel < 0 || _sel >= _view.Count)
        {
            Cancel("nothing_to_commit");
            if (s2) ReleaseAlt();
            return;
        }
        var target = _view[_sel];
        bool quick = !_shown;
        var mode = _mode;
        End();
        var r = Activator.Activate(target.Hwnd, KeyboardHook.Magic);
        if (s2) ReleaseAlt(); // S2: switch first while Alt is still down, then let Alt go
        Log.Write("switch", new
        {
            sid = _sid, mode = mode.ToString(), how, quick, s2, target = target.Title, index = _sel,
            ok = r.Ok, method = r.Method, act_ms = Math.Round(r.Ms, 2), was_min = r.WasMinimized,
            from_key_ms = Math.Round(Clock.Between(_startTs, Stopwatch.GetTimestamp()), 2),
        });
    }

    void Cancel(string why)
    {
        if (_mode == Mode.Idle) return;
        End();
        Log.Write("cancel", new { sid = _sid, why });
    }

    void End()
    {
        _showTimer.Stop();
        _winLimit.Stop();
        _ov.HideOverlay();
        _hook?.EndSession();
        _mode = Mode.Idle;
    }
}
