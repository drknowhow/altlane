using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Altlane.Spike;

sealed class AbortException(string why) : Exception(why);

/// <summary>
/// Drives the switcher with injected keys against its own six test windows and records what happened.
/// Safety: starts only after the user has been idle, stops at the first real (non-injected) key or
/// mouse event, never runs with an elevated window in front, releases every key it holds, and only
/// stops processes it started itself.
/// </summary>
static partial class SelfTest
{
    static readonly IntPtr Drv = (IntPtr)0x44525652; // "DRVR": the switcher treats these as user keys
    static readonly string[] StartProcs = { "StartMenuExperienceHost", "SearchHost", "SearchApp", "ShellExperienceHost" };

    static volatile string? _abort;
    static readonly HashSet<ushort> Held = new();
    static readonly List<IntPtr> Mru = new(); // the driver's own model of test-window recency
    static readonly Dictionary<string, IntPtr> W = new();
    static readonly List<Dictionary<string, object?>> Rows = new();
    static readonly List<(string name, double t0, double t1)> Phases = new();
    static readonly Dictionary<string, HashSet<string>> NativeSigs = new() { ["alt_tab"] = new(), ["task_view"] = new() };
    static IntPtr _overlay;
    static string _dir = "";
    static Process? _sw, _tw;
    static EventWaitHandle? _stop;
    static uint _guardTid;
    static N.HookProc? _kbGuard, _msGuard;

