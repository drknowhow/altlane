using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;

namespace Altlane;

/// <summary>Where Altlane keeps its files: %LOCALAPPDATA%\Altlane, or --config-dir for tests.</summary>
static class Paths
{
    public static string Root { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Altlane");
    public static string Rules => Path.Combine(Root, "rules.json");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Logs => Path.Combine(Root, "logs");
    public static string TodayLog => Path.Combine(Logs, $"altlane-{DateTime.Now:yyyyMMdd}.jsonl");

    public static void Use(string root) => Root = Path.GetFullPath(root);
}

/// <summary>settings.json. Start-at-login and admin mode are not here: the Run key and the scheduled task are their own truth.</summary>
sealed class Settings
{
    public int ShowDelayMs { get; set; } = 120;
    /// <summary>Alt+` cycles the windows in the current window's group.</summary>
    public bool SameGroupKey { get; set; } = true;
    /// <summary>Alt+1..9 jumps straight to the most recent window in that group. Off by default: apps use Alt+number too.</summary>
    public bool DirectJump { get; set; }

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Paths.Settings))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.Settings), Opts) ?? new Settings();
        }
        catch (Exception e) when (e is JsonException or IOException) { Log.Write("settings_unreadable", new { error = e.Message }); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.Root);
            File.WriteAllText(Paths.Settings, JsonSerializer.Serialize(this, Opts));
        }
        catch (IOException e) { Log.Write("settings_save_failed", new { error = e.Message }); }
    }
}

/// <summary>
/// The rules in force, reloaded when rules.json changes on disk. A file that doesn't parse leaves the
/// last good rules in force and sets Error, which the tray shows once.
/// </summary>
sealed class RuleStore
{
    public Rules Current { get; private set; } = Rules.Parse(Rules.Default);
    public string? Error { get; private set; }
    /// <summary>Windows moved with Ctrl+number for as long as they're open, by group key.</summary>
    public Dictionary<IntPtr, string> Moved { get; } = new();
    public event Action? Changed;

    readonly Dispatcher _ui;
    FileSystemWatcher? _fsw;
    DispatcherTimer? _debounce;

    public RuleStore(Dispatcher ui) { _ui = ui; }

    public void Load()
    {
        try
        {
            Directory.CreateDirectory(Paths.Root);
            if (!File.Exists(Paths.Rules)) File.WriteAllText(Paths.Rules, Rules.Default);
            Current = Rules.Parse(File.ReadAllText(Paths.Rules));
            Error = null;
            Log.Write("rules_loaded", new { groups = Current.Groups.Count, warnings = Current.Warnings });
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Error = e.Message;
            Log.Write("rules_error", new { error = e.Message });
        }
        foreach (var h in Moved.Keys.Where(h => !N.IsWindow(h)).ToList()) Moved.Remove(h);
        Changed?.Invoke();
    }

    public void Watch()
    {
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) => { _debounce!.Stop(); Load(); }, _ui);
        _debounce.Stop();
        _fsw = new FileSystemWatcher(Paths.Root, "rules.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        FileSystemEventHandler kick = (_, _) => _ui.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });
        _fsw.Changed += kick; _fsw.Created += kick;
        _fsw.Renamed += (_, e) => kick(null!, e);
        _fsw.EnableRaisingEvents = true;
    }

    public Placement Place(WinInfo w) =>
        Current.Place(w.Hwnd, new WinFacts(w.Title, w.Exe, w.AppId, Current.IsBrowser(w.Exe)), Moved);

    public WinFacts Facts(WinInfo w) => new(w.Title, w.Exe, w.AppId, Current.IsBrowser(w.Exe));
}
