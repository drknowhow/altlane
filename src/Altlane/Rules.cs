using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Altlane;

sealed record Group(string Key, string Name, string Color, int Num);

/// <summary>Which kind of rule put a window in its group. The order is the order rules are checked.</summary>
enum RuleLevel { Window, Hide, Title, WebApp, Site, App, Browser, Other }

/// <summary>What a window looks like to the rules.</summary>
sealed record WinFacts(string Title, string Exe, string? AppId, bool IsBrowser);

/// <summary>A window, or one Windows Terminal tab of it by its title without the status mark, as Ctrl+number remembers it.</summary>
readonly record struct WinKey(IntPtr Hwnd, string? Tab)
{
    public static WinKey Of(WinInfo w) => new(w.Hwnd, w.Tab == null ? null : Terminals.Bare(w.Tab.Name));
}

/// <param name="Group">null when a hide rule matched.</param>
/// <param name="Why">One sentence for the overlay footer.</param>
sealed record Placement(Group? Group, RuleLevel Level, string Why);

/// <summary>
/// rules.json: the groups, and which windows go where. Parsed leniently (comments and trailing commas
/// allowed) and never rewritten wholesale: Ctrl+number edits only the "apps" and "webApps" entries,
/// and only when the file parses. A file with an error leaves the last good rules in force.
/// </summary>
sealed class Rules
{
    public IReadOnlyList<Group> Groups { get; private set; } = Array.Empty<Group>();
    public List<string> Warnings { get; } = new();

    record TitleRule(Regex? Match, string? Contains, string? StartsWith, string? App, Group Group, string? Why);
    record SiteRule(string Contains, Group Group);
    record HideRule(string Exe, string? TitleContains);

    readonly List<TitleRule> _titles = new();
    readonly List<SiteRule> _sites = new();
    readonly List<HideRule> _hide = new();
    readonly Dictionary<string, Group> _apps = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (Group g, string? name)> _webApps = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _browsers = new(StringComparer.OrdinalIgnoreCase);
    Group? _browserGroup, _otherGroup;

    static readonly JsonDocumentOptions ReadOpts = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public bool IsBrowser(string exe) => _browsers.Contains(exe);
    public Group? ByNum(int n) => n >= 1 && n <= Groups.Count ? Groups[n - 1] : null;
    public Group? ByKey(string key) => Groups.FirstOrDefault(g => g.Key == key);

    /// <summary>Parse rules text. Throws JsonException (with line info) when the file is not valid JSON.</summary>
    public static Rules Parse(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: ReadOpts) as JsonObject ?? throw new JsonException("rules.json must be a JSON object");
        var r = new Rules();

        var groups = new List<Group>();
        foreach (var g in root["groups"] as JsonArray ?? new JsonArray())
        {
            string? key = Str(g?["key"]), name = Str(g?["name"]);
            if (key == null || name == null) { r.Warnings.Add("A group needs a key and a name; one was skipped."); continue; }
            if (groups.Count == 9) { r.Warnings.Add($"Only 9 groups fit on the number keys; \"{name}\" was skipped."); continue; }
            if (groups.Any(x => x.Key == key)) { r.Warnings.Add($"Group key \"{key}\" appears twice; the second was skipped."); continue; }
            groups.Add(new Group(key, name, Str(g?["color"]) ?? "#8a8f98", groups.Count + 1));
        }
        if (groups.Count == 0) throw new JsonException("rules.json lists no groups");
        r.Groups = groups;

        Group? Find(string? key, string where)
        {
            if (key == null) { r.Warnings.Add($"A rule in \"{where}\" names no group; it was skipped."); return null; }
            var g = groups.FirstOrDefault(x => x.Key == key);
            if (g == null) r.Warnings.Add($"A rule in \"{where}\" names group \"{key}\", which isn't in \"groups\"; it was skipped.");
            return g;
        }

        foreach (var t in root["titles"] as JsonArray ?? new JsonArray())
        {
            var g = Find(Str(t?["group"]), "titles");
            if (g == null) continue;
            Regex? rx = null;
            if (Str(t?["match"]) is string pat)
            {
                try { rx = new Regex(pat, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(20)); }
                catch (ArgumentException e) { r.Warnings.Add($"The title pattern {pat} isn't a valid regular expression ({e.Message}); it was skipped."); continue; }
            }
            string? contains = Str(t?["contains"]), starts = Str(t?["startsWith"]);
            if (rx == null && contains == null && starts == null) { r.Warnings.Add("A title rule needs \"match\", \"contains\" or \"startsWith\"; one was skipped."); continue; }
            r._titles.Add(new TitleRule(rx, contains, starts, Str(t?["app"]), g, Str(t?["why"])));
        }

        foreach (var s in root["sites"] as JsonArray ?? new JsonArray())
        {
            var g = Find(Str(s?["group"]), "sites");
            if (g == null) continue;
            if (Str(s?["contains"]) is string c && c.Length > 0) r._sites.Add(new SiteRule(c, g));
            else r.Warnings.Add("A site rule needs \"contains\"; one was skipped.");
        }

