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
    }
}