    public static int Run(Dictionary<string, string> o)
    {
        _dir = o.GetValueOrDefault("log-dir") ?? Path.Combine(AppContext.BaseDirectory, "logs", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(_dir);
        Log.Init(Path.Combine(_dir, "driver.jsonl"), "driver");

        var others = Process.GetProcessesByName("Altlane.Spike").Where(p => p.Id != Environment.ProcessId).Select(p => p.Id).ToList();
        if (others.Count > 0) { Log.Write("refused", new { why = "another Altlane.Spike is running", pids = others }); Log.Flush(); return 4; }

        double idleNeed = o.Num("idle-ms", 8000), idleWait = o.Num("idle-wait-s", 600);
        Log.Write("waiting_for_idle", new { need_ms = idleNeed, max_s = idleWait });
        if (!WaitIdle(idleNeed, idleWait)) { Log.Write("gave_up", new { why = "user never idle long enough" }); Log.Flush(); return 3; }

        IntPtr original = N.GetForegroundWindow();
        Log.Write("start_fg", new { fg = Sig(original), idle_ms = Win.IdleMs() });
        string result = "completed";
        StartGuard();
        try
        {
            StartTestWindows();
            Phase("T0_calibrate", T0);
            StartSwitcher(o.GetValueOrDefault("commit", "alt"));
            Phase("setup", Setup);
            Phase("T1_quick_tap", T1);
            Phase("T2_hold_cycle", T2);
            Phase("T3_filter_minimized", T3);
            Phase("T4_esc_cancel", T4);
            Phase("T5_win_tab", T5);
            Phase("T6_shift_win_tab", T6);
            Phase("C_detector_controls", Controls);
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

    // ---------- tests ----------

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
        foreach (var s in appeared.Append(fg).Where(s => !s.EndsWith("|Altlane.Spike"))) NativeSigs[what].Add(s);
        Row("T0", 0, new()
        {
            ["what"] = what, ["fg"] = fg, ["fg_title"] = Win.Title(N.GetForegroundWindow()),
            ["appeared"] = appeared, ["start_open"] = StartOpen(),
        });
    }

    /// <summary>
    /// Positive controls, run with no switcher session open: a stray key must reach the test window,
    /// a lone Alt must put it in menu mode, a lone Win must open Start. If these don't fire, the
    /// zero-leak and zero-menu results above prove nothing.
    /// </summary>
    static void Controls()
    {
        Activate(Mru.First(h => !N.IsIconic(h)));
        Tap('Z'); Pause(200);
        Down(N.VK_LMENU); Pause(30); Up(N.VK_LMENU); Pause(300);
        uint menu = Win.MenuFlags(N.GetForegroundWindow());
        Row("C", 0, new() { ["what"] = "alt_alone", ["ok"] = menu != 0, ["menu_flags"] = menu });
        if (menu != 0) { Tap(N.VK_ESCAPE); Pause(200); }
        Down(N.VK_LWIN); Pause(30); Up(N.VK_LWIN);
        bool start = WatchStart(1500);
        Row("C", 1, new() { ["what"] = "win_alone", ["ok"] = start });
        if (start) { Tap(N.VK_ESCAPE); Pause(500); }
    }

    static void Setup()
    {
        foreach (var n in new[] { "delta", "charlie", "bravo", "alpha" }) { Activate(W[n]); Pause(120); }
        Mru.Clear();
        Mru.AddRange(new[] { "alpha", "bravo", "charlie", "delta" }.Select(n => W[n]));
    }

    /// <summary>Quick tap x20: Alt+Tab released fast goes to the previous window with nothing drawn.</summary>
    static void T1()
    {
        for (int i = 0; i < 20; i++)
            Switch("T1", i, Mru[1], _ => { Down(N.VK_LMENU); Pause(20); Tap(N.VK_TAB, 20); Pause(20); Up(N.VK_LMENU); return new(); });
    }

    /// <summary>Hold and cycle x20: k Tabs with Alt held past the show delay, overlay must appear.</summary>
    static void T2()
    {
        for (int i = 0; i < 20; i++)
        {
            int k = 1 + i % 3;
            Switch("T2", i, Mru[k], before =>
            {
                Down(N.VK_LMENU); Pause(25);
                for (int j = 0; j < k; j++) { Tap(N.VK_TAB, 20); Pause(45); }
                Pause(220);
                var mid = NativeCheck(before);
                bool ov = Shown(_overlay);
                Up(N.VK_LMENU);
                return new() { ["k"] = k, ["overlay_mid"] = ov, ["native_mid"] = mid };
            });
        }
    }

    /// <summary>Type-to-filter onto a minimized window x10. Letters pin the view; Enter commits.</summary>
    static void T3()
    {
        for (int i = 0; i < 10; i++)
        {
            string name = i % 2 == 0 ? "echo" : "foxtrot", typed = name[..3];
            IntPtr target = W[name];
            if (!N.IsIconic(target)) { N.ShowWindowAsync(target, N.SW_MINIMIZE); Pause(350); RefocusTestWindow(target); }
            Switch("T3", i, target, _ =>
            {
                Down(N.VK_LMENU); Pause(25); Tap(N.VK_TAB, 20); Pause(40);
                foreach (char c in typed.ToUpperInvariant()) { Tap(c, 20); Pause(30); }
                Up(N.VK_LMENU); Pause(120);
                bool pinned = Shown(_overlay);
                Tap(N.VK_RETURN);
                return new() { ["typed"] = typed, ["pinned_after_alt_up"] = pinned };
            });
        }
    }

    /// <summary>Esc cancel x5: nothing switches, nothing opens.</summary>
    static void T4()
    {
        for (int i = 0; i < 5; i++)
        {
            Check();
            IntPtr fg0 = N.GetForegroundWindow();
            Down(N.VK_LMENU); Pause(25); Tap(N.VK_TAB, 20); Pause(220);
            bool ovMid = Shown(_overlay);
            Tap(N.VK_ESCAPE); Pause(40); Up(N.VK_LMENU); Pause(250);
            IntPtr fg = N.GetForegroundWindow();
            uint menu = Win.MenuFlags(fg);
            bool ovAfter = Shown(_overlay);
            Row("T4", i, new()
            {
                ["ok"] = ovMid && !ovAfter && fg == fg0 && menu == 0, ["overlay_mid"] = ovMid,
                ["overlay_after"] = ovAfter, ["fg_same"] = fg == fg0, ["menu"] = menu,
            });
            Recover(menu, false);
        }
    }

    /// <summary>Win+Tab x5: our overview, no Start, no Task View. Three cancels, two Tab+Enter commits.</summary>
    static void T5()
    {
        for (int i = 0; i < 5; i++)
        {
            Check();
            IntPtr fg0 = N.GetForegroundWindow();
            var before = Visible();
            Down(N.VK_LWIN); Pause(25); Tap(N.VK_TAB, 20); Pause(25); Up(N.VK_LWIN);
            bool start = WatchStart(400);
            bool ov = Shown(_overlay);
            var native = NativeCheck(before);
            var row = new Dictionary<string, object?> { ["overlay"] = ov, ["start_open"] = start, ["native"] = native };
            if (i < 3)
            {
                Tap(N.VK_ESCAPE); Pause(250);
                bool ovAfter = Shown(_overlay);
                row["action"] = "esc";
                row["ok"] = ov && !ovAfter && !start && native.Count == 0 && N.GetForegroundWindow() == fg0;
                row["overlay_after"] = ovAfter;
            }
            else
            {
                IntPtr expected = Mru[1];
                Tap(N.VK_TAB); Pause(40); Tap(N.VK_RETURN);
                long t = Stopwatch.GetTimestamp();
                bool landed = Activator.Verify(expected, 800);
                row["action"] = "tab_enter";
                row["switch_ms"] = Math.Round(Clock.Between(t, Stopwatch.GetTimestamp()), 1);
                row["expected"] = Win.Title(expected);
                row["got"] = Win.Title(N.GetForegroundWindow());
                row["ok"] = ov && landed && !start && native.Count == 0;
                if (landed) MoveFront(expected);
            }
            Row("T5", i, row);
            Recover(Win.MenuFlags(N.GetForegroundWindow()), start);
            Pause(200);
        }
    }

    /// <summary>Shift+Win+Tab x2 must still reach the real Task View.</summary>
    static void T6()
    {
        for (int i = 0; i < 2; i++)
        {
            Check();
            IntPtr fg0 = N.GetForegroundWindow();
            var before = Visible();
            Down(N.VK_LWIN); Pause(25); Down(N.VK_LSHIFT); Pause(25); Tap(N.VK_TAB, 20); Pause(25);
            Up(N.VK_LSHIFT); Pause(25); Up(N.VK_LWIN);
            bool start = WatchStart(300);
            Pause(700);
            var native = NativeCheck(before);
            string fgTitle = Win.Title(N.GetForegroundWindow());
            bool ov = Shown(_overlay);
            Tap(N.VK_ESCAPE); Pause(800);
            bool back = N.GetForegroundWindow() == fg0;
            Row("T6", i, new()
            {
                ["ok"] = native.Contains("task_view") && !ov && !start, ["native"] = native, ["fg_title"] = fgTitle,
                ["overlay"] = ov, ["start_open"] = start, ["fg_back"] = back,
            });
            if (!back && N.IsWindow(fg0)) Activate(fg0);
            Pause(300);
        }
    }

    // ---------- one switch ----------

    static void Switch(string test, int i, IntPtr expected, Func<Dictionary<IntPtr, string>, Dictionary<string, object?>> keys)
    {
        Check();
        var before = Visible();
        var extra = keys(before);
        long t = Stopwatch.GetTimestamp(); // from the last key: Alt-up or Enter
        bool landed = Activator.Verify(expected, 800);
        double ms = Clock.Between(t, Stopwatch.GetTimestamp());
        IntPtr fg = N.GetForegroundWindow();
        uint menu = Win.MenuFlags(fg);
        bool start = StartOpen();
        var native = NativeCheck(before);
        Pause(60);
        bool ovLeft = Shown(_overlay);
        extra["ok"] = landed && menu == 0 && !start && native.Count == 0 && !ovLeft;
        extra["landed"] = landed;
        extra["switch_ms"] = Math.Round(ms, 1);
        extra["expected"] = Win.Title(expected);
        extra["got"] = Win.Title(fg);
        extra["menu"] = menu;
        extra["start_open"] = start;
        extra["native"] = native;
        extra["overlay_left_open"] = ovLeft;
        Row(test, i, extra);
        if (landed) MoveFront(expected);
        else if (N.IsWindow(expected)) { Activate(expected); MoveFront(expected); } // keep the model true for the next step
        Recover(menu, start);
        Pause(150);
    }

    static void Recover(uint menu, bool start)
    {
        if (menu == 0 && !start) return;
        Log.Write("recovery_esc", new { menu, start });
        RecoveryTimes.Add(Clock.Unix);
        Tap(N.VK_ESCAPE); Pause(150);
    }

    static void RefocusTestWindow(IntPtr not)
    {
        var to = Mru.First(h => h != not && !N.IsIconic(h));
        Activate(to);
        MoveFront(to);
    }

    static void MoveFront(IntPtr h) { Mru.Remove(h); Mru.Insert(0, h); }

    static void Row(string test, int i, Dictionary<string, object?> data)
    {
        data["test"] = test; data["i"] = i; data["t"] = Math.Round(Clock.Unix, 1);
        Rows.Add(data);
        Log.Write("step", new { data = data });
    }

    static void Phase(string name, Action body)
    {
        double t0 = Clock.Unix;
        Log.Write("phase_start", new { name });
        try { body(); }
        finally { Phases.Add((name, t0, Clock.Unix)); Log.Write("phase_end", new { name }); }
    }
}
