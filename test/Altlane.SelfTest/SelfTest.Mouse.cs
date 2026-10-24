using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Automation;

namespace Altlane;

/// <summary>0.4.0's phases: the mouse in Alt+Tab, how each switch was picked, drag and middle-click, and Windows Terminal tabs.</summary>
static partial class SelfTest
{
    static readonly string[] MouseOrder = { "alpha", "charlie", "bravo", "delta", "echo" };
    const string TabOne = TestWindows.Prefix + "tab one", TabTwo = TestWindows.Prefix + "tab two";
    const uint MouseMiddleDown = 0x0020, MouseMiddleUp = 0x0040;

    /// <summary>Pointing at a card selects it in Alt+Tab too, and every switch logs how it was picked.</summary>
    static void T14()
    {
        N.GetCursorPos(out var home);
        try
        {
            Reset(MouseOrder);
            IntPtr target = Mru[3]; // the last card in Recent
            var seen = new List<JsonElement>();
            Switch("T14", 0, target, _ =>
            {
                Down(N.VK_LMENU); Pause(25); Tap(N.VK_TAB, 20); Pause(300);
                var row = new Dictionary<string, object?> { ["what"] = "hover_selects_in_alt_tab" };
                row["must_cursor_on_card"] = PointAt(target, seen);
                Pause(120);
                Up(N.VK_LMENU);
                return row;
            }, logCheck: (ev, row) =>
            {
                var all = seen.Concat(ev).ToList();
                var sw = all.LastOrDefault(e => Ev(e) == "switch");
                row["pick"] = Str(sw, "pick");
                return all.Any(e => Ev(e) == "hover" && e.GetProperty("sel").GetInt64() == target.ToInt64())
                       && Str(sw, "how") == "alt_up" && Str(sw, "pick") == "mouse";
            });
        }
        finally { MouseTo(home.X, home.Y); Pause(100); }

        Reset(MouseOrder);
        var flat = ExpectedFlat();
        Switch("T14", 1, flat[1].h, _ =>
        {
            Down(N.VK_LMENU); Pause(25); Tap(N.VK_TAB, 20); Pause(45); Tap(N.VK_TAB, 20); Pause(220); Up(N.VK_LMENU);
            return new() { ["what"] = "pick_tab" };
        }, section: flat[1].sec, index: 1, logCheck: (ev, row) => Picked(ev, row, "tab"));

        Reset(MouseOrder);
        Switch("T14", 2, Mru[1], before => { var r = QuickTap(N.VK_TAB)(before); r["what"] = "pick_quick"; return r; },
            section: Sections.RecentKey, index: 0, logCheck: (ev, row) => Picked(ev, row, "quick"));

        Reset(MouseOrder);
        flat = ExpectedFlat();
        var term = flat.First(x => x.sec == "term");
        Switch("T14", 3, term.h, _ =>
        {
            Down(N.VK_LMENU); Pause(25); Tap(N.VK_TAB, 20); Pause(220); Tap('2'); Pause(60); Up(N.VK_LMENU);
            return new() { ["what"] = "pick_number" };
        }, section: "term", index: flat.IndexOf(term), logCheck: (ev, row) => Picked(ev, row, "number"));
    }

    static bool Picked(List<JsonElement> ev, Dictionary<string, object?> row, string want)
    {
        var sw = ev.LastOrDefault(e => Ev(e) == "switch");
        row["pick"] = Str(sw, "pick");
        row["nav"] = sw.ValueKind == JsonValueKind.Object && sw.TryGetProperty("nav", out var nav) ? nav.ToString() : null;
        // Titles stay out of the log outside test mode; in test mode they're the test windows' own.
        return Str(sw, "pick") == want && sw.TryGetProperty("nav", out _);
    }

    /// <summary>In the overview, dragging a card onto another lane moves it there; a middle click closes its window.</summary>
    static void T15()
    {
        N.GetCursorPos(out var home);
        try
        {
            Reset(MouseOrder);
            IntPtr bravo = W["bravo"];
            Grp[bravo] = "term";
            string expect = ViewString(ExpectedLanes());
            var seen = new List<JsonElement>();
            NoSwitch("T15", 0, "drag_to_terminals", () =>
            {
                WinTab(); Pause(300);
                var row = OpenChecks("drag");
                row["must_dragged"] = DragCard(bravo, "term", seen, "drag");
                Pause(400);
                row["must_open_after_drop"] = Shown(_overlay);
                Tap(N.VK_ESCAPE);
                return row;
            }, ev =>
            {
                var all = seen.Concat(ev).ToList();
                return all.Any(e => Ev(e) == "regroup" && Str(e, "via") == "drag" && Str(e, "kind") == "window" && Str(e, "group") == "term")
                       && ViewOf(all.LastOrDefault(e => Ev(e) == "view")) == expect;
            });

            Reset(MouseOrder);
            IntPtr echo = W["echo"];
            var seen2 = new List<JsonElement>();
            NoSwitch("T15", 1, "middle_click_closes", () =>
            {
                WinTab(); Pause(300);
                var row = OpenChecks("middle_click");
                row["must_cursor_on_card"] = PointAt(echo, seen2);
                Pause(80);
                Check(); CheckTarget();
                SendMouse(MouseMiddleDown); Pause(40); SendMouse(MouseMiddleUp);
                Pause(400);
                Tap(N.VK_ESCAPE);
                var sw = Stopwatch.StartNew();
                while (N.IsWindow(echo) && sw.ElapsedMilliseconds < 2000) Pause(20);
                row["must_closed"] = !N.IsWindow(echo);
                return row;
            }, ev => seen2.Concat(ev).Any(e => Ev(e) == "close_window" && Str(e, "via") == "middle_click" && Str(e, "title") == TestWindows.Prefix + "echo"));
        }
        finally { MouseTo(home.X, home.Y); Pause(100); }
    }