        if (root["apps"] is JsonObject apps)
            foreach (var (exe, v) in apps)
                if (Find(Str(v), "apps") is Group g) r._apps[Exe(exe)] = g;

        foreach (var w in root["webApps"] as JsonArray ?? new JsonArray())
        {
            var g = Find(Str(w?["group"]), "webApps");
            if (g != null && Str(w?["id"]) is string id) r._webApps[id] = (g, Str(w?["name"]));
        }

        foreach (var h in root["hide"] as JsonArray ?? new JsonArray())
        {
            if (h is JsonValue && Str(h) is string exe) r._hide.Add(new HideRule(Exe(exe), null));
            else if (Str(h?["app"]) is string app) r._hide.Add(new HideRule(Exe(app), Str(h?["titleContains"])));
        }

        foreach (var b in root["browsers"] as JsonArray ?? new JsonArray())
            if (Str(b) is string exe) r._browsers.Add(Exe(exe));

        r._browserGroup = Str(root["browserGroup"]) is string bg ? Find(bg, "browserGroup") : null;
        r._otherGroup = (Str(root["otherGroup"]) is string og ? Find(og, "otherGroup") : null) ?? groups[^1];
        return r;
    }

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>"Chrome" and "chrome.exe" both mean chrome.exe.</summary>
    static string Exe(string s) => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s : s + ".exe";

    /// <param name="key">What <paramref name="moved"/> knows the window by: its handle, or a terminal tab's <see cref="WinKey"/>.</param>
    /// <param name="moved">Windows moved with Ctrl+number that can't be stated as a lasting rule, by group
    /// key. Kept outside the rules so a reload of rules.json doesn't drop them.</param>
    public Placement Place<TKey>(TKey key, WinFacts w, IReadOnlyDictionary<TKey, string>? moved = null) where TKey : notnull
    {
        if (moved != null && moved.TryGetValue(key, out var gkey) && ByKey(gkey) is Group wg)
            return new(wg, RuleLevel.Window, $"You moved this window here with Ctrl+{wg.Num}. It stays here until it closes.");

        foreach (var h in _hide)
            if (h.Exe.Equals(w.Exe, StringComparison.OrdinalIgnoreCase) &&
                (h.TitleContains == null || w.Title.Contains(h.TitleContains, StringComparison.OrdinalIgnoreCase)))
                return new(null, RuleLevel.Hide, "Hidden by a rule.");

        foreach (var t in _titles)
        {
            if (t.App != null && !Exe(t.App).Equals(w.Exe, StringComparison.OrdinalIgnoreCase)) continue;
            bool hit;
            try
            {
                hit = (t.Match?.IsMatch(w.Title) ?? true)
                      && (t.Contains == null || w.Title.Contains(t.Contains, StringComparison.OrdinalIgnoreCase))
                      && (t.StartsWith == null || w.Title.StartsWith(t.StartsWith, StringComparison.OrdinalIgnoreCase));
            }
            catch (RegexMatchTimeoutException) { hit = false; }
            if (hit) return new(t.Group, RuleLevel.Title, t.Why ?? $"Title rule: this title goes to {t.Group.Name}.");
        }

        bool webApp = w.AppId != null && w.AppId.Contains("_crx_", StringComparison.OrdinalIgnoreCase);
        if (w.AppId != null && _webApps.TryGetValue(w.AppId, out var wa))
            return new(wa.g, RuleLevel.WebApp, $"Web app rule: {wa.name ?? "this installed web app"} goes to {wa.g.Name}.");

        if (w.IsBrowser)
            foreach (var s in _sites)
                if (w.Title.Contains(s.Contains, StringComparison.OrdinalIgnoreCase))
                    return new(s.Group, RuleLevel.Site,
                        $"Site rule: a {(webApp ? "web app" : "browser")} title containing “{s.Contains}” goes to {s.Group.Name}.");

        if (_apps.TryGetValue(w.Exe, out var ag))
            return new(ag, RuleLevel.App, $"App rule: {w.Exe} goes to {ag.Name}.");

        if (w.IsBrowser && _browserGroup != null)
            return new(_browserGroup, RuleLevel.Browser, webApp
                ? $"An installed web app that no rule claims goes to {_browserGroup.Name}."
                : $"A browser window that no site rule claims goes to {_browserGroup.Name}.");

        return new(_otherGroup, RuleLevel.Other, $"No rule names {w.Exe}, so it goes to {_otherGroup!.Name}.");
    }

    /// <summary>
    /// How Ctrl+number on this window should be remembered: as an app rule when the window got its group
    /// from its app (or from nothing), as a web-app rule for an installed web app, and otherwise only for
    /// this window, because a title or a browser tab isn't a stable thing to write a rule about.
    /// </summary>
    public static (string kind, string? key) MoveScope(Placement p, WinFacts w)
    {
        bool webApp = w.AppId != null && w.AppId.Contains("_crx_", StringComparison.OrdinalIgnoreCase);
        return p.Level switch
        {
            RuleLevel.App or RuleLevel.Other when !w.IsBrowser => ("app", w.Exe),
            RuleLevel.WebApp => ("webApp", w.AppId),
            RuleLevel.Site or RuleLevel.Browser when webApp => ("webApp", w.AppId),
            _ => ("window", null),
        };
    }

    /// <summary>Write one app or web-app rule into the file. Returns null on success, or why it couldn't.</summary>
    public static string? SaveRule(string path, string kind, string key, string groupKey, string? name)
    {
        JsonObject root;
        try { root = JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOpts) as JsonObject ?? throw new JsonException("not an object"); }
        catch (Exception e) when (e is JsonException or IOException) { return "rules.json has an error, so nothing was saved (" + e.Message + ")"; }

        if (kind == "app")
        {
            if (root["apps"] is not JsonObject apps) root["apps"] = apps = new JsonObject();
            string existing = apps.Select(kv => kv.Key).FirstOrDefault(k => Exe(k).Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
            apps[existing] = groupKey;
        }
        else
        {
            if (root["webApps"] is not JsonArray list) root["webApps"] = list = new JsonArray();
            var hit = list.OfType<JsonObject>().FirstOrDefault(o => string.Equals(Str(o["id"]), key, StringComparison.OrdinalIgnoreCase));
            if (hit != null) hit["group"] = groupKey;
            else list.Add(new JsonObject { ["id"] = key, ["name"] = name, ["group"] = groupKey });
        }
        try
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(WriteOpts));
            File.Move(tmp, path, overwrite: true);
            return null;
        }
        catch (IOException e) { return "rules.json couldn't be written (" + e.Message + ")"; }
    }

    /// <summary>
    /// The Claude Code title rule as earlier versions wrote it into rules.json, and as it reads now that
    /// Claude Code marks a working terminal ◐ ◑. Both as JSON text: RuleStore swaps the first for the
    /// second where the file still has it exactly, and leaves a pattern the user changed alone.
    /// </summary>
    public const string OldClaudeTitle = """
"match": "^[\\u2733\\u2800-\\u28FF] ",
""";
    public const string ClaudeTitle = """
"match": "^[\\u2733\\u25D0-\\u25D3\\u2800-\\u28FF] ",
""";
    public const string OldClaudeWhy = "(✳ when idle, a spinner while it works)";
    public const string ClaudeWhy = "(✳ when idle, ◐ ◑ or a spinner while it works)";

    /// <summary>The rules a new install starts with.</summary>
    public const string Default = """
{
  "about": "Altlane's rules. Groups are numbered 1 to 9 in the order listed. A window goes to the first rule that claims it, checked in this order: a window you moved with Ctrl+number (until it closes), hide, titles, webApps, sites, apps, then any browser window goes to browserGroup and anything else to otherGroup. Ctrl+number on a window writes its app (or installed web app) here. Save the file and Altlane reloads it.",
  "groups": [
    { "key": "agents", "name": "Agents", "color": "#d97745" },
    { "key": "term", "name": "Terminals", "color": "#4c9a5f" },
    { "key": "web", "name": "Web", "color": "#3a7bd5" },
    { "key": "create", "name": "Creative", "color": "#b0569f" },
    { "key": "files", "name": "Files", "color": "#c99a1e" },
    { "key": "docs", "name": "Docs & notes", "color": "#2b9aa0" },
    { "key": "other", "name": "Other", "color": "#8a8f98" }
  ],
  "titles": [
    {
      "match": "^[\\u2733\\u25D0-\\u25D3\\u2800-\\u28FF] ",
      "group": "agents",
      "why": "The title starts with the mark Claude Code puts on its terminal (✳ when idle, ◐ ◑ or a spinner while it works), so it counts as an agent."
    }
  ],
  "sites": [
    { "contains": "Google Flow", "group": "create" },
    { "contains": "Suno", "group": "create" },
    { "contains": "Higgsfield", "group": "create" }
  ],
  "webApps": [],
  "apps": {
    "claude.exe": "agents",
    "ChatGPT.exe": "agents",
    "c3-desk-app.exe": "agents",
    "WindowsTerminal.exe": "term",
    "OpenConsole.exe": "term",
    "cmd.exe": "term",
    "powershell.exe": "term",
    "pwsh.exe": "term",
    "wezterm-gui.exe": "term",
    "alacritty.exe": "term",
    "mintty.exe": "term",
    "blender.exe": "create",
    "FreeCAD.exe": "create",
    "shotcut.exe": "create",
    "obs64.exe": "create",
    "Resolve.exe": "create",
    "Audacity.exe": "create",
    "krita.exe": "create",
    "inkscape.exe": "create",
    "Photoshop.exe": "create",
    "explorer.exe": "files",
    "notepad.exe": "docs",
    "notepad++.exe": "docs",
    "WINWORD.EXE": "docs",
    "EXCEL.EXE": "docs",
    "POWERPNT.EXE": "docs",
    "ONENOTE.EXE": "docs",
    "Obsidian.exe": "docs",
    "Code.exe": "docs",
    "Acrobat.exe": "docs",
    "SumatraPDF.exe": "docs"
  },
  "hide": [
    { "app": "SamsungMagician.exe", "titleContains": "Notify" }
  ],
  "browsers": ["chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "arc.exe", "zen.exe"],
  "browserGroup": "web",
  "otherGroup": "other"
}
""";
}
