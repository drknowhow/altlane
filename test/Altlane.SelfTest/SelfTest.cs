using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Altlane;

sealed class AbortException(string why) : Exception(why);

/// <summary>
/// Drives the real Altlane.exe (in --test mode, where it sees only the test windows) with injected keys,
/// and checks where focus lands and what the overlay showed against the driver's own model of recency
/// and groups. Safety as in the phase 0 spike: starts only after the user has been idle, stops at the
/// first real key or mouse event, never runs with an elevated window in front, releases every key it
/// holds, and stops only the processes it started.
/// </summary>
static partial class SelfTest
{
    static readonly IntPtr Drv = (IntPtr)0x44525652; // "DRVR": Altlane treats these as the user's keys
    const string ReadyEvent = "Altlane.Test.Ready", ControlTitle = "Altlane.Control.Test";

    /// <summary>The test rules' groups, in number order, and where each test window starts.</summary>
    static readonly string[] GroupOrder = { "agents", "term", "web", "create", "files", "docs", "other" };
    static readonly Dictionary<string, string> HomeGroup = new()
    {
        ["alpha"] = "agents", ["bravo"] = "agents", ["charlie"] = "term", ["delta"] = "create", ["echo"] = "files", ["foxtrot"] = "other",
    };

    static volatile string? _abort;
    static readonly HashSet<ushort> Held = new();
    static readonly List<IntPtr> Mru = new();             // the driver's model of recency, most recent first
    static readonly Dictionary<IntPtr, string> Grp = new(); // the driver's model of each window's group
    static readonly Dictionary<string, IntPtr> W = new();
    static readonly List<Dictionary<string, object?>> Rows = new();
    static readonly List<(string name, double t0, double t1)> Phases = new();
    static readonly Dictionary<string, HashSet<string>> NativeSigs = new() { ["alt_tab"] = new(), ["task_view"] = new() };
    static IntPtr _overlay;
    static string _dir = "", _cfg = "";
    static Process? _sw, _tw;

