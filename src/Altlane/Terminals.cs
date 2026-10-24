using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Threading;

namespace Altlane;

/// <summary>One Windows Terminal tab, as UI Automation lists it. Index counts from 0.</summary>
sealed record TermTab(string Name, int Index, int Count, bool Selected);

/// <summary>
/// A Claude Code session's state from Claude Code's own status file. State is "needs" (waiting for
/// you), "working" or "idle"; Project is the last folder of its working directory.
/// </summary>
sealed record AgentInfo(string State, DateTime Since, string? Project);

/// <summary>
/// Windows Terminal's tabs and Claude Code's session states, kept on a background thread so Alt+Tab
/// never waits for UI Automation. Tab lists are refreshed when a session starts (Alt+Tab's show delay
/// usually covers it) and every few seconds while Altlane runs. Session states come from
/// %USERPROFILE%\.claude\sessions\&lt;pid&gt;.json, which Claude Code writes; a terminal session's tab
/// title is its mark (✳ ◐ ◑ …), a space and the session's name, which is how a tab finds its session.
/// Nothing here is logged except counts and timings.
/// </summary>
sealed class Terminals : IDisposable
{
    public const string Exe = "WindowsTerminal.exe";
    const int PeriodMs = 5000;

    readonly Dispatcher _ui;
    string? _sessionsDir;
    readonly BlockingCollection<Action> _q = new();
    readonly Thread _worker;
    readonly object _lock = new();
    readonly Timer _timer;
    FileSystemWatcher? _fsw;
    Timer? _debounce;
    Dictionary<IntPtr, List<TermTab>> _tabs = new();
    Dictionary<string, AgentInfo> _agents = new(StringComparer.Ordinal);
    // When each tab (window, name without its mark) was last seen in front, so a window's other tabs
    // can be listed most recent first.
    readonly Dictionary<(IntPtr, string), long> _lastSelected = new();
    int _refreshQueued;
    Func<IReadOnlyList<IntPtr>>? _windows;

    /// <summary>Raised on the UI thread when a refresh found tabs or session states that differ from before.</summary>
    public event Action? Changed;

    /// <param name="sessionsDir">Claude Code's session folder; null turns session states off.</param>
    public Terminals(Dispatcher ui, string? sessionsDir)
    {
        _ui = ui;
        _worker = new Thread(Run) { IsBackground = true, Name = "Altlane terminals" };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
        _timer = new Timer(_ => Tick(), null, PeriodMs, PeriodMs);
        Sessions(sessionsDir);
    }

    void Tick()
    {
        Refresh();
        // Claude Code installed since Altlane started: its session folder exists now.
        if (_sessionsDir != null && _fsw == null && Directory.Exists(_sessionsDir)) _ui.BeginInvoke(() => Sessions(_sessionsDir));
    }

    public static string DefaultSessionsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");

    public static bool IsTerminal(WinInfo w) => w.Exe.Equals(Exe, StringComparison.OrdinalIgnoreCase);

    /// <summary>Where the background refresh gets the Windows Terminal windows to look at (it runs off the UI thread).</summary>
    public void Source(Func<IReadOnlyList<IntPtr>> windows) => _windows = windows;

    void Run()
    {
        foreach (var job in _q.GetConsumingEnumerable())
        {
            try { job(); }
            catch (Exception e) { Log.Write("terminals_error", new { error = e.GetType().Name + ": " + e.Message }); }
        }
    }

    // ---------- reading ----------

    /// <summary>The tabs of a Windows Terminal window as last seen, or null when it hasn't been read yet.</summary>
    public List<TermTab>? TabsOf(IntPtr hwnd) { lock (_lock) return _tabs.TryGetValue(hwnd, out var t) ? t : null; }

    /// <summary>The Claude Code session whose name a tab (or window) title carries, when exactly one does.</summary>
    public AgentInfo? AgentFor(string title) { lock (_lock) return _agents.TryGetValue(Bare(title), out var a) ? a : null; }

    /// <summary>When a tab was last seen in front; 0 when never.</summary>
    public long LastSelected(IntPtr hwnd, string name) { lock (_lock) return _lastSelected.GetValueOrDefault((hwnd, Bare(name))); }

    /// <summary>How long ago, for "Waiting for you · 3 min".</summary>
    public static string Ago(DateTime since)
    {
        var d = DateTime.Now - since;
        return d.TotalMinutes < 1 ? "just now" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min" : $"{(int)d.TotalHours} h {d.Minutes} min";
    }

