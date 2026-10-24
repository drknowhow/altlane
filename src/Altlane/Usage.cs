using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Altlane;

/// <summary>
/// What the usage window shows, counted from Altlane's own daily logs. The logs hold process names,
/// timings and how each window was picked, never titles, so neither does this.
/// </summary>
sealed class Usage
{
    public int Days, Files, Switches, Landed, Quick, Overviews, SameGroup, Typed, Regroups, Drags, Closes, Direct;
    public readonly List<double> ShownMs = new();
    /// <summary>quick, keys, mouse, number, search; "unrecorded" for switches logged before 0.4.0.</summary>
    public readonly Dictionary<string, int> Picks = new();
    /// <summary>By section: "recent" or a group key.</summary>
    public readonly Dictionary<string, int> Sections = new();
    /// <summary>By the group of the window switched to, whichever section it was picked from.</summary>
    public readonly Dictionary<string, int> Groups = new();

    /// <summary>Median time from Alt+Tab to the switch, for switches that drew the view.</summary>
    public double? MedianMs
    {
        get
        {
            if (ShownMs.Count == 0) return null;
            var a = ShownMs.OrderBy(x => x).ToList();
            return a.Count % 2 == 1 ? a[a.Count / 2] : (a[a.Count / 2 - 1] + a[a.Count / 2]) / 2;
        }
    }

    /// <summary>The last <paramref name="days"/> days of logs in <paramref name="dir"/>, today included.</summary>
    public static Usage Read(string dir, int days, DateTime? today = null)
    {
        var u = new Usage { Days = days };
        var from = (today ?? DateTime.Today).AddDays(-(days - 1));
        string[] files;
        try { files = Directory.GetFiles(dir, "altlane-*.jsonl"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return u; }
        foreach (var f in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            string stem = Path.GetFileNameWithoutExtension(f);
            if (!DateTime.TryParseExact(stem["altlane-".Length..], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || day < from) continue;
            try
            {
                // Today's log is open for writing in this process.
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(fs);
                u.Files++;
                while (r.ReadLine() is string line) u.Add(line);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return u;
    }

    /// <summary>Count one log line. Lines from other sources (the self-test, --list) and broken lines are skipped.</summary>
    public void Add(string line)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return; }
        using (doc)
        {
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object || Str(o, "src") != "app") return;
            switch (Str(o, "ev"))
            {
                case "switch": Switch(o); break;
                case "session_start":
                    if (Str(o, "kind") == "overview") Overviews++;
                    else if (Str(o, "kind") == "same_group") SameGroup++;
                    break;
                case "regroup": if (Str(o, "via") == "drag") Drags++; else Regroups++; break;
                case "close_window" or "close_tab": Closes++; break;
                case "direct": Direct++; break;
            }
        }
    }

    void Switch(JsonElement o)
    {
        Switches++;
        if (Bool(o, "ok")) Landed++;
        bool quick = Bool(o, "quick"), overview = Bool(o, "overview");
        if (quick) Quick++;
        if (!quick && !overview && o.TryGetProperty("from_key_ms", out var ms) && ms.TryGetDouble(out var x)) ShownMs.Add(x);
        string pick = Str(o, "pick") switch
        {
            "quick" => "quick",
            "mouse" => "mouse",
            "number" => "number",
            "search" => "search",
            "tab" or "grave" or "arrow" or "first" => "keys",
            // Before 0.4.0 the log said only whether Alt was released or a card was clicked.
            _ => quick ? "quick" : Str(o, "how") == "click" ? "mouse" : "unrecorded",
        };
        Picks[pick] = Picks.GetValueOrDefault(pick) + 1;
        if (pick == "search" || o.TryGetProperty("nav", out var nav) && nav.ValueKind == JsonValueKind.Object
            && nav.TryGetProperty("typed", out var typed) && typed.TryGetInt32(out int n) && n > 0) Typed++;
        if (Str(o, "section") is string sec) Sections[sec] = Sections.GetValueOrDefault(sec) + 1;
        if (Str(o, "group") is string grp) Groups[grp] = Groups.GetValueOrDefault(grp) + 1;
    }

    static string? Str(JsonElement o, string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool Bool(JsonElement o, string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Features and groups used at most once in these days, as the window lists them.</summary>
    public List<string> Unused(Settings s, IReadOnlyList<Group> groups)
    {
        var list = new List<string>();
        void Check(string name, int n, bool on = true) { if (on && n <= 1) list.Add(n == 0 ? name : name + " (once)"); }
        Check("Win+Tab", Overviews, s.WinTabLanes);
        Check("Alt+`", SameGroup, s.SameGroupKey);
        Check("Typing to filter", Typed);
        Check("Ctrl+number", Regroups);
        Check("Drag to a group", Drags, s.MouseActions);
        Check("Del or middle-click to close", Closes);
        Check("Alt+number", Direct, s.DirectJump);
        foreach (var g in groups) Check(g.Name, Groups.GetValueOrDefault(g.Key));
        return list;
    }
}