    public static int Run(Dictionary<string, string> o)
    {
        Activator.timeBeginPeriod(1); // 1 ms sleeps, so the timings mean something
        _dir = o.GetValueOrDefault("log-dir") ?? Path.Combine(RepoRoot() ?? AppContext.BaseDirectory, "logs", "selftest-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(_dir);
        Log.Init(Path.Combine(_dir, "driver.jsonl"), "driver");

        string? app = o.GetValueOrDefault("app") ?? FindApp();
        _theme = o.GetValueOrDefault("theme");
        _only = o.GetValueOrDefault("only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? refuse = app == null || !File.Exists(app) ? "Altlane.exe not found: build src/Altlane in Release, or pass --app" : Refusal();
        if (refuse != null) { Log.Write("refused", new { why = refuse }); Log.Flush(); return 4; }

        double idleNeed = o.Num("idle-ms", 8000), idleWait = o.Num("idle-wait-s", 600);
        Log.Write("waiting_for_idle", new { need_ms = idleNeed, max_s = idleWait });
        if (!WaitIdle(idleNeed, idleWait)) { Log.Write("gave_up", new { why = "user never idle long enough" }); Log.Flush(); return 3; }

        IntPtr original = N.GetForegroundWindow();
        Log.Write("start_fg", new { fg = Sig(original), idle_ms = Win.IdleMs() });
        string result = "completed";
        StartGuard();
        try
        {
            WriteConfig();
            StartTestWindows();
            Phase("T0_calibrate", T0);
            StartSwitcher(app!);
            Phase("T1_quick_tap", T1);
            Phase("T2_hold_cycle", T2);
            Phase("T3_group_keys", T3);
            Phase("T13_columns", T13); // beside T3: the same keys in the other layout
            Phase("T4_filter_minimized", T4);
            Phase("T5_esc", T5);
            Phase("T6_same_group", T6);
            Phase("T7_direct_jump", T7);
            Phase("T8_regroup", T8);
            Phase("T9_close", T9);
            Phase("T10_watchdog", T10);
            Phase("T11_win_tab_lanes", T11);
            Phase("T14_mouse_and_picks", T14);
            Phase("T15_drag_and_middle_click", T15);
            Phase("T16_terminal_tabs", T16);
            Phase("T18_close_button", T18);
            Phase(ManyPhase, T17);
            Phase("T12_start_open", T12);
            Phase("C_detector_controls", Controls); // last: Start can hold the foreground for a while after it closes
        }
        catch (AbortException e) { result = "aborted: " + e.Message; }
        catch (Exception e) { result = "error: " + e; }
        finally
        {
            foreach (var vk in Held.ToList()) Keys.Send(Keys.Key(vk, true, Drv));
            Held.Clear();
            StopGuard();
            StopChildren();
            // Give focus back only after a clean run; after an abort the user is already at the keyboard.
            if (result == "completed" && N.IsWindow(original)) Activator.Activate(original, Drv);
        }
        Log.Write("driver_done", new { result });
        Report(result);
        Log.Flush();
        return result == "completed" ? 0 : 1;
    }

    // ---------- calibration and controls ----------

    /// <summary>What the native Alt+Tab and Task View look like on this PC, before Altlane is running.</summary>
    static void T0()
    {
        Activate(W["alpha"]);
        var before = Visible();
        Down(N.VK_LMENU); Pause(30); Tap(N.VK_TAB); Pause(600);
        Calibrate("alt_tab", before);
        Tap(N.VK_ESCAPE); Pause(40); Up(N.VK_LMENU); Pause(400);

        before = Visible();
        Down(N.VK_LWIN); Pause(30); Tap(N.VK_TAB); Pause(30); Up(N.VK_LWIN); Pause(1000);
        Calibrate("task_view", before);
        Tap(N.VK_ESCAPE); Pause(800);
        if (N.GetForegroundWindow() != W["alpha"]) Activate(W["alpha"]);
    }

    static void Calibrate(string what, Dictionary<IntPtr, string> before)
    {
        string fg = Sig(N.GetForegroundWindow());
        var appeared = Visible().Where(kv => !before.ContainsKey(kv.Key)).Select(kv => kv.Value).Distinct().ToList();
        foreach (var s in appeared.Append(fg).Where(s => !s.EndsWith("|Altlane.SelfTest") && !s.EndsWith("|Altlane"))) NativeSigs[what].Add(s);
        Row("T0", 0, new() { ["what"] = what, ["fg"] = fg, ["appeared"] = appeared, ["start_open"] = StartOpen() });
    }

    /// <summary>
    /// Positive controls, with no switcher session open: a stray key must reach the test window, a lone
    /// Alt must put it in menu mode, a lone Win must open Start. If these don't fire, the zero-leak and
    /// zero-menu results prove nothing.
    /// </summary>
    static void Controls()
    {
        Activate(LiveMru().First());
        Tap('Z'); Pause(200);
        Down(N.VK_LMENU); Pause(30); Up(N.VK_LMENU); Pause(300);
        uint menu = Win.MenuFlags(N.GetForegroundWindow());
        Row("C", 0, new() { ["what"] = "alt_alone", ["ok"] = menu != 0, ["menu_flags"] = menu });
        if (menu != 0) { Tap(N.VK_ESCAPE); Pause(200); }
        Down(N.VK_LWIN); Pause(30); Up(N.VK_LWIN);
        bool start = WatchStart(1500);
        Row("C", 1, new() { ["what"] = "win_alone", ["ok"] = start });
        if (start) { Tap(N.VK_ESCAPE); var sw = Stopwatch.StartNew(); while (StartOpen() && sw.ElapsedMilliseconds < 3000) Pause(50); }
    }

    // ---------- Alt+Tab ----------

    /// <summary>Quick tap: Alt+Tab released fast goes to the previous window with nothing drawn.</summary>
    static void T1()
    {
        Reset();
        for (int i = 0; i < 10; i++) Switch("T1", i, Mru[1], QuickTap(N.VK_TAB), section: Sections.RecentKey, index: 0);
    }

    /// <summary>Hold and press Tab k times: the k-th card in reading order (Recent, then groups 1..n).</summary>
    static void T2()
    {
        Reset();
        for (int i = 0; i < 10; i++)
        {
            int k = i + 1;
            var flat = ExpectedFlat();
            int at = (k - 1) % flat.Count;
            string view = ViewString(flat);
            Switch("T2", i, flat[at].h, before =>
            {
                Down(N.VK_LMENU); Pause(25);
                for (int j = 0; j < k; j++) { Tap(N.VK_TAB, 20); Pause(45); }
                Pause(220);
                var mid = NativeCheck(before);
                bool ov = Shown(_overlay);
                Up(N.VK_LMENU);
                return new() { ["k"] = k, ["must_overlay_mid"] = ov, ["native_mid"] = mid };
            }, section: flat[at].sec, index: at, logCheck: (ev, row) => (row["view"] = ViewOf(ev.LastOrDefault(e => Ev(e) == "view"))) as string == view);
        }
    }

    /// <summary>With the view open: number keys jump to a group, ` and arrows move between sections and cards.</summary>
    static void T3()
    {
        Reset("charlie", "alpha", "bravo", "delta", "echo", "foxtrot");
        var steps = new (string what, Action press, Func<List<(string sec, IntPtr h)>, (string sec, IntPtr h)> want)[]
        {
            ("4_create", () => Tap('4'), f => f.First(x => x.sec == "create")),
            ("7_other", () => Tap('7'), f => f.First(x => x.sec == "other")),
            ("3_empty_web", () => Tap('3'), f => f[0]),
            ("grave", () => Tap(N.VK_OEM_3), f => f.First(x => x.sec != Sections.RecentKey)),
            ("shift_grave", () => { Down(N.VK_LSHIFT); Pause(20); Tap(N.VK_OEM_3); Pause(20); Up(N.VK_LSHIFT); }, f => f.First(x => x.sec == f[^1].sec)),
            ("right", () => Tap(N.VK_RIGHT), f => f[1]),
            ("left", () => Tap(N.VK_LEFT), f => f[^1]),
            ("down", () => Tap(N.VK_DOWN), f => f.First(x => x.sec != Sections.RecentKey)),
            ("up", () => Tap(N.VK_UP), f => f.First(x => x.sec == f[^1].sec)),
        };
        for (int i = 0; i < steps.Length; i++)
        {
            var (what, press, want) = steps[i];
            var flat = ExpectedFlat();
            var t = want(flat);
            Switch("T3", i, t.h, _ =>
            {
                Down(N.VK_LMENU); Pause(25); Tap(N.VK_TAB, 20); Pause(220);
                press(); Pause(80);
                Up(N.VK_LMENU);
                return new() { ["what"] = what };
            }, section: t.sec, index: flat.IndexOf(t),
            logCheck: what == "3_empty_web" ? (ev, row) => (row["flash"] = Str(ev.LastOrDefault(e => Ev(e) == "flash"), "text")) is string s && s.StartsWith("Group 3") : null);
        }
    }

    /// <summary>
    /// The columns layout: the same sections and Tab order as the grid, with Down and Up moving within a
    /// column (stopping at its ends) and Right and Left moving across columns at the same row (wrapping).
    /// </summary>
    static void T13()
    {
        SetLayout(columns: true);
        try
        {
            Reset("charlie", "alpha", "bravo", "delta", "echo", "foxtrot");
            var steps = new (string what, ushort[] keys)[]
            {
                ("tab", new[] { N.VK_TAB }),
                ("down", new[] { N.VK_DOWN }),
                ("up_at_top_stays", new[] { N.VK_UP }),
                ("down_x4_stops_at_foot", new[] { N.VK_DOWN, N.VK_DOWN, N.VK_DOWN, N.VK_DOWN }),
                ("right", new[] { N.VK_RIGHT }),
                ("left_wraps_to_last", new[] { N.VK_LEFT }),
                ("down_right_keeps_row", new[] { N.VK_DOWN, N.VK_RIGHT }),
                ("right_down_left", new[] { N.VK_RIGHT, N.VK_DOWN, N.VK_LEFT }),
            };
            for (int i = 0; i < steps.Length; i++)
            {
                var (what, keys) = steps[i];
                var flat = ExpectedFlat();
                var t = ColumnMoves(flat, keys);
                string view = ViewString(flat);
                Switch("T13", i, t.h, _ =>
                {
                    Down(N.VK_LMENU); Pause(25); Tap(N.VK_TAB, 20); Pause(220);
                    foreach (var k in keys) { Tap(k); Pause(60); }
                    Pause(40);
                    Up(N.VK_LMENU);
                    return new() { ["what"] = what };
                }, section: t.sec, index: flat.IndexOf(t), logCheck: (ev, row) =>
                {
                    var v = ev.LastOrDefault(e => Ev(e) == "view");
                    row["view"] = ViewOf(v);
                    row["layout"] = Str(v, "layout");
                    return (string?)row["view"] == view && (string?)row["layout"] == "columns";
                });
            }
        }
        finally { SetLayout(columns: false); }
    }

    /// <summary>
    /// The driver's own model of the columns layout's keys, from <paramref name="start"/>: the first card,
    /// where Alt+Tab opens, unless given. Number keys go to a group's first card, ` to the next column's.
    /// </summary>
    static (string sec, IntPtr h) ColumnMoves(List<(string sec, IntPtr h)> flat, ushort[] keys, (string sec, IntPtr h)? start = null)
    {
        var cols = flat.GroupBy(x => x.sec).Select(g => g.ToList()).ToList();
        int c = start is { } s0 ? cols.FindIndex(col => col.Contains(s0)) : 0;
        int r = start is { } s1 ? cols[c].IndexOf(s1) : 0;
        foreach (var k in keys)
        {
            if (k == N.VK_TAB)
            {
                var next = flat[(flat.IndexOf(cols[c][r]) + 1) % flat.Count];
                c = cols.FindIndex(col => col.Contains(next));
                r = cols[c].IndexOf(next);
            }
            else if (k == N.VK_OEM_3) { c = (c + 1) % cols.Count; r = 0; }
            else if (k is >= '1' and <= '9')
            {
                int g = k - '1' < GroupOrder.Length ? cols.FindIndex(col => col[0].sec == GroupOrder[k - '1']) : -1;
                if (g >= 0) { c = g; r = 0; }
            }
            else if (k is N.VK_DOWN or N.VK_UP) r = Math.Clamp(r + (k == N.VK_DOWN ? 1 : -1), 0, cols[c].Count - 1);
            else { c = (c + (k == N.VK_RIGHT ? 1 : -1) + cols.Count) % cols.Count; r = Math.Min(r, cols[c].Count - 1); }
        }
        return cols[c][r];
    }

    /// <summary>Switch Altlane's layout through its test-only control message. Not saved to settings.json.</summary>
    static void SetLayout(bool columns)
    {
        IntPtr ctl = N.FindWindowEx(N.HWND_MESSAGE, IntPtr.Zero, null, ControlTitle);
        N.PostMessage(ctl, (int)N.RegisterWindowMessage("Altlane.Test.Layout"), (IntPtr)(columns ? 1 : 0), IntPtr.Zero);
        Pause(150);
        Log.Write("layout_set", new { columns, ctl = ctl.ToInt64() });
    }

    /// <summary>Type to filter onto a minimized window: letters pin the view after Alt is released; Enter commits.</summary>
    static void T4()
    {
        Reset();
        var cases = new[] { ("ech", "echo", "files"), ("fox", "foxtrot", "other"), ("files", "echo", "files") };
        for (int i = 0; i < cases.Length; i++)
        {
            var (typed, name, sec) = cases[i];
            IntPtr target = W[name];
            if (!N.IsIconic(target))
            {
                if (N.GetForegroundWindow() == target) Front(LiveMru().First(h => h != target));
                N.ShowWindowAsync(target, N.SW_MINIMIZE); Pause(400);
            }
            Switch("T4", i, target, _ =>
            {
                Down(N.VK_LMENU); Pause(25); Tap(N.VK_TAB, 20); Pause(40);
                foreach (char c in typed.ToUpperInvariant()) { Tap(c, 20); Pause(30); }
                Up(N.VK_LMENU); Pause(150);
                bool pinned = Shown(_overlay);
                Tap(N.VK_RETURN);
                return new() { ["typed"] = typed, ["must_pinned_after_alt_up"] = pinned };
            }, section: sec, index: 0, logCheck: (ev, row) =>
            {
                // Every filtered view must select a window other than the one in front: Enter there would go nowhere.
                var views = ev.Where(e => Ev(e) == "view" && Str(e, "filter") is { Length: > 0 }).ToList();
                long cur = LiveMru()[0].ToInt64();
                row["filter_sel"] = string.Join(",", views.Select(v => v.TryGetProperty("sel", out var x) && x.ValueKind == JsonValueKind.Number ? Name((IntPtr)x.GetInt64()) : "-"));
                return views.Any(v => Str(v, "filter") == typed)
                       && views.All(v => !v.TryGetProperty("sel", out var x) || x.ValueKind != JsonValueKind.Number || x.GetInt64() != cur);
            });
        }
    }

    /// <summary>Esc cancels; with a filter typed, the first Esc clears it and the second cancels.</summary>
    static void T5()
    {
        Reset();
        for (int i = 0; i < 3; i++)
            NoSwitch("T5", i, "esc", () =>
            {
                Down(N.VK_LMENU); Pause(25); Tap(N.VK_TAB, 20); Pause(220);
                bool mid = Shown(_overlay);
                Tap(N.VK_ESCAPE); Pause(40); Up(N.VK_LMENU);
                return new() { ["must_overlay_mid"] = mid };
            }, ev => ev.Any(e => Ev(e) == "cancel" && Str(e, "why") == "esc"));

        for (int i = 3; i < 5; i++)
            NoSwitch("T5", i, "esc_filter", () =>
            {
                Down(N.VK_LMENU); Pause(25); Tap(N.VK_TAB, 20); Pause(40);
                Tap('Z'); Pause(30); Tap('Z'); Pause(30);
                Up(N.VK_LMENU); Pause(150);
                bool pinned = Shown(_overlay);
                Tap(N.VK_ESCAPE); Pause(200);
                bool still = Shown(_overlay);
                Tap(N.VK_ESCAPE);
                return new() { ["must_pinned"] = pinned, ["must_open_after_first_esc"] = still };
            }, ev =>
            {
                int zz = ev.FindIndex(e => Ev(e) == "view" && Str(e, "filter") == "zz");
                return zz >= 0 && ev.Skip(zz).Any(e => Ev(e) == "view" && Str(e, "filter") == "")
                       && ev.Any(e => Ev(e) == "cancel" && Str(e, "why") == "esc");
            });
    }

    // ---------- Alt+` ----------

    /// <summary>Alt+` stays in the current window's group; a group of one says so; Tab widens to every window.</summary>
    static void T6()
    {
        Reset("alpha", "bravo", "charlie", "delta", "echo", "foxtrot");
        for (int i = 0; i < 4; i++)
            Switch("T6", i, OtherIn("agents"), QuickTap(N.VK_OEM_3), section: "agents", index: 1);

        Switch("T6", 4, OtherIn("agents"), _ =>
        {
            Down(N.VK_LMENU); Pause(25); Tap(N.VK_OEM_3, 20); Pause(220);
            bool ov = Shown(_overlay);
            Up(N.VK_LMENU);
            return new() { ["what"] = "hold", ["must_overlay_mid"] = ov };
        }, section: "agents", index: 1, logCheck: (ev, row) =>
            ev.LastOrDefault(e => Ev(e) == "view") is { ValueKind: JsonValueKind.Object } v && Str(v, "scope") == "agents"
            && (row["view"] = ViewOf(v)) as string == "agents:" + string.Join(",", LiveMru().Where(h => Grp[h] == "agents").Select(Name)));

        Reset("charlie", "alpha", "bravo", "delta", "echo", "foxtrot");
        NoSwitch("T6", 5, "group_of_one", () =>
        {
            Down(N.VK_LMENU); Pause(25); Tap(N.VK_OEM_3, 20); Pause(40); Up(N.VK_LMENU); Pause(150);
            return new() { ["must_toast"] = Shown(_overlay) };
        }, ev => ev.Any(e => Ev(e) == "same_group_empty"));
        WaitOverlayGone();

        Reset("alpha", "bravo", "charlie", "delta", "echo", "foxtrot");
        string all = ViewString(ExpectedFlat());
        NoSwitch("T6", 6, "grave_then_tab", () =>
        {
            Down(N.VK_LMENU); Pause(25); Tap(N.VK_OEM_3, 20); Pause(220);
            Tap(N.VK_TAB); Pause(150);
            Tap(N.VK_ESCAPE); Pause(40); Up(N.VK_LMENU);
            return new();
        }, ev =>
        {
            int scoped = ev.FindIndex(e => Ev(e) == "view" && Str(e, "scope") == "agents");
            return scoped >= 0 && ev.Skip(scoped).Any(e => Ev(e) == "view" && Str(e, "scope") == null && ViewOf(e) == all);
        });
    }

    // ---------- Alt+number ----------

    /// <summary>Alt+number goes straight to the most recent window in that group, skipping the one in front.</summary>
    static void T7()
    {
        Reset("charlie", "alpha", "bravo", "delta", "echo", "foxtrot");
        Switch("T7", 0, W["delta"], QuickTap('4'), logCheck: DirectOk);
        Switch("T7", 1, OtherIn("agents"), QuickTap('1'), logCheck: DirectOk);
        Switch("T7", 2, OtherIn("agents"), QuickTap('1'), logCheck: DirectOk);
        NoSwitch("T7", 3, "empty_group", () =>
        {
            var keys = QuickTap('3')(new());
            Pause(150);
            keys["must_toast"] = Shown(_overlay);
            return keys;
        }, ev => ev.Any(e => Ev(e) == "direct" && e.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False));
        WaitOverlayGone();
    }

    static bool DirectOk(List<JsonElement> ev, Dictionary<string, object?> row) =>
        ev.Any(e => Ev(e) == "direct" && e.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True);

    // ---------- Ctrl+number, Del ----------

    /// <summary>
    /// Ctrl+number on a window placed by a title rule moves only that window and leaves rules.json alone;
    /// on a window placed by no rule it saves an app rule.
    /// </summary>
    static void T8()
    {
        Reset();
        string rulesPath = Path.Combine(_cfg, "rules.json");
        string before = File.ReadAllText(rulesPath);
        IntPtr delta = W["delta"], fox = W["foxtrot"];

        Grp[delta] = "agents"; // what the view should show once the move is made
        string expectA = ViewString(ExpectedFlat());
        NoSwitch("T8", 0, "ctrl1_on_title_rule_window", () =>
        {
            Down(N.VK_LMENU); Pause(25); Tap(N.VK_TAB, 20); Pause(220);
            Tap('4'); Pause(60);
            Down(N.VK_LCONTROL); Pause(20); Tap('1'); Pause(20); Up(N.VK_LCONTROL); Pause(300);
            Tap(N.VK_ESCAPE); Pause(40); Up(N.VK_LMENU);
            return new() { ["must_rules_unchanged"] = File.ReadAllText(rulesPath) == before };
        }, ev =>
            ev.Any(e => Ev(e) == "regroup" && Str(e, "kind") == "window" && Str(e, "group") == "agents")
            && ViewOf(ev.LastOrDefault(e => Ev(e) == "view")) == expectA);

        Grp[fox] = "docs";
        string expectB = ViewString(ExpectedFlat());
        NoSwitch("T8", 1, "ctrl6_on_unruled_window", () =>
        {
            Down(N.VK_LMENU); Pause(25); Tap(N.VK_TAB, 20); Pause(220);
            Tap('7'); Pause(60);
            Down(N.VK_LCONTROL); Pause(20); Tap('6'); Pause(20); Up(N.VK_LCONTROL); Pause(500);
            Tap(N.VK_ESCAPE); Pause(40); Up(N.VK_LMENU);
            string? saved = null;
            try { saved = JsonDocument.Parse(File.ReadAllText(rulesPath)).RootElement.GetProperty("apps").GetProperty("Altlane.SelfTest.exe").GetString(); }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or IOException) { }
            return new() { ["saved_rule"] = saved, ["must_saved_docs"] = saved == "docs" };
        }, ev =>
            ev.Any(e => Ev(e) == "regroup" && Str(e, "kind") == "app" && Str(e, "group") == "docs")
            && ViewOf(ev.LastOrDefault(e => Ev(e) == "view")) == expectB);
    }

    /// <summary>Del closes the selected window the way its close button would, and the view drops it.</summary>
    static void T9()
    {
        Reset();
        IntPtr fox = W["foxtrot"];
        NoSwitch("T9", 0, "del_closes", () =>
        {
            Down(N.VK_LMENU); Pause(25); Tap(N.VK_TAB, 20); Pause(220);
            Tap('6'); Pause(60); // foxtrot is in Docs since T8
            Tap(N.VK_DELETE); Pause(400);
            Tap(N.VK_ESCAPE); Pause(40); Up(N.VK_LMENU);
            var sw = Stopwatch.StartNew();
            while (N.IsWindow(fox) && sw.ElapsedMilliseconds < 2000) Pause(20);
            return new() { ["must_closed"] = !N.IsWindow(fox) };
        }, ev =>
            ev.Any(e => Ev(e) == "close_window" && Str(e, "title") == TestWindows.Prefix + "foxtrot")
            && ViewOf(ev.LastOrDefault(e => Ev(e) == "view")) == ViewString(ExpectedFlat())); // foxtrot is gone from the live model
    }

    // ---------- watchdog ----------

    /// <summary>Remove Altlane's hook the way Windows does after a timeout; the watchdog must put it back.</summary>
    static void T10()
    {
        Reset();
        Tail();
        IntPtr ctl = N.FindWindowEx(N.HWND_MESSAGE, IntPtr.Zero, null, ControlTitle);
        N.PostMessage(ctl, (int)N.RegisterWindowMessage("Altlane.Test.DropHook"), IntPtr.Zero, IntPtr.Zero);
        Pause(2000); // the watchdog acts on a key raw input saw at least 1.5 s after the hook last saw one
        var ev = new List<JsonElement>();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && !ev.Any(e => Ev(e) == "hook_reinstalled"))
        {
            Tap(N.VK_LSHIFT); Pause(400);
            ev.AddRange(Tail());
        }
        bool dropped = ev.Any(e => Ev(e) == "hook_dropped_for_test"), back = ev.Any(e => Ev(e) == "hook_reinstalled");
        Row("T10", 0, new() { ["what"] = "reinstall", ["ok"] = dropped && back, ["dropped"] = dropped, ["reinstalled"] = back, ["wait_ms"] = sw.ElapsedMilliseconds });
        Pause(300);
        Switch("T10", 1, Mru[1], QuickTap(N.VK_TAB), section: Sections.RecentKey, index: 0);
    }

