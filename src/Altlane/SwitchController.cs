using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace Altlane;

/// <summary>
/// The switching state machine, on the UI thread. Alt+Tab: a quick tap switches to the previous
/// window without drawing anything; holding past the show delay draws layout A. Alt+` does the same
/// inside the current window's group. Typing filters and pins the view open until Enter or Esc.
/// Win+Tab opens the lanes overview at once and keeps it open, without Alt held, until Enter, a click
/// or Esc; there every window appears once, in its group, and the keys work as in the columns layout.
/// </summary>
sealed class SwitchController
{
    readonly Overlay _ov;
    readonly WindowTracker _wt;
    readonly RuleStore _rules;
    readonly Settings _settings;
    readonly Terminals? _terms;
    readonly DispatcherTimer _showTimer, _prebuild, _closeCheck, _trim;
    readonly bool _test;
    // Windows asked to close, by session: one that asks to save stays open, and its card comes back.
    readonly List<(int sid, IntPtr hwnd)> _closing = new();
    KeyboardHook? _hook;

    bool _active, _shown, _sticky;
    bool _columns; // the columns layout, fixed for the session: the arrow keys depend on it
    bool _overview; // Win+Tab's lanes overview rather than Alt+Tab
    string? _scope; // group key while in an Alt+` session
    List<Item> _items = new();
    List<Section> _secs = new();
    Slot? _sel;
    string _filter = "";
    int _hidden, _sid;
    long _startTs;
    IntPtr _activating, _startFg;
    string _shotName = "";
    // How the selection last moved (tab, grave, arrow, number, mouse), and how often each way was used
    // this session: the switch log says how each window was picked, never which window or what was typed.
    string _pick = "first";
    readonly Dictionary<string, int> _nav = new();

    /// <summary>When set, save one PNG per kind of view here (self-test).</summary>
    public string? ShotDir;
    readonly HashSet<string> _shots = new();