    /// <summary>A title without the status mark Claude Code (or anything else) puts first: "◐ fix bug" is "fix bug".</summary>
    public static string Bare(string title) =>
        title.Length > 2 && title[1] == ' ' && !char.IsLetterOrDigit(title[0]) ? title[2..] : title;

    // ---------- refreshing ----------

    /// <summary>Re-read every Windows Terminal window's tabs, off the UI thread. Requests made while one is queued fold into it.</summary>
    public void Refresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1 || _q.IsAddingCompleted) return;
        try { _q.Add(ReadAllTabs); } catch (InvalidOperationException) { }
    }

    void ReadAllTabs()
    {
        Interlocked.Exchange(ref _refreshQueued, 0);
        var hwnds = _windows?.Invoke() ?? Array.Empty<IntPtr>();
        long t0 = Stopwatch.GetTimestamp();
        var fresh = new Dictionary<IntPtr, List<TermTab>>();
        foreach (var h in hwnds)
            if (ReadTabs(h) is { } tabs) fresh[h] = tabs;
        bool changed;
        lock (_lock)
        {
            changed = fresh.Count != _tabs.Count || fresh.Any(kv => !_tabs.TryGetValue(kv.Key, out var old) || !Same(old, kv.Value));
            _tabs = fresh;
            long now = Environment.TickCount64;
            foreach (var (h, tabs) in fresh)
                foreach (var t in tabs.Where(t => t.Selected)) _lastSelected[(h, Bare(t.Name))] = now;
            foreach (var k in _lastSelected.Keys.Where(k => !fresh.ContainsKey(k.Item1)).ToList()) _lastSelected.Remove(k);
        }
        double ms = Clock.Between(t0, Stopwatch.GetTimestamp());
        if (ms > 250) Log.Write("terminals_slow", new { windows = hwnds.Count, ms = Math.Round(ms, 1) });
        if (changed) _ui.BeginInvoke(() => Changed?.Invoke());
    }

    /// <summary>A window's tab titles as they stand. Spinner marks change several times a second, so they don't count as a change.</summary>
    static bool Same(List<TermTab> a, List<TermTab> b) =>
        a.Count == b.Count && a.Zip(b).All(p => Bare(p.First.Name) == Bare(p.Second.Name) && p.First.Selected == p.Second.Selected);

    static readonly Condition TabItems = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);

    static List<TermTab>? ReadTabs(IntPtr hwnd)
    {
        if (!N.IsWindow(hwnd)) return null;
        var cache = new CacheRequest();
        cache.Add(AutomationElement.NameProperty);
        cache.Add(SelectionItemPattern.IsSelectedProperty);
        using (cache.Activate())
        {
            AutomationElementCollection items;
            try { items = AutomationElement.FromHandle(hwnd).FindAll(TreeScope.Descendants, TabItems); }
            catch (Exception e) when (e is ElementNotAvailableException or ArgumentException or InvalidOperationException) { return null; }
            var list = new List<TermTab>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                string name = items[i].Cached.Name ?? "";
                bool sel = items[i].GetCachedPropertyValue(SelectionItemPattern.IsSelectedProperty) is true;
                list.Add(new TermTab(name, i, items.Count, sel));
            }
            return list;
        }
    }

    /// <summary>The UI Automation element of one tab, found again by position and checked by name.</summary>
    static AutomationElement? FindTab(IntPtr hwnd, TermTab tab)
    {
        if (!N.IsWindow(hwnd)) return null;
        var items = AutomationElement.FromHandle(hwnd).FindAll(TreeScope.Descendants, TabItems);
        if (tab.Index < items.Count && Bare(items[tab.Index].Current.Name) == Bare(tab.Name)) return items[tab.Index];
        // The tabs moved since the list was read: find it by name, if only one tab has it.
        var byName = Enumerable.Range(0, items.Count).Select(i => items[i]).Where(e => Bare(e.Current.Name) == Bare(tab.Name)).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    /// <summary>Bring a tab to the front of its window, off the UI thread; done gets (ok, ms) on the UI thread.</summary>
    public void SelectTab(IntPtr hwnd, TermTab tab, Action<bool, double> done)
    {
        Run(() =>
        {
            long t0 = Stopwatch.GetTimestamp();
            bool ok = false;
            try
            {
                if (FindTab(hwnd, tab)?.GetCurrentPattern(SelectionItemPattern.Pattern) is SelectionItemPattern p) { p.Select(); ok = true; }
            }
            catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException) { }
            double ms = Clock.Between(t0, Stopwatch.GetTimestamp());
            _ui.BeginInvoke(() => done(ok, ms));
            Refresh();
        });
    }

    /// <summary>Close one tab with its own close button, as a click on it would; done gets ok on the UI thread.</summary>
    public void CloseTab(IntPtr hwnd, TermTab tab, Action<bool> done)
    {
        Run(() =>
        {
            bool ok = false;
            try
            {
                var el = FindTab(hwnd, tab);
                var button = el?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                if (button?.GetCurrentPattern(InvokePattern.Pattern) is InvokePattern inv) { inv.Invoke(); ok = true; }
            }
            catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException) { }
            _ui.BeginInvoke(() => done(ok));
            Refresh();
        });
    }

    void Run(Action job) { try { _q.Add(job); } catch (InvalidOperationException) { } }

    // ---------- Claude Code sessions ----------

    /// <summary>
    /// Start (a folder) or stop (null) reading Claude Code's status files, on the UI thread. Settings
    /// turns this on and off. A folder that doesn't exist yet is looked for again every few seconds,
    /// never created.
    /// </summary>
    public void Sessions(string? dir)
    {
        if (dir == _sessionsDir && (dir == null || _fsw != null)) return;
        _fsw?.Dispose(); _fsw = null;
        _debounce?.Dispose(); _debounce = null;
        _sessionsDir = dir;
        Run(ReadSessions);
        if (dir == null || !Directory.Exists(dir)) return;
        try
        {
            var debounce = _debounce = new Timer(_ => Run(ReadSessions));
            _fsw = new FileSystemWatcher(dir, "*.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler kick = (_, _) => debounce.Change(150, Timeout.Infinite);
            _fsw.Changed += kick; _fsw.Created += kick; _fsw.Deleted += kick;
            _fsw.Renamed += (_, e) => kick(null!, e);
            _fsw.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Write("sessions_unwatched", new { error = e.Message });
        }
    }

    void ReadSessions()
    {
        var found = new List<(string name, AgentInfo info)>();
        string[] files = Array.Empty<string>();
        try { if (_sessionsDir is string dir && Directory.Exists(dir)) files = Directory.GetFiles(dir, "*.json"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        foreach (var f in files)
            if (ParseSession(f) is { } s) found.Add(s);
        // A name two live sessions share can't tell their tabs apart: leave both unmarked.
        var fresh = found.GroupBy(s => s.name).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First().info, StringComparer.Ordinal);
        bool changed;
        lock (_lock)
        {
            changed = fresh.Count != _agents.Count || fresh.Any(kv => !_agents.TryGetValue(kv.Key, out var old) || old.State != kv.Value.State);
            _agents = fresh;
        }
        if (changed) _ui.BeginInvoke(() => Changed?.Invoke());
    }

    /// <summary>One status file: a live terminal session's name and state, or null. The format is
    /// Claude Code's own and undocumented, so every field is optional.</summary>
    public static (string name, AgentInfo info)? ParseSession(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return null;
            string? Str(string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (Str("entrypoint") != "cli") return null; // a terminal session; the desktop app and SDK runs have no tab
            string? name = Str("name");
            int pid = o.TryGetProperty("pid", out var p) && p.TryGetInt32(out var x) ? x : 0;
            if (string.IsNullOrEmpty(name) || pid <= 0 || !Alive(pid)) return null;
            string state = Str("status") switch { "waiting" => "needs", "busy" => "working", _ => "idle" };
            long ms = o.TryGetProperty("statusUpdatedAt", out var u) && u.TryGetInt64(out var t) ? t : 0;
            var since = ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime : DateTime.Now;
            string? cwd = Str("cwd");
            string? project = cwd == null ? null : Path.GetFileName(cwd.TrimEnd('\\', '/'));
            return (name, new AgentInfo(state, since, string.IsNullOrEmpty(project) ? null : project));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return null; } // mid-write, or gone
    }

    /// <summary>A status file can outlive its session. A process Altlane may not inspect (an elevated one) counts as alive.</summary>
    static bool Alive(int pid)
    {
        Process p;
        try { p = Process.GetProcessById(pid); }
        catch (ArgumentException) { return false; }
        using (p)
        {
            try { return !p.HasExited; }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _debounce?.Dispose();
        _fsw?.Dispose();
        _q.CompleteAdding();
    }
}
