using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Altlane;

/// <summary>Rules, sections and layout logic, with no windows or keys involved. Prints one line per check.</summary>
static class UnitChecks
{
    static int _fail, _pass;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail.Length == 0 ? "" : "  -- " + detail)}");
    }

    static WinFacts F(string title, string exe, string? appId = null, bool browser = false) => new(title, exe, appId, browser);

    public static int Run()
    {
        DefaultRules();
        Precedence();
        Lenient();
        MoveScopes();
        SaveRules();
        SectionsLogic();
        LayoutLogic();
        ClaudeMarks();
        RulesUpgrade();
        TabMoves();
        Sessions();
        TabSections();
        UsageCounts();
        MonitorPlacement();
        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    static void DefaultRules()
    {
        var r = Rules.Parse(Rules.Default);
        Check("default rules parse with no warnings", r.Warnings.Count == 0, string.Join(" | ", r.Warnings));
        Check("default rules have the 7 design groups in order",
            string.Join(",", r.Groups.Select(g => g.Key)) == "agents,term,web,create,files,docs,other");
        Check("group numbers follow list order", r.Groups.Select(g => g.Num).SequenceEqual(Enumerable.Range(1, 7)));

        Group? G(string title, string exe, string? id = null) => r.Place(IntPtr.Zero, F(title, exe, id, r.IsBrowser(exe))).Group;
        Check("claude.exe goes to Agents", G("Claude", "claude.exe")?.Key == "agents");
        Check("Claude Code idle mark (✳) in Windows Terminal goes to Agents", G("✳ api-refactor", "WindowsTerminal.exe")?.Key == "agents");
        Check("Claude Code spinner (braille) goes to Agents", G("⠂ Fix the bug", "WindowsTerminal.exe")?.Key == "agents");
        Check("a plain terminal goes to Terminals", G("Administrator: PowerShell", "WindowsTerminal.exe")?.Key == "term");
        Check("a ✳ in the middle of a title is not the mark", G("notes ✳ draft", "WindowsTerminal.exe")?.Key == "term");
        Check("Google Flow tab goes to Creative", G("Google Flow - Rooftop Relay - Google Chrome", "chrome.exe")?.Key == "create");
        Check("a site rule doesn't apply outside a browser", G("Suno notes.txt - Notepad", "notepad.exe")?.Key == "docs");
        Check("an unclaimed browser window goes to Web", G("Inbox (4) - Gmail - Google Chrome", "chrome.exe")?.Key == "web");
        Check("explorer.exe goes to Files", G("Downloads", "explorer.exe")?.Key == "files");
        Check("exe names match case-insensitively", G("budget.xlsx - Excel", "excel.exe")?.Key == "docs");
        Check("an unknown app goes to Other", G("Notify2", "RustDesk.exe")?.Key == "other");
        Check("Samsung Magician's Notify window is hidden", r.Place(IntPtr.Zero, F("Notify", "SamsungMagician.exe")).Level == RuleLevel.Hide);
        Check("Samsung Magician's main window is not hidden", G("Samsung Magician", "SamsungMagician.exe")?.Key == "other");
    }

    static void Precedence()
    {
        var r = Rules.Parse("""
        {
          "groups": [ {"key":"a","name":"A"}, {"key":"b","name":"B"}, {"key":"c","name":"C"}, {"key":"w","name":"W"}, {"key":"o","name":"O"} ],
          "titles": [ {"startsWith":"TT","group":"a"}, {"contains":"only-in-x","app":"x.exe","group":"c"} ],
          "webApps": [ {"id":"Chrome._crx_abc","name":"Suno","group":"c"} ],
          "sites": [ {"contains":"Site","group":"b"} ],
          "apps": { "x": "b", "chrome.exe": "c" },
          "hide": [ "hidden.exe" ],
          "browsers": ["chrome.exe"],
          "browserGroup": "w",
          "otherGroup": "o"
        }
        """);
        var moved = new Dictionary<IntPtr, string> { [(IntPtr)7] = "c" };
        Placement P(string title, string exe, string? id = null, IntPtr h = default) => r.Place(h, F(title, exe, id, r.IsBrowser(exe)), moved);

        Check("a moved window beats every rule", P("TT", "x.exe", null, (IntPtr)7) is { Level: RuleLevel.Window, Group.Key: "c" });
        Check("hide beats titles", P("TT", "hidden.exe").Level == RuleLevel.Hide);
        Check("a title rule beats the app rule", P("TT x", "x.exe") is { Level: RuleLevel.Title, Group.Key: "a" });
        Check("a title rule with \"app\" applies only to that app", P("only-in-x", "y.exe").Group?.Key == "o" && P("only-in-x", "x.exe").Group?.Key == "c");
        Check("\"x\" in apps means x.exe", P("hello", "X.EXE") is { Level: RuleLevel.App, Group.Key: "b" });
        Check("a web-app id rule beats a site rule", P("Site", "chrome.exe", "Chrome._crx_abc") is { Level: RuleLevel.WebApp, Group.Key: "c" });
        Check("a site rule beats an app rule for a browser", P("Site", "chrome.exe") is { Level: RuleLevel.Site, Group.Key: "b" });
        Check("an app rule for the browser itself beats the browser fallback", P("page", "chrome.exe") is { Level: RuleLevel.App, Group.Key: "c" });
        Check("a moved window that names a missing group falls through", r.Place((IntPtr)8, F("TT", "x.exe", null, false), new Dictionary<IntPtr, string> { [(IntPtr)8] = "gone" }).Level == RuleLevel.Title);
    }

    static void Lenient()
    {
        var r = Rules.Parse("""
        {
          // comments are fine
          "groups": [ {"key":"a","name":"A"}, {"key":"a","name":"Dup"}, {"name":"no key"} ],
          "titles": [ {"match":"([unclosed","group":"a"}, {"contains":"x","group":"nope"} ],
          "apps": { "x.exe": "a", },
        }
        """);
        Check("comments and trailing commas parse", r.Groups.Count == 1);
        Check("bad entries become warnings, not errors", r.Warnings.Count == 4, string.Join(" | ", r.Warnings));
        Check("otherGroup defaults to the last group", r.Place(IntPtr.Zero, F("t", "zzz.exe")).Group?.Key == "a");
        bool threw = false;
        try { Rules.Parse("{ \"groups\": [ }"); } catch (System.Text.Json.JsonException) { threw = true; }
        Check("broken JSON throws, so the store keeps the last good rules", threw);
        threw = false;
        try { Rules.Parse("{ \"groups\": [] }"); } catch (System.Text.Json.JsonException) { threw = true; }
        Check("no groups at all is an error", threw);
        var nine = Rules.Parse("{\"groups\":[" + string.Join(",", Enumerable.Range(1, 10).Select(i => $"{{\"key\":\"g{i}\",\"name\":\"G{i}\"}}")) + "]}");
        Check("a tenth group is skipped with a warning", nine.Groups.Count == 9 && nine.Warnings.Count == 1);
    }

    static void MoveScopes()
    {
        var r = Rules.Parse(Rules.Default);
        (string, string?) S(string title, string exe, string? id = null)
        {
            var f = F(title, exe, id, r.IsBrowser(exe));
            return Rules.MoveScope(r.Place(IntPtr.Zero, f), f);
        }
        Check("Ctrl+number on an app-rule window saves an app rule", S("todo.txt", "notepad.exe") == ("app", "notepad.exe"));
        Check("Ctrl+number on an Other window saves an app rule", S("x", "RustDesk.exe") == ("app", "RustDesk.exe"));
        Check("Ctrl+number on a Claude Code terminal moves only that window", S("✳ api", "WindowsTerminal.exe") == ("window", null));
        Check("Ctrl+number on a browser tab moves only that window", S("Gmail", "chrome.exe", "Chrome") == ("window", null));
        Check("Ctrl+number on an installed web app saves a web-app rule", S("Suno", "chrome.exe", "Chrome._crx_xyz") == ("webApp", "Chrome._crx_xyz"));
    }

    static void SaveRules()
    {
        string dir = Path.Combine(Path.GetTempPath(), "altlane-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "rules.json");
        try
        {
            File.WriteAllText(path, Rules.Default);
            Check("saving an app rule succeeds", Rules.SaveRule(path, "app", "notepad.exe", "term", null) == null);
            var r = Rules.Parse(File.ReadAllText(path));
            Check("the saved app rule replaces the existing entry", r.Place(IntPtr.Zero, F("x", "notepad.exe")).Group?.Key == "term" && r.Warnings.Count == 0);
            Check("the file keeps the ✳ readable after saving", File.ReadAllText(path).Contains("\\u2733") || File.ReadAllText(path).Contains("✳"));
            Rules.SaveRule(path, "webApp", "Chrome._crx_q", "create", "Suno");
            Rules.SaveRule(path, "webApp", "Chrome._crx_q", "docs", "Suno");
            r = Rules.Parse(File.ReadAllText(path));
            Check("a web-app rule is added once and then updated", r.Place(IntPtr.Zero, F("Suno", "chrome.exe", "Chrome._crx_q", true)).Group?.Key == "docs"
                && File.ReadAllText(path).Split("Chrome._crx_q").Length == 2);
            File.WriteAllText(path, "{ broken");
            Check("a broken file is not overwritten", Rules.SaveRule(path, "app", "a.exe", "term", null) != null && File.ReadAllText(path) == "{ broken");
        }
        finally { Directory.Delete(dir, true); }
    }

    static void SectionsLogic()
    {
        var r = Rules.Parse(Rules.Default);
        int n = 0;
        Item I(string title, string exe, bool cur = false)
        {
            var w = new WinInfo((IntPtr)(++n), title, exe, exe, null, null, false);
            return new Item(w, r.Place(w.Hwnd, F(title, exe, null, r.IsBrowser(exe))), cur);
        }
        var mru = new List<Item>
        {
            I("✳ api", "WindowsTerminal.exe", cur: true), // 1 agents (current)
            I("Inbox", "chrome.exe"),                     // 2 web
            I("todo.txt", "notepad.exe"),                 // 3 docs
            I("Claude", "claude.exe"),                    // 4 agents
            I("PowerShell", "WindowsTerminal.exe"),       // 5 term
            I("Downloads", "explorer.exe"),               // 6 files
        };
        var secs = Sections.Build(mru, r.Groups, "");
        Check("Recent comes first and holds the three windows after the current one",
            secs[0].Key == "recent" && secs[0].Items.Select(i => (int)i.W.Hwnd).SequenceEqual(new[] { 2, 3, 4 }));
        Check("groups follow in number order, empty ones left out",
            string.Join(",", secs.Skip(1).Select(s => s.Key)) == "agents,term,web,files,docs");
        Check("a group lists its windows most recent first", secs[1].Items.Select(i => (int)i.W.Hwnd).SequenceEqual(new[] { 1, 4 }));
        var flat = Sections.Flat(secs);
        Check("the flat order is 3 recent + 6 grouped", flat.Count == 9);
        Check("Tab from the last card wraps to the first", Sections.Move(secs, flat[^1], 1) == flat[0]);
        Check("Shift+Tab from the first card wraps to the last", Sections.Move(secs, flat[0], -1) == flat[^1]);
        Check("` jumps from Recent to the first agent", Sections.JumpSection(secs, flat[0], 1) == new Slot("agents", (IntPtr)1));
        Check("Shift+` from Recent wraps to the last group", Sections.JumpSection(secs, flat[0], -1) == new Slot("docs", (IntPtr)3));
        Check("number 5 jumps to Files", Sections.Group(secs, 5) == new Slot("files", (IntPtr)6));
        Check("number 4 (Creative, empty) finds nothing", Sections.Group(secs, 4) == null);

        Slot S(string key, int h) => new(key, (IntPtr)h);
        Check("columns: ↓ moves down the column", Sections.MoveInColumn(secs, S("recent", 2), 1) == S("recent", 3));
        Check("columns: ↓ at the foot of a column stays", Sections.MoveInColumn(secs, S("recent", 4), 1) == S("recent", 4));
        Check("columns: ↑ at the top of a column stays", Sections.MoveInColumn(secs, S("recent", 2), -1) == S("recent", 2));
        Check("columns: → keeps the row", Sections.MoveAcrossColumns(secs, S("recent", 3), 1) == S("agents", 4));
        Check("columns: → into a shorter column lands on its last card", Sections.MoveAcrossColumns(secs, S("agents", 4), 1) == S("term", 5));
        Check("columns: ← from the first column wraps to the last", Sections.MoveAcrossColumns(secs, S("recent", 4), -1) == S("docs", 3));
        Check("columns: → from the last column wraps to the first, same row", Sections.MoveAcrossColumns(secs, S("docs", 3), 1) == S("recent", 2));
        Check("columns: an arrow with nothing selected picks the first card", Sections.MoveAcrossColumns(secs, null, 1) == flat[0] && Sections.MoveInColumn(secs, null, 1) == flat[0]);

        var f = Sections.Build(mru, r.Groups, "power");
        Check("a filter hides Recent and matches the title", f.Count == 1 && f[0].Key == "term");
        Check("a filter matches the group name", Sections.Build(mru, r.Groups, "files").Select(s => s.Key).SequenceEqual(new[] { "files" }));
        Check("a filter matches the app's file name", Sections.Build(mru, r.Groups, "explorer").Single().Items.Single().W.Hwnd == (IntPtr)6);
        Check("a filter that matches nothing gives no sections", Sections.Build(mru, r.Groups, "zzz").Count == 0);

        var only = Sections.Build(mru, r.Groups, "", "agents");
        Check("Alt+` scope shows only the group, no Recent", only.Count == 1 && only[0].Key == "agents");
        Check("Alt+number skips the window in front", Sections.Direct(mru, r.ByKey("agents")!)?.W.Hwnd == (IntPtr)4);
        Check("Alt+number on an empty group finds nothing", Sections.Direct(mru, r.ByKey("create")!) == null);

        var lanes = Sections.Build(mru, r.Groups, "", recent: false);
        Check("Win+Tab's overview has no Recent row and shows each window once, in its group",
            lanes.All(s => s.Key != "recent") && Sections.Flat(lanes).Count == mru.Count && Sections.Flat(lanes).Select(x => x.Hwnd).Distinct().Count() == mru.Count);
        Check("the overview's groups follow in number order", string.Join(",", lanes.Select(s => s.Key)) == "agents,term,web,files,docs");

        var desktop = mru.Select(i => i with { IsCurrent = false }).ToList();
        Check("with the desktop in front, Recent starts at the most recent window",
            Sections.Build(desktop, r.Groups, "")[0].Items.Select(i => (int)i.W.Hwnd).SequenceEqual(new[] { 1, 2, 3 }));
    }

    /// <summary>The overlay's fitting, at this PC's size: 1536×864 at 125% leaves about 1385 × 646 DIPs for cards.</summary>
    static void LayoutLogic()
    {
        var r = Rules.Parse(Rules.Default);
        int n = 0;
        Section Sec(string key, int count) => new(key, key, null, "", Enumerable.Range(0, count).Select(_ =>
        {
            var w = new WinInfo((IntPtr)(++n), "w", "a.exe", "a.exe", null, null, false);
            return new Item(w, r.Place(w.Hwnd, F("w", "a.exe")), false);
        }).ToList());
        const double W = 1385, H = 646;

        static bool Inside(Layouts.Result L, double w, double h) => L.width <= w && L.height <= h
            && L.cards.All(c => c.X >= 0 && c.Y >= 0 && c.X + L.cw <= L.width + 0.01 && c.Y + Layouts.CardH(L.thumbH) <= L.height + 0.01);
        static bool Apart(Layouts.Result L)
        {
            double ch = Layouts.CardH(L.thumbH);
            var cs = L.cards;
            for (int i = 0; i < cs.Count; i++)
                for (int j = i + 1; j < cs.Count; j++)
                    if (cs[i].X < cs[j].X + L.cw && cs[j].X < cs[i].X + L.cw && cs[i].Y < cs[j].Y + ch && cs[j].Y < cs[i].Y + ch) return false;
            return true;
        }

        var eight = Enumerable.Range(0, 8).Select(i => Sec("s" + i, 1)).ToList();
        var c8 = Layouts.Columns(eight, W, H);
        Check("columns: eight groups of one fit side by side", Inside(c8, W, H) && Apart(c8) && c8.cards.Select(c => c.X).Distinct().Count() == 8,
            $"cw {c8.cw}, {c8.width}x{c8.height}");
        Check("columns: each header spans its column", c8.heads.All(h => h.W == c8.cw));

        var tall = new List<Section> { Sec("long", 12) }.Concat(Enumerable.Range(0, 7).Select(i => Sec("t" + i, 1))).ToList();
        var ct = Layouts.Columns(tall, W, H);
        int subs = ct.cards.Where(c => c.Slot.Section == "long").Select(c => c.X).Distinct().Count();
        Check("columns: a group too long for the screen continues in a column beside it", Inside(ct, W, H) && Apart(ct) && subs >= 2,
            $"cw {ct.cw}, {subs} columns, {ct.width}x{ct.height}");
        Check("columns: that group's header spans both, and the next group starts after them",
            ct.heads[0].W == subs * ct.cw + (subs - 1) * Layouts.CardGap && ct.heads[1].X == ct.heads[0].X + ct.heads[0].W + Layouts.SecGapX);

        var grid = Layouts.Grid(tall, W, H);
        Check("grid: the same windows fit", Inside(grid, W, H) && Apart(grid), $"cw {grid.cw}, {grid.width}x{grid.height}");

        // Win+Tab's overview fills the work area: 1536×864 at 125% less the taskbar is 1536×816 DIPs.
        const double WW = 1536, WH = 816;
        var typical = new List<Section> { Sec("a", 5), Sec("b", 3), Sec("c", 4), Sec("d", 2), Sec("e", 1), Sec("f", 2) };
        foreach (var (name, secs) in new[] { ("eight groups of one", eight), ("a group of twelve", tall), ("six mixed groups", typical) })
        {
            var o = Layouts.Lanes(secs, WW, WH);
            var boxes = o.L.heads.Select(h => (h.S.Key, x: o.X + h.X - Layouts.LanePad, y: o.Y - Layouts.LanePad, w: h.W + 2 * Layouts.LanePad, h: o.LaneH + 2 * Layouts.LanePad)).ToList();
            double ch = Layouts.CardH(o.L.thumbH);
            bool inside = boxes.All(b => b.x >= Layouts.LanesSide - 0.5 && b.x + b.w <= WW - Layouts.LanesSide + 0.5);
            bool between = boxes.All(b => b.y >= Layouts.SearchTop + Layouts.SearchH + 8 && b.y + b.h <= o.FootY - 8) && o.FootY + Layouts.LanesFootH <= WH;
            bool apart = boxes.Zip(boxes.Skip(1), (p, q) => q.x - (p.x + p.w)).All(gap => gap >= 8);
            bool cardsIn = o.L.cards.All(c =>
            {
                var b = boxes.First(x => x.Key == c.Slot.Section);
                double cx = o.X + c.X, cy = o.Y + c.Y;
                return cx >= b.x && cx + o.L.cw <= b.x + b.w + 0.01 && cy >= b.y && cy + ch <= b.y + b.h + 0.01;
            });
            bool centered = Math.Abs(o.X - (WW - o.X - o.L.width)) <= 1;
            Check($"lanes, {name}: inside the work area, under the search box, over the footer, apart, cards in their lanes, centered",
                inside && between && apart && cardsIn && centered && Apart(o.L),
                $"inside {inside} between {between} apart {apart} cardsIn {cardsIn} centered {centered}; cw {o.L.cw}, lanes {o.X}..{o.X + o.L.width}");
        }
        var lw = Layouts.Lanes(typical, WW, WH).L;
        Check("lanes: the overview's lanes sit further apart than Alt+Tab's columns", lw.heads[1].X - (lw.heads[0].X + lw.heads[0].W) == Layouts.LaneGapX);
        int aSubs = lw.cards.Where(c => c.Slot.Section == "a").Select(c => c.X).Distinct().Count();
        Check("lanes: a group of five, too tall for its lane, continues in a second column inside the same lane", aSubs == 2, $"{aSubs} columns, cw {lw.cw}");
    }

    /// <summary>Claude Code's terminal title marks, as seen on 2026-10-24 (v2.1.308): ◐ ◑ while it works.</summary>
    static void ClaudeMarks()
    {
        var r = Rules.Parse(Rules.Default);
        Group? G(string title) => r.Place(IntPtr.Zero, F(title, "WindowsTerminal.exe")).Group;
        Check("Claude Code's working marks (◐ ◑) go to Agents", G("◐ Win+Tab hover fix")?.Key == "agents" && G("◑ Win+Tab hover fix")?.Key == "agents");
        Check("a ◐ in the middle of a title is not the mark", G("notes ◐ draft")?.Key == "term");
        Check("Bare drops a status mark and its space", Terminals.Bare("◐ fix bug") == "fix bug" && Terminals.Bare("✳ api") == "api" && Terminals.Bare("⠂ x y") == "x y");
        Check("Bare leaves ordinary titles alone", Terminals.Bare("PowerShell") == "PowerShell" && Terminals.Bare("a b") == "a b" && Terminals.Bare("1 file") == "1 file" && Terminals.Bare("◐") == "◐");
    }

    /// <summary>A rules.json an earlier version wrote gets the new Claude Code pattern; one the user edited is left alone.</summary>
    static void RulesUpgrade()
    {
        string dir = Path.Combine(Path.GetTempPath(), "altlane-unit-" + Guid.NewGuid().ToString("N"));
        string? old = null;
        try
        {
            Paths.Use(dir);
            Directory.CreateDirectory(dir);
            string v030 = Rules.Default.Replace(Rules.ClaudeTitle, Rules.OldClaudeTitle).Replace(Rules.ClaudeWhy, Rules.OldClaudeWhy);
            Check("the 0.3.0 rules text differs from today's", v030 != Rules.Default && v030.Contains(Rules.OldClaudeTitle));
            File.WriteAllText(Paths.Rules, v030);
            var store = new RuleStore(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            store.Load();
            old = File.ReadAllText(Paths.Rules);
            Check("0.3.0's rules.json is upgraded on load, the rest of the file unchanged", old == Rules.Default, $"{old.Length} vs {Rules.Default.Length} chars");
            Check("after the upgrade a working Claude Code tab goes to Agents", store.Current.Place(IntPtr.Zero, F("◐ api", "WindowsTerminal.exe")).Group?.Key == "agents");
            string custom = v030.Replace("\"group\": \"agents\"", "\"group\": \"term\"");
            File.WriteAllText(Paths.Rules, custom.Replace(Rules.OldClaudeTitle, Rules.OldClaudeTitle.Replace("2733", "2734")));
            string before = File.ReadAllText(Paths.Rules);
            store.Load();
            Check("a rules.json whose Claude Code rule was edited is left as it is", File.ReadAllText(Paths.Rules) == before);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    /// <summary>Ctrl+number or a drag on one terminal tab moves that tab, keyed by window and name without its mark.</summary>
    static void TabMoves()
    {
        var r = Rules.Parse(Rules.Default);
        var tabA = new WinInfo((IntPtr)5, "◐ api", "WindowsTerminal.exe", "Terminal", null, null, false, new TermTab("◐ api", 0, 2, true));
        var tabB = tabA with { Title = "PowerShell", Tab = new TermTab("PowerShell", 1, 2, false) };
        var moved = new Dictionary<WinKey, string> { [WinKey.Of(tabA)] = "web" };
        Placement P(WinInfo w) => r.Place(WinKey.Of(w), F(w.Title, w.Exe), moved);
        Check("a moved tab goes where it was moved", P(tabA) is { Level: RuleLevel.Window, Group.Key: "web" });
        Check("the window's other tab stays put", P(tabB).Group?.Key == "term");
        var spun = tabA with { Title = "◑ api", Tab = new TermTab("◑ api", 0, 2, true) };
        Check("the move survives the spinner turning (◐ to ◑)", WinKey.Of(spun) == WinKey.Of(tabA) && P(spun).Group?.Key == "web");
        var plain = new WinInfo((IntPtr)5, "◐ api", "WindowsTerminal.exe", "Terminal", null, null, false);
        Check("a window with no tab cards keys by window alone", WinKey.Of(plain) == new WinKey((IntPtr)5, null) && P(plain).Group?.Key == "agents");
    }

    /// <summary>Claude Code's status files, written to a temp folder in the shape v2.1.308 uses.</summary>
    static void Sessions()
    {
        string dir = Path.Combine(Path.GetTempPath(), "altlane-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        int live = Environment.ProcessId;
        (string name, AgentInfo info)? S(string json)
        {
            string p = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(p, json);
            return Terminals.ParseSession(p);
        }
        string J(string status, string entry = "cli", int? pid = null, string? name = "api fix") =>
            $$"""{"pid":{{pid ?? live}},"sessionId":"x","cwd":"Y:\\Projects\\Altitude","name":{{(name == null ? "null" : "\"" + name + "\"")}},"status":"{{status}}","statusUpdatedAt":{{DateTimeOffset.UtcNow.AddMinutes(-3).ToUnixTimeMilliseconds()}},"entrypoint":"{{entry}}"}""";
        try
        {
            var w = S(J("waiting"));
            Check("a waiting terminal session needs you, with its project and since when",
                w is { name: "api fix", info: { State: "needs", Project: "Altitude" } } && Math.Abs((DateTime.Now - w.Value.info.Since).TotalMinutes - 3) < 0.1,
                w?.ToString() ?? "null");
            Check("busy is working, anything else idle", S(J("busy"))?.info.State == "working" && S(J("idle"))?.info.State == "idle" && S(J("odd"))?.info.State == "idle");
            Check("the desktop app's and SDK sessions have no tab, so they're skipped", S(J("waiting", "claude-desktop")) == null && S(J("waiting", "sdk-cli")) == null);
            Check("a session whose process has gone is skipped", S(J("waiting", pid: 999_999_9)) == null);
            Check("a session with no name is skipped", S(J("waiting", name: null)) == null);
            Check("a half-written file is skipped", S("{\"pid\": 1") == null && S("[]") == null);
            Check("Ago reads minutes", Terminals.Ago(DateTime.Now.AddMinutes(-3)) == "3 min" && Terminals.Ago(DateTime.Now) == "just now" && Terminals.Ago(DateTime.Now.AddMinutes(-75)) == "1 h 15 min");
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>A Windows Terminal window whose three tabs each have a card.</summary>
    static void TabSections()
    {
        var r = Rules.Parse(Rules.Default);
        AgentInfo A(string state) => new(state, DateTime.Now, "Altitude");
        Item T(int i, string title, AgentInfo? a, bool cur = false)
        {
            var w = new WinInfo((IntPtr)9, title, "WindowsTerminal.exe", "Terminal", null, null, false, new TermTab(title, i, 3, i == 0), a);
            return new Item(w, r.Place(WinKey.Of(w), F(title, w.Exe)), cur);
        }
        Item W(int h, string title, string exe) { var w = new WinInfo((IntPtr)h, title, exe, exe, null, null, false); return new Item(w, r.Place(w.Hwnd, F(title, exe, null, r.IsBrowser(exe))), false); }
        var mru = new List<Item>
        {
            T(0, "◐ Win+Tab hover fix", A("working"), cur: true),
            T(1, "✳ Release notes", A("needs")),
            T(2, "✳ Daily report", A("idle")),
            W(2, "Inbox", "chrome.exe"),
            W(3, "Claude", "claude.exe"),
            W(4, "Downloads", "explorer.exe"),
            W(5, "todo.txt", "notepad.exe"),
        };
        var secs = Sections.Build(mru, r.Groups, "");
        Check("Recent skips tabs that aren't in front", secs[0].Items.Select(i => (int)i.W.Hwnd).SequenceEqual(new[] { 2, 3, 4 }));
        var agents = secs.First(s => s.Key == "agents");
        Check("each tab is its own card in its group", agents.Items.Count(i => i.W.Hwnd == (IntPtr)9) == 3);
        var flat = Sections.Flat(secs);
        Check("tab cards of one window are distinct slots", flat.Where(s => s.Hwnd == (IntPtr)9).Distinct().Count() == 3);
        Check("a tab card is found again by its slot", Sections.ItemAt(secs, new Slot("agents", (IntPtr)9, 2))?.W.Title == "✳ Daily report");
        Check("without needs-first, Agents keeps most-recent order",
            string.Join("|", agents.Items.Select(i => i.W.Title)) == "◐ Win+Tab hover fix|✳ Release notes|✳ Daily report|Claude");
        var first = Sections.Build(mru, r.Groups, "", needsFirst: true).First(s => s.Key == "agents");
        Check("with needs-first, the session waiting for you leads Agents, the rest in order",
            string.Join("|", first.Items.Select(i => i.W.Title)) == "✳ Release notes|◐ Win+Tab hover fix|✳ Daily report|Claude");
        Check("Alt+number skips hidden tabs and the window in front", Sections.Direct(mru, r.ByKey("agents")!)?.W.Hwnd == (IntPtr)3);
        // The overlay keys its cards by slot, so two tabs sharing a slot would draw one card.
        bool Distinct(Layouts.Result L) => L.cards.Select(c => c.Slot).Distinct().Count() == L.cards.Count && L.cards.Count(c => c.Slot.Hwnd == (IntPtr)9) == 3;
        Check("the grid gives each tab card its own slot", Distinct(Layouts.Grid(secs, 1700, 900)));
        Check("the columns give each tab card its own slot", Distinct(Layouts.Columns(secs, 1700, 900)));
        Check("the lanes give each tab card its own slot", Distinct(Layouts.Lanes(secs, 1900, 1000).L));
    }

    /// <summary>Where the overlay lands on monitors this PC may not have: left of or above the primary, at other scales.</summary>
    static void MonitorPlacement()
    {
        bool Inside((int x, int y) p, int l, int t, int ww, int wh, int w, int h) => p.x >= l && p.y >= t && p.x + w <= l + ww && p.y + h <= t + wh;
        Check("centered on the primary monitor", Layouts.Center(0, 0, 1920, 1040, 800, 600, 1.0, false) == (560, 220));
        var left = Layouts.Center(-2560, 0, 2560, 1392, 1200, 900, 1.5, false);
        Check("centered on a 150% monitor left of the primary", left == (-1880, 246) && Inside(left, -2560, 0, 2560, 1392, 1200, 900));
        var above = Layouts.Center(0, -1440, 2560, 1400, 1000, 800, 1.0, false);
        Check("centered on a monitor above the primary", above == (780, -1140) && Inside(above, 0, -1440, 2560, 1400, 1000, 800));
        Check("a toast sits 72 scaled pixels above the bottom of the work area", Layouts.Center(0, 0, 1920, 1032, 300, 40, 1.25, true) == (810, 902));
        Check("a window bigger than the work area keeps its corner on that monitor", Layouts.Center(-1920, 0, 1920, 1040, 2000, 1100, 1.0, false) == (-1920, 0));
    }

    static void UsageCounts()
    {
        var u = new Usage { Days = 7 };
        foreach (var line in new[]
        {
            """{"t":1,"src":"app","ev":"switch","how":"alt_up","quick":false,"overview":false,"section":"recent","group":"term","ok":true,"from_key_ms":1000,"pick":"tab","nav":{"tab":2}}""",
            """{"t":2,"src":"app","ev":"switch","how":"click","quick":false,"overview":false,"section":"agents","group":"agents","ok":true,"from_key_ms":2000,"pick":"mouse","nav":{}}""",
            """{"t":3,"src":"app","ev":"switch","how":"alt_up","quick":true,"overview":false,"section":"recent","group":"web","ok":true,"from_key_ms":40}""",
            """{"t":4,"src":"app","ev":"switch","how":"alt_up","quick":false,"overview":false,"section":"recent","group":"web","ok":false,"from_key_ms":3000}""",
            """{"t":5,"src":"app","ev":"switch","how":"enter","quick":false,"overview":true,"section":"files","group":"files","ok":true,"from_key_ms":9000,"pick":"search","nav":{"typed":3}}""",
            """{"t":6,"src":"app","ev":"session_start","kind":"overview"}""",
            """{"t":7,"src":"app","ev":"regroup","via":"drag","group":"term"}""",
            """{"t":8,"src":"selftest","ev":"switch","how":"alt_up","quick":true}""",
            """not json""",
        }) u.Add(line);
        Check("usage counts switches, landings and quick taps from the app's lines only", u.Switches == 5 && u.Landed == 4 && u.Quick == 1, $"{u.Switches} {u.Landed} {u.Quick}");
        Check("usage's median covers switches that drew Alt+Tab (not quick, not the overview)", u.MedianMs == 2000, $"{u.MedianMs}");
        Check("usage sorts picks, with 0.3.0's key releases as unrecorded",
            u.Picks.GetValueOrDefault("keys") == 1 && u.Picks.GetValueOrDefault("mouse") == 1 && u.Picks.GetValueOrDefault("quick") == 1
            && u.Picks.GetValueOrDefault("unrecorded") == 1 && u.Picks.GetValueOrDefault("search") == 1);
        Check("usage counts sections and groups apart", u.Sections["recent"] == 3 && u.Groups["web"] == 2 && u.Typed == 1 && u.Drags == 1 && u.Overviews == 1);
        var unused = u.Unused(new Settings(), Rules.Parse(Rules.Default).Groups);
        Check("usage lists what went unused, and what was used once",
            unused.Contains("Alt+`") && unused.Contains("Ctrl+number") && unused.Contains("Win+Tab (once)") && unused.Contains("Creative") && !unused.Contains("Web")
            && !unused.Contains("Alt+number"), string.Join(", ", unused));
    }
}