    // ---------- Win+Tab ----------

    /// <summary>
    /// Win+Tab opens the lanes overview and leaves it open once Win is up: every window once, in its group,
    /// starting on the previous window. Keys move as in the columns layout, typing searches, the mouse
    /// hovers and clicks, Esc, Win+Tab again or a click outside close it. Shift+Win+Tab, and Win+Tab with
    /// the setting off, still open Windows' Task View.
    /// </summary>
    static void T11()
    {
        N.GetCursorPos(out var home);
        // A fixed spot over the overview, so the key-only steps prove a still cursor selects nothing.
        var wa = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(home.X, home.Y)).WorkingArea;
        MouseTo(wa.Left + wa.Width / 2, wa.Top + wa.Height / 2);
        try
        {
            string[] order = { "alpha", "charlie", "bravo", "delta", "echo" };
            var steps = new (string what, ushort[] keys)[]
            {
                ("enter_goes_back", Array.Empty<ushort>()),
                ("tab", new[] { N.VK_TAB }),
                ("right", new[] { N.VK_RIGHT }),
                ("left_down", new[] { N.VK_LEFT, N.VK_DOWN }),
                ("right_x2_wraps_down_x2", new[] { N.VK_RIGHT, N.VK_RIGHT, N.VK_DOWN, N.VK_DOWN }),
                ("down_at_foot_stays", new[] { N.VK_DOWN }),
                ("grave", new[] { N.VK_OEM_3 }),
                ("5_files", new[] { (ushort)'5' }),
                ("1_down_x3_up", new[] { (ushort)'1', N.VK_DOWN, N.VK_DOWN, N.VK_DOWN, N.VK_UP }),
            };
            int i = 0;
            foreach (var (what, keys) in steps)
            {
                Reset(order);
                var flat = ExpectedLanes();
                var first = flat.First(x => x.h == Mru[1]);
                var want = ColumnMoves(flat, keys, first);
                string view = ViewString(flat);
                Switch("T11", i++, want.h, _ =>
                {
                    WinTab(); Pause(250);
                    var row = OpenChecks(what);
                    foreach (var k in keys) { Tap(k); Pause(60); }
                    Pause(40);
                    Tap(N.VK_RETURN);
                    return row;
                }, section: want.sec, index: flat.IndexOf(want), logCheck: (ev, row) => LanesOpened(ev, row, view, first.h) && NoHover(ev, row));
            }

            Reset(order);
            Switch("T11", i++, W["echo"], _ =>
            {
                WinTab(); Pause(250);
                var row = OpenChecks("search");
                foreach (char c in "ECH") { Tap(c, 20); Pause(40); }
                Pause(150);
                row["must_open_while_searching"] = Shown(_overlay);
                Tap(N.VK_RETURN);
                return row;
            }, section: "files", index: 0, logCheck: (ev, row) =>
            {
                var v = ev.LastOrDefault(e => Ev(e) == "view");
                row["last_view"] = ViewOf(v);
                return Str(v, "filter") == "ech" && Str(v, "layout") == "lanes" && (string?)row["last_view"] == "files:echo" && NoHover(ev, row);
            });

            Reset(order);
            NoSwitch("T11", i++, "esc_closes", () =>
            {
                WinTab(); Pause(250);
                var row = OpenChecks("esc");
                Tap(N.VK_ESCAPE);
                return row;
            }, ev => ev.Any(e => Ev(e) == "cancel" && Str(e, "why") == "esc"));

            NoSwitch("T11", i++, "esc_clears_search_then_closes", () =>
            {
                WinTab(); Pause(250);
                var row = OpenChecks("esc_search");
                Tap('Z'); Pause(30); Tap('Z'); Pause(150);
                Tap(N.VK_ESCAPE); Pause(200);
                row["must_open_after_first_esc"] = Shown(_overlay);
                Tap(N.VK_ESCAPE);
                return row;
            }, ev =>
            {
                int zz = ev.FindIndex(e => Ev(e) == "view" && Str(e, "filter") == "zz");
                return zz >= 0 && ev.Skip(zz).Any(e => Ev(e) == "view" && Str(e, "filter") == "")
                       && ev.Any(e => Ev(e) == "cancel" && Str(e, "why") == "esc");
            });

            NoSwitch("T11", i++, "win_tab_again_closes", () =>
            {
                WinTab(); Pause(250);
                var row = OpenChecks("toggle");
                WinTab(); Pause(100);
                return row;
            }, ev => ev.Any(e => Ev(e) == "cancel" && Str(e, "why") == "win_tab"));

            // The mouse: hover selects, a click on a card switches, a click anywhere else closes.
            Reset(order);
            IntPtr hoverTo = Mru[2];
            var seen = new List<JsonElement>();
            Switch("T11", i++, hoverTo, _ =>
            {
                WinTab(); Pause(250);
                var row = OpenChecks("hover");
                row["must_cursor_on_card"] = PointAt(hoverTo, seen);
                Pause(150);
                Tap(N.VK_RETURN);
                return row;
            }, logCheck: (ev, row) =>
            {
                var all = seen.Concat(ev).ToList();
                row["hovered"] = string.Join(",", all.Where(e => Ev(e) == "hover").Select(e => Name((IntPtr)e.GetProperty("sel").GetInt64())));
                return all.Any(e => Ev(e) == "hover" && e.GetProperty("sel").GetInt64() == hoverTo.ToInt64())
                       && Str(all.LastOrDefault(e => Ev(e) == "switch"), "how") == "enter";
            });

            Reset(order);
            IntPtr clickOn = Mru[3];
            var seen2 = new List<JsonElement>();
            Switch("T11", i++, clickOn, _ =>
            {
                WinTab(); Pause(250);
                var row = OpenChecks("click_card");
                row["must_cursor_on_card"] = PointAt(clickOn, seen2);
                Pause(100);
                Click();
                return row;
            }, logCheck: (ev, row) => Str(seen2.Concat(ev).LastOrDefault(e => Ev(e) == "switch"), "how") == "click");

            Reset(order);
            NoSwitch("T11", i++, "click_outside_closes", () =>
            {
                WinTab(); Pause(250);
                var row = OpenChecks("click_outside");
                N.GetWindowRect(_overlay, out var r);
                MouseTo(r.Left + 12, r.Top + 12); Pause(80);
                Click();
                return row;
            }, ev => ev.Any(e => Ev(e) == "cancel" && Str(e, "why") == "click_outside"));
        }
        finally { MouseTo(home.X, home.Y); }