    public SwitchController(Overlay ov, WindowTracker wt, RuleStore rules, Settings settings, bool test, Terminals? terms = null)
    {
        _ov = ov; _wt = wt; _rules = rules; _settings = settings; _test = test; _terms = terms;
        _showTimer = new DispatcherTimer(DispatcherPriority.Send);
        _showTimer.Tick += (_, _) => { _showTimer.Stop(); if (_active && !_shown) Show(); };
        _prebuild = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1200) };
        _prebuild.Tick += (_, _) => { _prebuild.Stop(); Prebuild(); };
        _closeCheck = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1500) };
        _closeCheck.Tick += (_, _) => { _closeCheck.Stop(); CheckClosed(); };
        _trim = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMinutes(1) };
        _trim.Tick += (_, _) => { _trim.Stop(); TrimHeap(); };
        _ov.CardClicked += slot => { if (_active) { _sel = slot; Commit("click"); } };
        _ov.CardMiddleClicked += slot => { if (_active && _settings.MouseActions) { _sel = slot; CloseSelected("middle_click"); } };
        _ov.CardCloseClicked += slot => { if (_active && _settings.CloseButton) { _sel = slot; CloseSelected("button"); } };
        _ov.CardDropped += (slot, g) => { if (_active) { _sel = slot; Regroup(g.Num, "drag"); } };
        _ov.CardHovered += slot =>
        {
            if (!_active || _sel == slot || (!_overview && !_settings.HoverSelects)) return;
            Select(slot, "mouse");
            if (_test) Log.Write("hover", new { sid = _sid, sel = slot.Hwnd.ToInt64(), sel_section = slot.Section });
        };
        _ov.BackgroundClicked += () => { if (_active && _overview) Cancel("click_outside"); };
        _ov.Composed += ms =>
        {
            Log.Write("overlay_composed", new
            {
                sid = _sid, lanes = _overview, show_ms = Math.Round(ms, 2),
                from_key_ms = Math.Round(Clock.Between(_startTs, Stopwatch.GetTimestamp()), 2),
                cards = _secs.Sum(s => s.Items.Count),
                reused = _ov.LastReused,
                parts = new
                {
                    clear = Math.Round(_ov.LastParts.clear, 2), draw = Math.Round(_ov.LastParts.draw, 2), place = Math.Round(_ov.LastParts.place, 2),
                    show = Math.Round(_ov.LastParts.show, 2), layout = Math.Round(_ov.LastParts.layout, 2), thumbs = Math.Round(_ov.LastParts.thumbs, 2),
                },
                gcs = _ov.LastCost.gcs, gc_ms = Math.Round(_ov.LastCost.gcMs, 2), mcycles = Math.Round(_ov.LastCost.mcycles, 1),
            });
            // One screenshot per kind of view, taken after timing is logged so it can't skew the numbers.
            if (ShotDir != null && _active && _shots.Add(_shotName))
                _ov.Capture(Path.Combine(ShotDir, $"overlay-{_shotName}.png"));
        };
        _wt.Foreground += h =>
        {
            // A click on another window (or anything else taking the foreground) ends the session. A late
            // event for the window that was already in front when the session began is not a change.
            var k = Win.MruKey(h);
            if (_active && k != _startFg && k != Win.MruKey(_activating)) Cancel("foreground_changed");
            // New windows usually arrive in front. Once per burst, not once per window.
            if (!_active && !_prebuild.IsEnabled) _prebuild.Start();
        };
        _rules.Changed += () => { if (_active) { Reclassify(); Rebuild(keepHwnd: true); } };
        // A tab list read after the session began (Alt+Tab's show delay usually hides the wait), or a
        // session that started waiting for you: show it, keeping the selection.
        if (_terms != null)
            _terms.Changed += () =>
            {
                if (!_active) return;
                _items = Collect(out _hidden, out _);
                Rebuild(keepHwnd: true);
                if (_test) Log.Write("terminals_changed", new { sid = _sid, tabs = _items.Count(i => i.W.Tab != null) });
            };
        ArmTrim();
    }

    public void Attach(KeyboardHook hook) => _hook = hook;
    public bool Active => _active;

    /// <summary>The sections Alt+Tab would show right now, for warming the overlay at startup.</summary>
    public List<Section> Preview() => Sections.Build(Collect(out _, out _), _rules.Current.Groups, "", needsFirst: _settings.NeedsFirst);

    /// <summary>
    /// A moment after the window in front changes, build Alt+Tab's cards with the overlay hidden. After a
    /// burst of new windows, the next Alt+Tab then reuses them instead of building and measuring them
    /// all (on a busy desktop that was the slowest show by 20 ms).
    /// </summary>
    void Prebuild()
    {
        if (_active || _ov.IsVisible) return;
        long t = Stopwatch.GetTimestamp();
        var secs = Preview();
        int built = _ov.Prebuild(secs, _settings.Columns ? ViewKind.Columns : ViewKind.Grid);
        if (built > 0) Log.Write("prebuild", new { cards = secs.Sum(s => s.Items.Count), built, ms = Math.Round(Clock.Between(t, Stopwatch.GetTimestamp()), 2) });
        ArmTrim();
    }

    /// <summary>How long Altlane waits after a session (or a prebuild) before it trims its heap; zero turns trimming off.</summary>
    public TimeSpan TrimAfter
    {
        get => _trim.Interval;
        set { _trim.Interval = value; ArmTrim(); }
    }

    void ArmTrim()
    {
        _trim.Stop();
        if (_trim.Interval > TimeSpan.Zero) _trim.Start();
    }

    /// <summary>
    /// Give the heap's spare room back once Altlane has been quiet for a while. Between collections the
    /// GC keeps the young generation's whole budget committed: about 14 MB, against some 6 MB of live
    /// objects. One collection that compacts and decommits frees that until the next session needs it,
    /// and faulting the pages back in costs that session a few milliseconds.
    /// </summary>
    void TrimHeap()
    {
        if (_active || _ov.IsVisible) return;
        long t = Stopwatch.GetTimestamp(), ws = Environment.WorkingSet;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        var gc = GC.GetGCMemoryInfo(GCKind.Any);
        const double MB = 1 << 20;
        Log.Write("gc_trim", new
        {
            ms = Math.Round(Clock.Between(t, Stopwatch.GetTimestamp()), 2),
            ws_mb_before = Math.Round(ws / MB, 1), ws_mb = Math.Round(Environment.WorkingSet / MB, 1),
            heap_mb = Math.Round(gc.HeapSizeBytes / MB, 1), committed_mb = Math.Round(gc.TotalCommittedBytes / MB, 1),
        });
    }

    public void OnHook(HookEvent e)
    {
        _hook?.Ack(e.Ts);
        double lag = Clock.Between(e.Ts, Stopwatch.GetTimestamp());
        if (lag > 30) Log.Write("dispatch_lag", new { kind = e.Kind.ToString(), ms = Math.Round(lag, 2) });
        switch (e.Kind)
        {
            case HookKind.AltStart: Start(e); break;
            case HookKind.AltRelease: if (_active && !_sticky) Commit("alt_up"); break;
            case HookKind.WinStart: StartOverview(e); break;
            case HookKind.WinToggle: Cancel("win_tab"); break;
            case HookKind.ShiftWinTab: Log.Write("task_view", new { sid = _sid, was_open = _active }); Cancel("shift_win_tab"); break;
            case HookKind.Key: OnKey(e); break;
            case HookKind.Direct: Direct(e); break;
            case HookKind.Expired: Cancel("hook_session_limit"); break;
            case HookKind.Panic: Cancel("panic"); App.Quit("panic"); break;
        }
    }

    // ---------- items ----------

    /// <summary>Snapshot and classify. Hidden windows are counted and dropped; the window in front goes first.</summary>
    List<Item> Collect(out int hidden, out double snapMs)
    {
        long t = Stopwatch.GetTimestamp();
        var wins = Expand(_wt.Snapshot());
        snapMs = Clock.Between(t, Stopwatch.GetTimestamp());
        IntPtr fgKey = Win.MruKey(N.GetForegroundWindow());
        var items = new List<Item>(wins.Count);
        hidden = 0;
        foreach (var w in wins)
        {
            var p = _rules.Place(w);
            if (p.Group == null) { hidden++; continue; }
            items.Add(new Item(w, p, Win.MruKey(w.Hwnd) == fgKey && !w.HiddenTab));
        }
        int cur = items.FindIndex(i => i.IsCurrent);
        if (cur > 0) { var c = items[cur]; items.RemoveAt(cur); items.Insert(0, c); }
        return items;
    }

    /// <summary>
    /// A Windows Terminal window with several tabs becomes one entry per tab, from the tab lists read in
    /// the background: the tab in front first, where the window stands in MRU order, then its other tabs,
    /// most recently in front first. Every entry gets its Claude Code session state when one matches.
    /// </summary>
    List<WinInfo> Expand(List<WinInfo> wins)
    {
        if (_terms == null) return wins;
        var outp = new List<WinInfo>(wins.Count);
        foreach (var w in wins)
        {
            var tabs = _settings.TerminalTabs && Terminals.IsTerminal(w) ? _terms.TabsOf(w.Hwnd) : null;
            if (tabs == null || tabs.Count < 2 || !tabs.Any(t => t.Selected))
            {
                outp.Add(_settings.ClaudeStatus ? w with { Agent = _terms.AgentFor(w.Title) } : w);
                continue;
            }
            foreach (var tab in tabs.OrderBy(t => t.Selected ? 0 : 1).ThenByDescending(t => _terms.LastSelected(w.Hwnd, t.Name)).ThenBy(t => t.Index))
                outp.Add(w with { Title = tab.Name, Tab = tab, Agent = _settings.ClaudeStatus ? _terms.AgentFor(tab.Name) : null });
        }
        return outp;
    }

    void Reclassify()
    {
        _items = _items.Select(i => i with { P = _rules.Place(i.W) }).Where(i => i.P.Group != null).ToList();
    }

    void Rebuild(bool keepHwnd)
    {
        // The card to keep, found again by window and tab name: a tab list read since may have moved its index.
        var keepItem = Sections.ItemAt(_secs, _sel);
        string? keepSec = _sel?.Section;
        _secs = Build();
        var flat = Sections.Flat(_secs);
        if (keepHwnd && keepItem != null)
            _sel = flat.Where(s => Sections.ItemAt(_secs, s) is Item it && WinKey.Of(it.W) == WinKey.Of(keepItem.W))
                       .OrderBy(s => s.Section == keepSec ? 0 : 1).Cast<Slot?>().FirstOrDefault() ?? FirstNotCurrent(flat);
        else _sel = _overview && _filter.Length == 0 ? Previous(flat) : FirstNotCurrent(flat);
        if (_shown) Show();
    }

    /// <summary>The overview has no Recent row: each window appears once, in its group.</summary>
    List<Section> Build() => Sections.Build(_items, _rules.Current.Groups, _filter, _scope, recent: !_overview, needsFirst: _settings.NeedsFirst);

    // ---------- session ----------

    void Start(HookEvent e)
    {
        if (_active) Cancel("restart");
        _sid++;
        _active = true; _shown = false; _sticky = false; _overview = false; _filter = ""; _scope = null; _columns = _settings.Columns; _startTs = e.Ts; _activating = IntPtr.Zero;
        _trim.Stop();
        _pick = "first"; _nav.Clear();
        _startFg = Win.MruKey(N.GetForegroundWindow());
        _terms?.Refresh();
        long t0 = Stopwatch.GetTimestamp();
        _items = Collect(out _hidden, out double snapMs);

        if (e.Grave)
        {
            var anchor = _items.FirstOrDefault();
            _scope = anchor?.P.Group?.Key;
            if (anchor == null || _items.Count(i => i.P.Group == anchor.P.Group) < 2)
            {
                string name = anchor?.P.Group?.Name ?? "this group";
                End();
                _ov.Toast($"No other window in {name}");
                Log.Write("same_group_empty", new { sid = _sid });
                return;
            }
        }

        _secs = Build();
        var flat = Sections.Flat(_secs);
        _sel = e.Shift && flat.Count > 0 ? flat[^1] : FirstNotCurrent(flat);
        _shotName = _scope != null ? "same-group" : MainShot;

        Log.Write("session_start", new
        {
            sid = _sid, kind = _scope != null ? "same_group" : "alt_tab", n = _items.Count, hidden = _hidden,
            snapshot_ms = Math.Round(snapMs, 2), build_ms = Math.Round(Clock.Between(t0, Stopwatch.GetTimestamp()), 2),
        });
        _showTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.ShowDelayMs, 0, 1000));
        _showTimer.Start();
    }

    /// <summary>Win+Tab: the lanes overview, drawn at once, open until Enter, a click, Esc or Win+Tab again.</summary>
    void StartOverview(HookEvent e)
    {
        if (_active) Cancel("restart");
        _sid++;
        _active = true; _shown = false; _sticky = true; _overview = true; _columns = true; _filter = ""; _scope = null; _startTs = e.Ts; _activating = IntPtr.Zero;
        _trim.Stop();
        _pick = "first"; _nav.Clear();
        _startFg = Win.MruKey(N.GetForegroundWindow());
        _terms?.Refresh();
        long t0 = Stopwatch.GetTimestamp();
        _items = Collect(out _hidden, out double snapMs);
        _secs = Build();
        _sel = Previous(Sections.Flat(_secs));
        _shotName = MainShot;
        Log.Write("session_start", new
        {
            sid = _sid, kind = "overview", n = _items.Count, hidden = _hidden,
            snapshot_ms = Math.Round(snapMs, 2), build_ms = Math.Round(Clock.Between(t0, Stopwatch.GetTimestamp()), 2),
        });
        Show();
    }

    /// <summary>The card of the most recent window that isn't in front, so Enter right away goes back to it.</summary>
    Slot? Previous(List<Slot> flat)
    {
        var prev = _items.FirstOrDefault(i => !i.IsCurrent && !i.W.HiddenTab);
        return (prev == null ? null : flat.Where(s => s.Hwnd == prev.W.Hwnd && s.Tab == (prev.W.Tab?.Index ?? -1)).Cast<Slot?>().FirstOrDefault())
            ?? FirstNotCurrent(flat);
    }

    /// <summary>The first card that isn't the window already in front: committing that one would go nowhere.</summary>
    Slot? FirstNotCurrent(List<Slot> flat) =>
        flat.Where(s => Sections.ItemAt(_secs, s) is Item it && !it.IsCurrent).Cast<Slot?>().FirstOrDefault() ?? flat.Cast<Slot?>().FirstOrDefault();

    /// <summary>The self-test's screenshot of the full view, one per layout.</summary>
    string MainShot => _overview ? "lanes" : _columns ? "columns" : "alt-tab";

    ViewKind View => _overview ? ViewKind.Lanes : _columns ? ViewKind.Columns : ViewKind.Grid;

    string Hints()
    {
        int n = _rules.Current.Groups.Count;
        if (_overview)
            return $"Tab  next     `  next group     1–{n}  jump to group     ↑ ↓ ← →  move     type to search     Ctrl+1–{n}  move to group     Del  close window     Enter  switch     Esc  close     Shift+Win+Tab  Task View";
        return _scope != null
            ? "`  next in this group     Tab  all windows     Del  close window     Esc  cancel"
            : $"Tab  next     `  next group     1–{n}  jump to group     type to filter     Ctrl+1–{n}  move to group     Del  close window     Esc  cancel";
    }

    string Why(Slot? sel)
    {
        var it = Sections.ItemAt(_secs, sel);
        if (it?.P.Group == null) return "";
        string why = $"Why {it.P.Group.Name}: {it.P.Why}";
        if (it.W.Tab is TermTab t) why += $" Windows Terminal tab {t.Index + 1} of {t.Count}.";
        if (it.W.Agent is AgentInfo a)
            why += a.State switch
            {
                "needs" => $" Claude Code has been waiting for you for {Terminals.Ago(a.Since).Replace("just now", "under a minute")}.",
                "working" => " Claude Code is working.",
                _ => "",
            };
        return why;
    }

    void Show()
    {
        if (!_active) return;
        _ov.DragEnabled = _settings.MouseActions && _scope == null;
        _ov.CloseButtons = _settings.CloseButton;
        _ov.ShowSections(_secs, _sel, _filter, _scope == null ? _hidden : 0, Why(_sel), Hints(), View);
        _shown = true;
        if (_test)
            Log.Write("view", new
            {
                sid = _sid, filter = _filter, scope = _scope, layout = _overview ? "lanes" : _columns ? Settings.ColumnsLayout : Settings.GridLayout,
                sections = _secs.Select(s => new
                {
                    key = s.Key, hwnds = s.Items.Select(i => i.W.Hwnd.ToInt64()).ToArray(),
                    tabs = s.Items.Select(i => i.W.Tab?.Index ?? -1).ToArray(), agents = s.Items.Select(i => i.W.Agent?.State).ToArray(),
                }).ToArray(),
                sel = _sel?.Hwnd.ToInt64(), sel_tab = _sel?.Tab, sel_section = _sel?.Section,
                // Where each card is on screen, so the self-test can point the mouse at one.
                cards = _ov.CardRects(), drops = _ov.DropRects(),
            });
    }

    void Select(Slot? s, string via)
    {
        if (s == null) return;
        _sel = s;
        _pick = via;
        _nav[via] = _nav.GetValueOrDefault(via) + 1;
        if (_shown) _ov.SetSelection(_sel, Why(_sel));
    }

    void OnKey(HookEvent e)
    {
        if (!_active) return;
        ushort vk = e.Vk;
        if (vk == N.VK_TAB)
        {
            if (_scope != null) { _scope = null; _shotName = MainShot; Rebuild(keepHwnd: true); return; }
            Select(Sections.Move(_secs, _sel, e.Shift ? -1 : 1), "tab");
        }
        else if (e.Grave)
        {
            if (_scope != null) Select(Sections.Move(_secs, _sel, e.Shift ? -1 : 1), "grave");
            else Select(Sections.JumpSection(_secs, _sel, e.Shift ? -1 : 1), "grave");
        }
        // Grid: ← → step through the cards, ↓ ↑ through the sections. Columns: ↓ ↑ within a column, ← → across.
        else if (vk is N.VK_RIGHT or N.VK_LEFT)
        {
            int d = vk == N.VK_RIGHT ? 1 : -1;
            Select(_columns ? Sections.MoveAcrossColumns(_secs, _sel, d) : Sections.Move(_secs, _sel, d), "arrow");
        }
        else if (vk is N.VK_DOWN or N.VK_UP)
        {
            int d = vk == N.VK_DOWN ? 1 : -1;
            Select(_columns ? Sections.MoveInColumn(_secs, _sel, d) : Sections.JumpSection(_secs, _sel, d), "arrow");
        }
        else if (vk is >= '1' and <= '9')
        {
            int num = vk - '0';
            if (e.Ctrl) { Regroup(num, "key"); return; }
            if (_scope != null) { _scope = null; Rebuild(keepHwnd: true); }
            var g = _rules.Current.ByNum(num);
            var s = Sections.Group(_secs, num);
            if (s != null) Select(s, "number");
            else Flash(g == null ? $"There is no group {num}" : $"Group {num}, {g.Name}, has no windows right now");
        }
        else if (vk == N.VK_RETURN) Commit("enter");
        else if (vk == N.VK_ESCAPE)
        {
            if (_filter.Length > 0) { _filter = ""; Rebuild(keepHwnd: false); }
            else Cancel("esc");
        }
        else if (vk == N.VK_DELETE) CloseSelected("del");
        else if (vk == N.VK_BACK)
        {
            if (_filter.Length > 0) { _filter = _filter[..^1]; Rebuild(keepHwnd: false); }
        }
        else if (vk is >= 'A' and <= 'Z' || vk == N.VK_SPACE)
        {
            if (e.Ctrl) return;
            _sticky = true;
            _scope = null;
            _filter += vk == N.VK_SPACE ? ' ' : char.ToLowerInvariant((char)vk);
            _nav["typed"] = _nav.GetValueOrDefault("typed") + 1;
            _showTimer.Stop();
            _shotName = _overview ? "lanes-filter" : "filter";
            _shown = true; // a filter always shows the view
            Rebuild(keepHwnd: false);
        }
    }

    void Flash(string text)
    {
        if (_shown) _ov.Toast(text);
        Log.Write("flash", new { sid = _sid, text = _test ? text : null });
    }

    // ---------- actions ----------

    /// <param name="via">"key" for Ctrl+number, "drag" for a card dropped on a group.</param>
    void Regroup(int num, string via)
    {
        var it = Sections.ItemAt(_secs, _sel);
        var g = _rules.Current.ByNum(num);
        if (it == null) return;
        if (g == null) { Flash($"There is no group {num}"); return; }
        if (it.P.Group == g) { Flash($"Already in {g.Name}"); return; }

        // A tab moves on its own: a rule for Windows Terminal would move every terminal.
        var (kind, key) = it.W.Tab != null ? ("tab", (string?)null) : Rules.MoveScope(it.P, _rules.Facts(it.W));
        string message;
        string? error = null;
        if (kind is not ("window" or "tab") && key != null)
        {
            error = Rules.SaveRule(Paths.Rules, kind, key, g.Key, kind == "webApp" ? it.W.Title : null);
            if (error == null)
            {
                _rules.Load(); // don't wait for the file watcher: the view should change now
                message = kind == "app" ? $"Saved: {it.W.Exe} windows go to {g.Name}." : $"Saved: this web app goes to {g.Name}.";
            }
            else
            {
                _rules.Moved[WinKey.Of(it.W)] = g.Key;
                message = $"Moved this window to {g.Name} until it closes. {error}.";
            }
        }
        else if (kind == "tab")
        {
            _rules.Moved[WinKey.Of(it.W)] = g.Key;
            message = $"Moved this tab to {g.Name} until it closes.";
        }
        else
        {
            _rules.Moved[WinKey.Of(it.W)] = g.Key;
            message = $"Moved this window to {g.Name} until it closes. A title or tab isn't steady enough to save as a rule.";
        }
        Log.Write("regroup", new { sid = _sid, via, kind, saved = kind is not ("window" or "tab") && error == null, group = g.Key, exe = it.W.Exe, error });
        Reclassify();
        _shotName = "regroup";
        Rebuild(keepHwnd: true);
        Flash(message);
    }

    /// <param name="via">"del", "button" (the card's close button) or "middle_click".</param>
    void CloseSelected(string via)
    {
        var it = Sections.ItemAt(_secs, _sel);
        if (it == null) return;
        var flat = Sections.Flat(_secs);
        int idx = Sections.IndexOf(flat, _sel);
        if (it.W.Tab is TermTab tab && _terms != null)
        {
            // The tab's own close button, through UI Automation, so the window's other tabs stay open.
            int sid = _sid;
            _terms.CloseTab(it.W.Hwnd, tab, ok =>
            {
                Log.Write("close_tab", new { sid, via, ok, title = _test ? it.W.Title : null });
                if (!ok && _active && sid == _sid) Recollect("This tab didn't close.");
            });
            var key = WinKey.Of(it.W);
            _items = _items.Where(i => WinKey.Of(i.W) != key).ToList();
        }
        else
        {
            // The same as the window's close button: the app may still ask to save.
            N.PostMessage(it.W.Hwnd, N.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            Log.Write("close_window", new { sid = _sid, via, exe = it.W.Exe, title = _test ? it.W.Title : null });
            _items = _items.Where(i => i.W.Hwnd != it.W.Hwnd).ToList();
            _closing.Add((_sid, it.W.Hwnd));
            _closeCheck.Stop();
            _closeCheck.Start();
        }
        _secs = Build();
        flat = Sections.Flat(_secs);
        _sel = flat.Count == 0 ? null : flat[Math.Clamp(idx, 0, flat.Count - 1)];
        if (_shown) Show();
    }

    /// <summary>A window asked to close that still shows, usually asking whether to save, gets its card back.</summary>
    void CheckClosed()
    {
        var open = _closing.Where(c => c.sid == _sid && N.IsWindow(c.hwnd) && N.IsWindowVisible(c.hwnd) && !Win.IsCloaked(c.hwnd)).Select(c => c.hwnd).ToList();
        _closing.Clear();
        if (!_active || open.Count == 0) return;
        Recollect(null);
        string? exe = _items.FirstOrDefault(i => open.Contains(i.W.Hwnd))?.W.Exe;
        Log.Write("close_refused", new { sid = _sid, n = open.Count, exe });
        Flash(open.Count == 1 && exe != null
            ? $"{exe} is still open. It may be asking whether to save."
            : $"{open.Count} windows are still open. They may be asking whether to save.");
    }

    void Recollect(string? message)
    {
        _items = Collect(out _hidden, out _);
        Rebuild(keepHwnd: true);
        if (message != null) Flash(message);
    }

    void Direct(HookEvent e)
    {
        if (_active) return;
        var items = Collect(out _, out _);
        var g = _rules.Current.ByNum(e.Num);
        if (g == null) { _ov.Toast($"There is no group {e.Num}"); return; }
        var target = Sections.Direct(items, g);
        if (target == null) { _ov.Toast($"No other window in {g.Name}"); Log.Write("direct", new { num = e.Num, ok = false }); return; }
        var r = Activator.Activate(target.W.Hwnd, KeyboardHook.Magic);
        Log.Write("direct", new { num = e.Num, ok = r.Ok, method = r.Method, act_ms = Math.Round(r.Ms, 2), exe = target.W.Exe, title = _test ? target.W.Title : null });
    }

    void Commit(string how)
    {
        if (!_active) return;
        var it = Sections.ItemAt(_secs, _sel);
        if (it == null) { Cancel("nothing_to_commit"); return; }
        bool quick = !_shown, overview = _overview;
        // A click, a quick tap and a search say it all; otherwise the last way the selection moved.
        string pick = how == "click" ? "mouse" : quick ? "quick" : _filter.Length > 0 ? "search" : _pick;
        int index = Sections.IndexOf(Sections.Flat(_secs), _sel);
        string section = _sel!.Value.Section;
        _activating = it.W.Hwnd;
        End();
        var r = Activator.Activate(it.W.Hwnd, KeyboardHook.Magic, altReleased: how == "alt_up");
        Log.Write("switch", new
        {
            sid = _sid, how, quick, overview, index, section, ok = r.Ok, method = r.Method, act_ms = Math.Round(r.Ms, 2), was_min = r.WasMinimized,
            exe = it.W.Exe, group = it.P.Group?.Key, hwnd = _test ? it.W.Hwnd.ToInt64() : 0, title = _test ? it.W.Title : null,
            from_key_ms = Math.Round(Clock.Between(_startTs, Stopwatch.GetTimestamp()), 2),
            tab = it.W.Tab == null ? null : it.W.HiddenTab ? "hidden" : "front", agent = it.W.Agent?.State,
            pick, nav = new Dictionary<string, int>(_nav),
        });
        // The window first, so the switch feels instant; then its tab, off the UI thread.
        if (r.Ok && it.W.HiddenTab && _terms != null)
        {
            int sid = _sid;
            _terms.SelectTab(it.W.Hwnd, it.W.Tab!, (ok, ms) => Log.Write("tab_select", new { sid, ok, ms = Math.Round(ms, 2) }));
        }
        if (!r.Ok)
        {
            // Class and process of what kept the foreground, never its title.
            IntPtr fg = N.GetForegroundWindow();
            Log.Write("switch_failed", new { sid = _sid, exe = it.W.Exe, fg = Win.ProcessName(Win.Pid(fg)), fg_class = Win.Class(fg), menu = Win.MenuFlags(fg), idle_ms = Win.IdleMs() });
        }
    }

    public void Cancel(string why)
    {
        if (!_active) return;
        End();
        Log.Write("cancel", new { sid = _sid, why });
    }

    void End()
    {
        _showTimer.Stop();
        _ov.HideOverlay();
        _hook?.EndSession();
        _active = false;
        _shown = false;
        _overview = false;
        ArmTrim();
    }
}
