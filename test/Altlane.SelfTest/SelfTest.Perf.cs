using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Altlane;

/// <summary>Speed on a busy desktop: 30 more windows, spread over the groups the way a working day is.</summary>
static partial class SelfTest
{
    const string ManyPhase = "T17_busy_desktop";
    const int BulkCount = 30;

    /// <summary>
    /// A second test-window process adds 30 windows; Alt+Tab then opens 12 times and Win+Tab 6 times, each
    /// closed with Esc. The report takes the show times from Altlane's log for this phase alone.
    /// </summary>
    static void T17()
    {
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, TestWindows.BulkReadyEvent);
        ready.Reset();
        var bulk = Spawn(Environment.ProcessPath!, "testwindows", "--bulk", BulkCount.ToString(), "--minutes", "4", "--log", Path.Combine(_dir, "bulk.jsonl"));
        try
        {
            if (!ready.WaitOne(15000))
            {
                Row("T17", 0, new() { ["what"] = "bulk_windows", ["ok"] = false, ["error"] = $"the {BulkCount} extra windows didn't appear" });
                return;
            }
            Pause(1500); // painted, and seen by Altlane's tracker
            Reset(MouseOrder);
            int n = Win.TopLevel().Count(h => Win.Title(h).StartsWith(TestWindows.Prefix, StringComparison.Ordinal) && N.IsWindowVisible(h));

            NoSwitch("T17", 0, "alt_tab_x12", () =>
            {
                for (int i = 0; i < 12; i++)
                {
                    Down(N.VK_LMENU); Pause(25); Tap(N.VK_TAB, 20); Pause(300);
                    Tap(N.VK_ESCAPE); Pause(40); Up(N.VK_LMENU); Pause(250);
                }
                return new() { ["windows"] = n };
            }, ev => Busy(ev, lanes: false, 12, n));

            NoSwitch("T17", 1, "win_tab_x6", () =>
            {
                for (int i = 0; i < 6; i++) { WinTab(); Pause(400); Tap(N.VK_ESCAPE); Pause(300); }
                return new() { ["windows"] = n };
            }, ev => Busy(ev, lanes: true, 6, n));
        }
        finally
        {
            foreach (var h in Win.TopLevel().Where(h => Win.Title(h).StartsWith(TestWindows.Prefix + "bulk ", StringComparison.Ordinal)))
                N.PostMessage(h, N.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            if (!bulk.WaitForExit(5000)) bulk.Kill(); // the run's own process
            Log.Write("bulk_closed", new { exit = bulk.HasExited });
            Pause(300);
        }
    }

    /// <summary>How long Altlane waits after a session before it trims its heap, in this run.</summary>
    static int _trimMs = 2000;
    static readonly List<double> TrimmedShows = new();

    /// <summary>
    /// Altlane gives its heap's spare room back a while after a session, and this run makes that wait a few
    /// seconds (--gc-trim-ms). Five times: wait for the trim, then open Alt+Tab and close it with Esc. Each
    /// round needs a trim first, and the views drawn on a trimmed heap have Alt+Tab's 50 ms target of their own.
    /// </summary>
    static void T19()
    {
        // A session first: the trim the earlier phases left behind may be over already.
        Down(N.VK_LMENU); Pause(25); Tap(N.VK_TAB, 20); Pause(300); Tap(N.VK_ESCAPE); Pause(40); Up(N.VK_LMENU);
        for (int i = 0; i < 5; i++)
        {
            var trim = WaitTrim();
            bool trimmed = trim.ValueKind == JsonValueKind.Object;
            NoSwitch("T19", i, "alt_tab_after_trim", () =>
            {
                Down(N.VK_LMENU); Pause(25); Tap(N.VK_TAB, 20); Pause(300);
                Tap(N.VK_ESCAPE); Pause(40); Up(N.VK_LMENU);
                return new()
                {
                    ["must_trimmed"] = trimmed,
                    ["trim_ms"] = trimmed ? trim.GetProperty("ms").GetDouble() : null,
                    ["freed_mb"] = trimmed ? Math.Round(trim.GetProperty("ws_mb_before").GetDouble() - trim.GetProperty("ws_mb").GetDouble(), 1) : null,
                    ["committed_mb"] = trimmed ? trim.GetProperty("committed_mb").GetDouble() : null,
                };
            }, ev =>
            {
                var shows = Nums(ev.Where(e => !Lanes(e)).ToList(), "overlay_composed", "show_ms");
                TrimmedShows.AddRange(shows);
                return shows.Length == 1;
            });
        }
    }

    /// <summary>Altlane's next gc_trim line, or an empty element when none comes within the wait and 4 s more.</summary>
    static JsonElement WaitTrim()
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < _trimMs + 4000)
        {
            var t = Tail().LastOrDefault(e => Ev(e) == "gc_trim");
            if (t.ValueKind == JsonValueKind.Object) return t;
            Pause(100);
        }
        return default;
    }

    /// <summary>Every open drew the whole busy desktop: as many views as opens, each with a card per window.</summary>
    static bool Busy(List<JsonElement> ev, bool lanes, int opens, int windows)
    {
        var composed = ev.Where(e => Ev(e) == "overlay_composed" && Lanes(e) == lanes).ToList();
        // Alt+Tab leaves the window in front out of its groups and adds a Recent row of three.
        return composed.Count == opens && composed.All(e => e.GetProperty("cards").GetInt32() >= windows - 1);
    }
}