        // Task View stays one key away.
        TaskView(0, "shift_win_tab", () => { Down(N.VK_LSHIFT); Pause(20); WinTab(); Pause(20); Up(N.VK_LSHIFT); },
            ev => ev.Any(e => Ev(e) == "task_view") && !ev.Any(e => Ev(e) == "session_start"));
        TaskView(1, "shift_win_tab_from_overview", () =>
        {
            WinTab(); Pause(250);
            if (!Shown(_overlay)) Log.Write("overview_not_open", new { });
            Down(N.VK_LSHIFT); Pause(20); WinTab(); Pause(20); Up(N.VK_LSHIFT);
        }, ev => ev.Any(e => Ev(e) == "session_start" && Str(e, "kind") == "overview") && ev.Any(e => Ev(e) == "task_view")
            // Task View taking the foreground can reach Altlane before the hook's own event does; either one closes the overview.
            && ev.Any(e => Ev(e) == "cancel" && Str(e, "why") is "shift_win_tab" or "foreground_changed"));
        SetWinTab(false);
        try
        {
            for (int k = 2; k < 4; k++) TaskView(k, "setting_off", WinTab, ev => !ev.Any(e => Ev(e) == "session_start"));
        }
        finally { SetWinTab(true); }
    }

    /// <summary>Win down, Tab, Win up.</summary>
    static void WinTab() { Down(N.VK_LWIN); Pause(25); Tap(N.VK_TAB, 20); Pause(25); Up(N.VK_LWIN); }

    /// <summary>Just after Win+Tab, with Win already up: the overview must be open and Start must not be.</summary>
    static Dictionary<string, object?> OpenChecks(string what) =>
        new() { ["what"] = what, ["must_open_after_win_up"] = Shown(_overlay), ["must_no_start"] = !StartOpen() };

    /// <summary>The overview's first view: groups only, the model's order, opened on the previous window.</summary>
    static bool LanesOpened(List<JsonElement> ev, Dictionary<string, object?> row, string view, IntPtr first)
    {
        var v = ev.FirstOrDefault(e => Ev(e) == "view");
        row["view"] = ViewOf(v);
        row["layout"] = Str(v, "layout");
        row["opened_on"] = v.ValueKind == JsonValueKind.Object && v.TryGetProperty("sel", out var s) && s.ValueKind == JsonValueKind.Number ? Name((IntPtr)s.GetInt64()) : null;
        return (string?)row["view"] == view && (string?)row["layout"] == "lanes" && (string?)row["opened_on"] == Name(first)
               && ev.Any(e => Ev(e) == "session_start" && Str(e, "kind") == "overview");
    }

    /// <summary>The cursor sat over the overview and never moved: a redraw must not select the card under it.</summary>
    static bool NoHover(List<JsonElement> ev, Dictionary<string, object?> row) => (row["hovers"] = ev.Count(e => Ev(e) == "hover")) is 0;

    /// <summary>Win+Tab that should reach Windows' Task View, with no overview and no Start.</summary>
    static void TaskView(int i, string what, Action keys, Func<List<JsonElement>, bool> logCheck)
    {
        EnsureFront();
        Tail();
        IntPtr fg0 = N.GetForegroundWindow();
        var before = Visible();
        keys();
        bool start = WatchStart(300);
        Pause(700);
        var native = NativeCheck(before);
        bool ov = Shown(_overlay);
        Tap(N.VK_ESCAPE); Pause(800);
        bool back = N.GetForegroundWindow() == fg0;
        bool logOk = logCheck(Tail());
        Row("T11tv", i, new() { ["what"] = what, ["ok"] = native.Contains("task_view") && !ov && !start && logOk, ["native"] = native, ["overlay"] = ov, ["start_open"] = start, ["log_ok"] = logOk, ["fg_back"] = back });
        if (!back && N.IsWindow(fg0)) Activate(fg0);
        Pause(300);
    }

    /// <summary>Switch what Win+Tab does through Altlane's test-only control message. Not saved to settings.json.</summary>
    static void SetWinTab(bool lanes)
    {
        IntPtr ctl = N.FindWindowEx(N.HWND_MESSAGE, IntPtr.Zero, null, ControlTitle);
        N.PostMessage(ctl, (int)N.RegisterWindowMessage("Altlane.Test.WinTab"), (IntPtr)(lanes ? 1 : 0), IntPtr.Zero);
        Pause(150);
        Log.Write("win_tab_set", new { lanes, ctl = ctl.ToInt64() });
    }

    // ---------- the mouse ----------

    [DllImport("user32")] static extern int GetSystemMetrics(int index);
    const uint MouseMove = 0x0001, MouseLeftDown = 0x0002, MouseLeftUp = 0x0004, MouseVirtualDesk = 0x4000, MouseAbsolute = 0x8000;

    static void SendMouse(uint flags, int dx = 0, int dy = 0)
    {
        var input = new N.INPUT { type = 0 };
        input.u.mi = new N.MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags, dwExtraInfo = Drv };
        Keys.Send(input);
    }

    /// <summary>Put the cursor on a physical pixel, as injected input so the guard knows it is ours.</summary>
    static void MouseTo(int x, int y)
    {
        Check();
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        SendMouse(MouseMove | MouseAbsolute | MouseVirtualDesk,
            (int)Math.Round((x - vx) * 65535.0 / Math.Max(1, vw - 1)), (int)Math.Round((y - vy) * 65535.0 / Math.Max(1, vh - 1)));
    }

    static void Click()
    {
        Check(); CheckTarget();
        SendMouse(MouseLeftDown); Pause(40); SendMouse(MouseLeftUp);
    }

    /// <summary>Move the cursor onto a window's card, from where Altlane says it drew it. True when it got there.</summary>
    /// <param name="tab">A terminal tab's card: its tab index.</param>
    static bool PointAt(IntPtr h, List<JsonElement> seen, int? tab = null)
    {
        var sw = Stopwatch.StartNew();
        JsonElement card = default;
        while (sw.ElapsedMilliseconds < 1500 && card.ValueKind != JsonValueKind.Object)
        {
            seen.AddRange(Tail());
            var v = seen.LastOrDefault(e => Ev(e) == "view");
            if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("cards", out var cards) && cards.ValueKind == JsonValueKind.Array)
                card = cards.EnumerateArray().FirstOrDefault(c => c.GetProperty("hwnd").GetInt64() == h.ToInt64() && (tab == null || c.GetProperty("tab").GetInt32() == tab));
            if (card.ValueKind != JsonValueKind.Object) Pause(30);
        }
        if (card.ValueKind != JsonValueKind.Object) return false;
        int x = card.GetProperty("x").GetInt32(), y = card.GetProperty("y").GetInt32(), w = card.GetProperty("w").GetInt32(), hh = card.GetProperty("h").GetInt32();
        // Two moves, so the card gets a move that changes the cursor's position.
        MouseTo(x + w / 2 - 6, y + hh / 2 - 6); Pause(40);
        MouseTo(x + w / 2, y + hh / 2); Pause(60);
        N.GetCursorPos(out var p);
        return p.X >= x && p.X < x + w && p.Y >= y && p.Y < y + hh;
    }

    /// <summary>Move the cursor onto a card's close button: the card first, which shows the button. True when it got there.</summary>
    static bool PointAtClose(IntPtr h, List<JsonElement> seen)
    {
        if (!PointAt(h, seen)) return false;
        var v = seen.LastOrDefault(e => Ev(e) == "view");
        var card = v.GetProperty("cards").EnumerateArray().First(c => c.GetProperty("hwnd").GetInt64() == h.ToInt64());
        var x = card.GetProperty("close");
        int cx = x.GetProperty("x").GetInt32() + x.GetProperty("w").GetInt32() / 2, cy = x.GetProperty("y").GetInt32() + x.GetProperty("h").GetInt32() / 2;
        MouseTo(cx - 3, cy - 2); Pause(40);
        MouseTo(cx, cy); Pause(80);
        N.GetCursorPos(out var p);
        return Math.Abs(p.X - cx) <= 1 && Math.Abs(p.Y - cy) <= 1;
    }

    /// <summary>
    /// Alt+Tab with Start open over a test window. Every way of switching fails while Start holds the
    /// foreground, so Altlane has to close it first. Nothing counts as current while Start has focus,
    /// so Recent starts with the window behind Start: a quick tap goes back to it, a second Tab goes past it.
    /// </summary>
    static void T12()
    {
        for (int i = 0; i < 4; i++)
        {
            Reset();
            bool hold = i % 2 == 1;
            Switch("T12", i, hold ? Mru[1] : Mru[0], before =>
            {
                Tap(N.VK_LWIN);
                bool open = WatchStart(1500);
                Pause(300);
                if (hold) { Down(N.VK_LMENU); Pause(25); Tap(N.VK_TAB, 20); Pause(45); Tap(N.VK_TAB, 20); Pause(260); Up(N.VK_LMENU); }
                else QuickTap(N.VK_TAB)(before);
                return new() { ["must_start_opened"] = open };
            }, section: Sections.RecentKey, index: hold ? 1 : 0);
        }
    }

    // ---------- one step ----------

    /// <summary>Alt down, one key, Alt up, quickly.</summary>
    static Func<Dictionary<IntPtr, string>, Dictionary<string, object?>> QuickTap(ushort vk) => _ =>
    {
        Down(N.VK_LMENU); Pause(20); Tap(vk, 20); Pause(20); Up(N.VK_LMENU);
        return new();
    };

    /// <summary>
    /// Run keys that should end in a switch to <paramref name="expected"/>, and check everything around it.
    /// Keys in the returned row named must_* have to be true; <paramref name="logCheck"/> sees Altlane's
    /// log lines from this step.
    /// </summary>
    static void Switch(string test, int i, IntPtr expected, Func<Dictionary<IntPtr, string>, Dictionary<string, object?>> keys,
        string? section = null, int? index = null, Func<List<JsonElement>, Dictionary<string, object?>, bool>? logCheck = null)
    {
        EnsureFront();
        Tail();
        var before = Visible();
        var row = keys(before);
        long t = Stopwatch.GetTimestamp(); // from the last key: Alt-up or Enter
        bool landed = Activator.Verify(expected, 800);
        double ms = Clock.Between(t, Stopwatch.GetTimestamp());
        IntPtr fg = N.GetForegroundWindow();
        uint menu = Win.MenuFlags(fg);
        bool start = StartOpen();
        var native = NativeCheck(before);
        Pause(120);
        bool ovLeft = Shown(_overlay);
        var ev = Tail();
        var sw = ev.LastOrDefault(e => Ev(e) == "switch");
        bool logOk = true;
        if (section != null || index != null)
        {
            row["sw_section"] = Str(sw, "section");
            row["sw_index"] = sw.ValueKind == JsonValueKind.Object ? sw.GetProperty("index").GetInt32() : null;
            row["sw_quick"] = sw.ValueKind == JsonValueKind.Object ? sw.GetProperty("quick").GetBoolean() : null;
            logOk = (section == null || (string?)row["sw_section"] == section) && (index == null || (int?)row["sw_index"] == index);
        }
        if (logCheck != null) logOk &= logCheck(ev, row);
        row["log_ok"] = logOk;
        row["landed"] = landed;
        row["switch_ms"] = Math.Round(ms, 1);
        row["expected"] = Name(expected);
        row["got"] = Name(fg);
        row["menu"] = menu;
        row["start_open"] = start;
        row["native"] = native;
        row["overlay_left_open"] = ovLeft;
        row["ok"] = landed && logOk && menu == 0 && !start && native.Count == 0 && !ovLeft && Musts(row);
        Row(test, i, row);
        if (landed) MoveFront(expected);
        else if (N.IsWindow(expected)) Front(expected); // keep the model true for the next step
        Recover(menu, start);
        Pause(150);
    }

    /// <summary>Run keys that should leave the window in front where it is.</summary>
    static void NoSwitch(string test, int i, string what, Func<Dictionary<string, object?>> keys, Func<List<JsonElement>, bool> logCheck)
    {
        EnsureFront();
        Tail();
        IntPtr fg0 = N.GetForegroundWindow();
        var before = Visible();
        var row = keys();
        Pause(250);
        IntPtr fg = N.GetForegroundWindow();
        uint menu = Win.MenuFlags(fg);
        bool start = StartOpen();
        var native = NativeCheck(before);
        var ev = Tail();
        bool logOk = logCheck(ev);
        bool switched = ev.Any(e => Ev(e) is "switch");
        bool overlayExpected = row.ContainsKey("must_toast");
        bool ovLeft = !overlayExpected && Shown(_overlay);
        row["what"] = what;
        row["log_ok"] = logOk;
        row["fg_same"] = fg == fg0;
        row["got"] = Name(fg);
        row["switched"] = switched;
        row["menu"] = menu;
        row["start_open"] = start;
        row["native"] = native;
        row["overlay_left_open"] = ovLeft;
        row["ok"] = logOk && fg == fg0 && !switched && menu == 0 && !start && native.Count == 0 && !ovLeft && Musts(row);
        Row(test, i, row);
        if (fg != fg0 && N.IsWindow(fg0)) Activate(fg0);
        Recover(menu, start);
        Pause(150);
    }

    static bool Musts(Dictionary<string, object?> row) => row.Where(kv => kv.Key.StartsWith("must_")).All(kv => kv.Value is true);

    static void Recover(uint menu, bool start)
    {
        if (menu == 0 && !start) return;
        Log.Write("recovery_esc", new { menu, start });
        RecoveryTimes.Add(Clock.Unix);
        Tap(N.VK_ESCAPE); Pause(150);
    }

    static void WaitOverlayGone()
    {
        var sw = Stopwatch.StartNew();
        while (Shown(_overlay) && sw.ElapsedMilliseconds < 4000) Pause(50);
    }

    static void Row(string test, int i, Dictionary<string, object?> data)
    {
        data["test"] = test; data["i"] = i; data["t"] = Math.Round(Clock.Unix, 1);
        Rows.Add(data);
        Log.Write("step", new { data });
    }

    /// <summary>--only T17,T11: run just the phases whose names start with these (and T0's calibration). The verdict is then NO-GO.</summary>
    static string[]? _only;

    static void Phase(string name, Action body)
    {
        if (_only != null && !name.StartsWith("T0_") && !_only.Any(p => name.StartsWith(p + "_", StringComparison.Ordinal))) return;
        double t0 = Clock.Unix;
        Log.Write("phase_start", new { name });
        try { body(); }
        finally { Phases.Add((name, t0, Clock.Unix)); Log.Write("phase_end", new { name }); }
    }
}
