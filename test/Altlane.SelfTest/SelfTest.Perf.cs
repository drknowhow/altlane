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

    /// <summary>Every open drew the whole busy desktop: as many views as opens, each with a card per window.</summary>
    static bool Busy(List<JsonElement> ev, bool lanes, int opens, int windows)
    {
        var composed = ev.Where(e => Ev(e) == "overlay_composed" && Lanes(e) == lanes).ToList();
        // Alt+Tab leaves the window in front out of its groups and adds a Recent row of three.
        return composed.Count == opens && composed.All(e => e.GetProperty("cards").GetInt32() >= windows - 1);
    }
}