    /// <summary>
    /// A Windows Terminal window of two tabs, with a Claude Code status file saying tab two's session is
    /// waiting. Each tab gets a card and tab two is marked; choosing tab two's card selects that tab; Del on
    /// a tab card closes only that tab.
    /// </summary>
    static void T16()
    {
        File.WriteAllText(Path.Combine(_dir, "claude-sessions", "1.json"), JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId, sessionId = "selftest", cwd = @"C:\Projects\altlane", name = TabTwo, nameSource = "user",
            status = "waiting", statusUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeMilliseconds(), entrypoint = "cli",
        }));
        IntPtr wt = OpenTerminal();
        if (wt == IntPtr.Zero) { Row("T16", 0, new() { ["what"] = "windows_terminal", ["ok"] = false, ["error"] = "no Windows Terminal window with two tabs appeared" }); return; }
        W["wt"] = wt;
        Grp[wt] = "term";
        N.GetCursorPos(out var home);
        try
        {
            Activate(wt);
            MoveFront(wt);
            Pause(5500); // Altlane's 5 s tab refresh has read the new window by now

            NoSwitch("T16", 0, "tab_cards_and_marks", () =>
            {
                Down(N.VK_LMENU); Pause(25); Tap(N.VK_TAB, 20); Pause(700);
                Shot("tabs");
                Tap(N.VK_ESCAPE); Pause(40); Up(N.VK_LMENU);
                return new();
            }, ev =>
            {
                var v = ev.LastOrDefault(e => Ev(e) == "view");
                if (v.ValueKind != JsonValueKind.Object) return false;
                var term = v.GetProperty("sections").EnumerateArray().FirstOrDefault(s => s.GetProperty("key").GetString() == "term");
                if (term.ValueKind != JsonValueKind.Object) return false;
                var cards = term.GetProperty("hwnds").EnumerateArray().Select(x => x.GetInt64())
                    .Zip(term.GetProperty("tabs").EnumerateArray().Select(x => x.GetInt32()), term.GetProperty("agents").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null))
                    .Where(c => c.First == wt.ToInt64()).ToList();
                return cards.Count == 2 && cards.Any(c => c.Second == 0 && c.Third == null) && cards.Any(c => c.Second == 1 && c.Third == "needs");
            });

            var seen = new List<JsonElement>();
            Switch("T16", 1, wt, _ =>
            {
                Down(N.VK_LMENU); Pause(25); Tap(N.VK_TAB, 20); Pause(400);
                var row = new Dictionary<string, object?> { ["what"] = "hidden_tab_selects_it" };
                row["must_cursor_on_card"] = PointAt(wt, seen, tab: 1);
                Pause(120);
                Up(N.VK_LMENU);
                return row;
            }, logCheck: (ev, row) =>
            {
                var all = seen.Concat(ev).ToList();
                var sw = Stopwatch.StartNew();
                while (!all.Any(e => Ev(e) == "tab_select") && sw.ElapsedMilliseconds < 2000) { Pause(50); all.AddRange(Tail()); }
                var s = all.LastOrDefault(e => Ev(e) == "switch");
                row["tab"] = Str(s, "tab");
                row["agent"] = Str(s, "agent");
                row["tab_select_ok"] = all.Any(e => Ev(e) == "tab_select" && Bool(e, "ok"));
                Pause(300);
                row["selected_after"] = WtSelected(wt);
                return Str(s, "tab") == "hidden" && Str(s, "agent") == "needs" && row["tab_select_ok"] is true && (string?)row["selected_after"] == TabTwo;
            });

            var seen2 = new List<JsonElement>();
            NoSwitch("T16", 2, "del_closes_tab", () =>
            {
                Down(N.VK_LMENU); Pause(25); Tap(N.VK_TAB, 20); Pause(400);
                var row = new Dictionary<string, object?> { ["must_cursor_on_card"] = PointAt(wt, seen2, tab: 0) };
                Pause(100);
                Tap(N.VK_DELETE); Pause(500);
                Tap(N.VK_ESCAPE); Pause(40); Up(N.VK_LMENU);
                var sw = Stopwatch.StartNew();
                while (WtTabs(wt).Count != 1 && sw.ElapsedMilliseconds < 2000) Pause(50);
                var left = WtTabs(wt);
                row["tabs_left"] = left.Count;
                row["must_tab_two_left"] = left.Count == 1 && left[0].Current.Name == TabTwo && N.IsWindow(wt);
                return row;
            }, ev => seen2.Concat(ev).Concat(Tail()).Any(e => Ev(e) == "close_tab" && Str(e, "via") == "del" && Bool(e, "ok")));
        }
        finally
        {
            // Closed even after an abort: the run opened this window.
            CloseTerminal(wt);
            W.Remove("wt");
            Mru.Remove(wt);
            if (_abort == null) MouseTo(home.X, home.Y);
        }
    }

    static bool Bool(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>A new Windows Terminal window with the two test tabs, tab one in front; zero when none appears.</summary>
    static IntPtr OpenTerminal()
    {
        var before = Win.TopLevel().ToHashSet();
        try
        {
            var psi = new ProcessStartInfo("wt.exe") { UseShellExecute = false };
            foreach (var a in new[]
            {
                "-w", "new", "new-tab", "--title", TabOne, "--suppressApplicationTitle", "cmd.exe", "/k", "echo Altlane self-test, tab one",
                ";", "new-tab", "--title", TabTwo, "--suppressApplicationTitle", "cmd.exe", "/k", "echo Altlane self-test, tab two",
                ";", "focus-tab", "-t", "0",
            }) psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception e) { Log.Write("wt_missing", new { error = e.Message }); return IntPtr.Zero; }
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 15000)
        {
            var h = Win.TopLevel().FirstOrDefault(x => !before.Contains(x) && Win.Class(x) == "CASCADIA_HOSTING_WINDOW_CLASS" && Win.Title(x).StartsWith(TestWindows.Prefix + "tab", StringComparison.Ordinal));
            if (h != IntPtr.Zero && WtTabs(h).Count == 2 && WtSelected(h) == TabOne) { Log.Write("wt_open", new { hwnd = h.ToInt64(), ms = sw.ElapsedMilliseconds }); return h; }
            Pause(100);
        }
        return IntPtr.Zero;
    }

    static List<AutomationElement> WtTabs(IntPtr h)
    {
        if (!N.IsWindow(h)) return new();
        try
        {
            return AutomationElement.FromHandle(h).FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
                .Cast<AutomationElement>().ToList();
        }
        catch (Exception e) when (e is ElementNotAvailableException or ArgumentException) { return new(); }
    }

    static string? WtSelected(IntPtr h) =>
        WtTabs(h).FirstOrDefault(e => e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p) && ((SelectionItemPattern)p).Current.IsSelected)?.Current.Name;

    /// <summary>Close the test window's tabs one at a time with their own buttons. Closing the window itself would ask first.</summary>
    static void CloseTerminal(IntPtr h)
    {
        var sw = Stopwatch.StartNew();
        while (N.IsWindow(h) && sw.ElapsedMilliseconds < 5000)
        {
            var tabs = WtTabs(h);
            if (tabs.Count == 0) break;
            try
            {
                var button = tabs[^1].FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                if (button?.GetCurrentPattern(InvokePattern.Pattern) is InvokePattern inv) inv.Invoke();
            }
            catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException) { }
            Sleep(400);
        }
        Log.Write("wt_closed", new { gone = !N.IsWindow(h) });
    }

    static void Sleep(int ms) => System.Threading.Thread.Sleep(ms);

    /// <summary>Drag a window's card onto a group's column or lane, with injected mouse input, and optionally screenshot the drag.</summary>
    static bool DragCard(IntPtr h, string group, List<JsonElement> seen, string? shot = null)
    {
        if (!PointAt(h, seen)) return false;
        var v = seen.LastOrDefault(e => Ev(e) == "view");
        if (!v.TryGetProperty("drops", out var drops) || drops.ValueKind != JsonValueKind.Array) return false;
        var d = drops.EnumerateArray().FirstOrDefault(x => Str(x, "group") == group);
        if (d.ValueKind != JsonValueKind.Object) return false;
        int tx = d.GetProperty("x").GetInt32() + d.GetProperty("w").GetInt32() / 2, ty = d.GetProperty("y").GetInt32() + d.GetProperty("h").GetInt32() / 2;
        N.GetCursorPos(out var p0);
        Check(); CheckTarget();
        SendMouse(MouseLeftDown); Pause(60);
        for (int s = 1; s <= 10; s++) { MouseTo(p0.X + (tx - p0.X) * s / 10, p0.Y + (ty - p0.Y) * s / 10); Pause(35); }
        Pause(200);
        if (shot != null) Shot(shot);
        SendMouse(MouseLeftUp);
        return true;
    }

    /// <summary>What is on screen over the overlay, popups included, as overlay-NAME.png.</summary>
    static void Shot(string name)
    {
        if (!N.GetWindowRect(_overlay, out var r)) return;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return;
        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(w, h));
        bmp.Save(Path.Combine(_dir, $"overlay-{name}.png"), System.Drawing.Imaging.ImageFormat.Png);
    }
}
