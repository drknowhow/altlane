using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Altlane;

/// <summary>
/// Altlane: Alt+Tab, grouped by kind of window.
///   (no args) | --autostart        run in the tray (a second launch opens Settings in the first)
///   --from-task | --takeover       run, waiting for an exiting copy to let go first
///   --quit                         ask the running copy to quit
///   --admin-task install|remove|status|selfcheck [--user DOMAIN\name]
///   --list FILE                    write the windows and where the rules put them, then exit
/// Test and diagnostic options: --test --config-dir DIR --log FILE --shot-dir DIR --minutes N --no-tray
///   --claude-dir DIR (Claude Code's session folder; in --test there is none unless this is given)
///   --theme dark|light (draw the overlay in this theme instead of following Windows)
///   --debug-drop-hook-after-s N
/// Panic quit, from anywhere: Ctrl+Alt+Shift+Q.
/// </summary>
static class App
{
    public const string TestReadyEvent = "Altlane.Test.Ready";
    public static string Version => Assembly.GetExecutingAssembly().GetName().Version is Version v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

    static Application? _app;
    static KeyboardHook? _hook;
    static HookWatchdog? _watchdog;
    static ControlWindow? _ctl;
    static Tray? _tray;
    static SettingsWindow? _settingsWindow;
    static UsageWindow? _usageWindow;
    static Terminals? _terms;
    static Mutex? _instance;
    static int _quitting, _crashes;

    [DllImport("user32")] static extern bool AllowSetForegroundWindow(int pid);

    [STAThread]
    static int Main(string[] args)
    {
        var o = Parse(args);
        if (o.TryGetValue("config-dir", out var dir)) Paths.Use(dir);
        bool test = o.ContainsKey("test");
        if (test && o.TryGetValue("theme", out var theme)) Overlay.ThemeOverride = theme;

        if (o.ContainsKey("quit")) return QuitRunning(test);
        if (o.TryGetValue("admin-task", out var action))
        {
            Log.Init(Paths.TodayLog, "admin-task");
            int code = AdminTask.Command(action, o.GetValueOrDefault("user"));
            Log.Flush();
            return code;
        }
        if (o.TryGetValue("list", out var listPath)) return List(listPath, test);
        return Run(o, test);
    }

    static Dictionary<string, string> Parse(string[] args)
    {
        var o = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            string k = args[i][2..];
            bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
            o[k] = hasValue ? args[++i] : "true";
        }
        return o;
    }

    static double Num(this Dictionary<string, string> o, string k, double d) =>
        o.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : d;

    static int QuitRunning(bool test)
    {
        if (!ControlWindow.Send(ControlWindow.MsgQuit, test)) return 0;
        var sw = Stopwatch.StartNew();
        while (ControlWindow.Running(test) && sw.ElapsedMilliseconds < 8000) Thread.Sleep(100);
        return ControlWindow.Running(test) ? 1 : 0;
    }

    /// <summary>One copy per sign-in session. A copy started by the task or a hand-over waits for the old one to go.</summary>
    static bool TakeInstance(bool test, double waitMs)
    {
        string name = test ? @"Local\Altlane.Test.Instance" : @"Local\Altlane.Instance";
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                _instance = new Mutex(true, name, out bool created);
                if (created) return true;
                _instance.Dispose();
                _instance = null;
            }
            catch (UnauthorizedAccessException) { } // an elevated copy owns it
            if (sw.ElapsedMilliseconds >= waitMs) return false;
            Thread.Sleep(200);
        }
    }

    static int Run(Dictionary<string, string> o, bool test)
    {
        bool fromTask = o.ContainsKey("from-task") || o.ContainsKey("takeover");
        if (o.TryGetValue("log", out var logPath)) Log.Init(logPath, "app");
        else Log.InitDaily(Paths.Logs, "app", keepDays: 14);

        // Admin mode is on but this copy isn't elevated (a Start-menu launch, say): let the task start the elevated one.
        if (!test && !fromTask && !AdminTask.SelfElevated && AdminTask.Exists() && !ControlWindow.Running() && AdminTask.Run())
        {
            Log.Write("handed_to_admin_task");
            Log.Flush();
            return 0;
        }

        if (!TakeInstance(test, fromTask ? 10_000 : 0))
        {
            bool quietStart = o.ContainsKey("autostart") || fromTask;
            if (!quietStart && !test)
            {
                AllowSetForegroundWindow(-1); // let the running copy bring Settings to the front
                ControlWindow.Send(ControlWindow.MsgSettings);
            }
            Log.Write("already_running", new { quiet = quietStart });
            Log.Flush();
            return 0;
        }

        _app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        _app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Write("crash", new { error = e.Exception.ToString() });
            e.Handled = true;
            // Keep running (the hook is what makes Alt+Tab work), but give up if it keeps happening.
            if (++_crashes > 5) Quit("repeated_crash");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { Log.Write("fatal", new { error = e.ExceptionObject.ToString() }); Log.Flush(); };

        if (!Activator.NoThrottling()) Log.Write("throttling_opt_out_failed", new { error = Marshal.GetLastWin32Error() });
        var settings = Settings.Load();
        var rules = new RuleStore(_app.Dispatcher);
        rules.Load();
        rules.Watch();

        var ov = new Overlay();
        var wt = new WindowTracker(test ? "Altlane test" : null, exe => rules.Current.IsBrowser(exe));
        // Claude Code's status files. The self-test brings its own folder, so it never reads the real one.
        string? sessionsDir = test ? o.GetValueOrDefault("claude-dir") : Terminals.DefaultSessionsDir;
        var terms = _terms = new Terminals(_app.Dispatcher, settings.ClaudeStatus ? sessionsDir : null);
        terms.Source(() => Win.TopLevel()
            .Where(h => Win.Class(h) == Terminals.WindowClass && (!test || Win.Title(h).StartsWith("Altlane test", StringComparison.Ordinal)))
            .ToList());
        terms.Refresh();
        wt.Foreground += terms.Noticed;
        var ctl = new SwitchController(ov, wt, rules, settings, test, terms) { ShotDir = o.GetValueOrDefault("shot-dir") };
        // The self-test trims after a few seconds, so its timings include sessions that start on a trimmed heap.
        if (o.TryGetValue("gc-trim-ms", out var trimMs) && int.TryParse(trimMs, out int ms) && ms >= 0) ctl.TrimAfter = TimeSpan.FromMilliseconds(ms);
        var hook = _hook = new KeyboardHook(_app.Dispatcher, ctl.OnHook);
        void ApplySettings()
        {
            hook.SameGroupKey = settings.SameGroupKey; hook.DirectJump = settings.DirectJump; hook.WinTab = settings.WinTabLanes;
            terms.Sessions(settings.ClaudeStatus ? sessionsDir : null);
        }
        ApplySettings();
        ctl.Attach(hook);
        hook.Start();

        _ctl = new ControlWindow(test);
        _ctl.QuitRequested += () => Quit("quit_message");
        _ctl.DropHookRequested += () => hook.DropForTest();
        _ctl.LayoutRequested += columns => { settings.Layout = columns ? Settings.ColumnsLayout : Settings.GridLayout; Log.Write("layout", new { layout = settings.Layout }); };
        _ctl.WinTabRequested += on => { settings.WinTabLanes = hook.WinTab = on; Log.Write("win_tab", new { lanes = on }); };
        _watchdog = new HookWatchdog(_hook, _ctl, _app.Dispatcher);
        _watchdog.Start();

        // Draw real cards with real thumbnails once off-screen, so the first Alt+Tab isn't the slow one.
        // Ready only once that frame is drawn and hidden: a session before then would race the warm-up.
        ov.Prewarm(ctl.Preview(), settings.Columns ? ViewKind.Columns : ViewKind.Grid, () =>
        {
            Log.Write("ready", new
            {
                version = Version, pid = Environment.ProcessId, test, elevated = AdminTask.SelfElevated, from_task = fromTask,
                overlay = ov.Hwnd.ToInt64(), rules_error = rules.Error,
            });
            if (test) { using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, TestReadyEvent); ready.Set(); }
        });

        void ShowSettings()
        {
            if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
            _settingsWindow = new SettingsWindow(settings, rules, Version, () => { settings.Save(); ApplySettings(); })
            {
                TurnOnAdmin = TurnOnAdmin, TurnOffAdmin = TurnOffAdmin,
                OpenRules = () => Open(Paths.Rules), OpenLogs = () => Open(Paths.Logs), OpenUsage = () => ShowUsage(),
                QuitApp = () => Quit("settings_quit"),
            };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }
        _ctl.SettingsRequested += ShowSettings;

        void ShowUsage()
        {
            if (_usageWindow != null) { _usageWindow.Activate(); return; }
            _usageWindow = new UsageWindow(Usage.Read(Paths.Logs, days: 7), settings, rules.Current.Groups);
            _usageWindow.Closed += (_, _) => _usageWindow = null;
            _usageWindow.Show();
            _usageWindow.Activate();
        }
        _ctl.UsageRequested += ShowUsage;

        if (!test && !o.ContainsKey("no-tray"))
        {
            var tray = _tray = new Tray(Version, AdminTask.SelfElevated);
            tray.PauseClicked += () =>
            {
                hook.Paused = !hook.Paused;
                if (hook.Paused) ctl.Cancel("paused");
                tray.SetPaused(hook.Paused);
                Log.Write("paused", new { on = hook.Paused });
            };
            tray.SettingsClicked += ShowSettings;
            tray.UsageClicked += ShowUsage;
            tray.RulesClicked += () => Open(Paths.Rules);
            tray.LogsClicked += () => Open(Paths.Logs);
            tray.QuitClicked += () => Quit("tray_quit");
            string? shownError = null;
            void RulesNotice()
            {
                if (rules.Error != null && rules.Error != shownError)
                    tray.Balloon("rules.json has an error", "Altlane keeps using the last good rules. " + rules.Error, warning: true);
                shownError = rules.Error;
            }
            rules.Changed += RulesNotice;
            RulesNotice();
            // Start at sign-in is the installer's checkbox (and Settings'), never set here: a copy run from
            // a build folder would otherwise point the Run key at that folder.
            if (!File.Exists(Paths.Settings)) settings.Save();
        }

        if (o.ContainsKey("minutes"))
        {
            var limit = new DispatcherTimer { Interval = TimeSpan.FromMinutes(o.Num("minutes", 30)) };
            limit.Tick += (_, _) => Quit("time_limit");
            limit.Start();
        }
        if (o.ContainsKey("debug-drop-hook-after-s"))
        {
            var drop = new DispatcherTimer { Interval = TimeSpan.FromSeconds(o.Num("debug-drop-hook-after-s", 5)) };
            drop.Tick += (_, _) => { drop.Stop(); hook.DropForTest(); };
            drop.Start();
        }

        _app.Run();

        var (n, p50, p99, max) = hook.Stats();
        Log.Write("hook_stats", new
        {
            n, p50_us = Math.Round(p50, 1), p99_us = Math.Round(p99, 1), max_us = Math.Round(max, 1),
            hook_seen = hook.Seen, raw_seen = _ctl.RawCount, reinstalls = _watchdog.Reinstalls,
        });
        Log.Flush();
        _instance?.ReleaseMutex();
        return 0;
    }

    public static void Quit(string why)
    {
        if (Interlocked.Exchange(ref _quitting, 1) == 1) return;
        _hook?.Stop();
        Log.Write("exit", new { why });
        _tray?.Dispose();
        _settingsWindow?.Close();
        _usageWindow?.Close();
        _terms?.Dispose();
        _ctl?.Dispose();
        _app?.Shutdown();
    }

    static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            // No app is set to open .json files: Notepad always can.
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
    }

    // ---------- admin mode ----------

    static bool TurnOnAdmin()
    {
        string user = WindowsIdentity.GetCurrent().Name;
        int? code = AdminTask.Elevated($"--admin-task install --user \"{user}\"");
        Log.Write("admin_on", new { code });
        if (code == null) { MessageBox.Show("Admin mode stays off: the permission prompt was declined.", "Altlane"); return false; }
        if (code != 0) { MessageBox.Show($"Admin mode couldn't be set up (schtasks exit code {code}). The details are in today's log.", "Altlane"); return false; }

        Autostart.Set(false); // the task starts Altlane at sign-in now
        if (AdminTask.Run())
        {
            Quit("handed_to_admin_task"); // the elevated copy waits for this one to let go
            return true;
        }
        MessageBox.Show("Admin mode is on. It takes effect the next time you sign in.", "Altlane");
        return true;
    }

    static bool TurnOffAdmin()
    {
        int? code = AdminTask.SelfElevated ? AdminTask.Command("remove", null) : AdminTask.Elevated("--admin-task remove");
        Log.Write("admin_off", new { code });
        if (code != 0)
        {
            MessageBox.Show(code == null ? "Admin mode stays on: the permission prompt was declined." : $"Admin mode couldn't be turned off (schtasks exit code {code}).", "Altlane");
            return false;
        }
        Autostart.Set(true);
        if (AdminTask.SelfElevated)
            MessageBox.Show("Admin mode is off. This copy keeps running as administrator until you quit it or sign out; next time Altlane starts normally.", "Altlane");
        return true;
    }

    // ---------- diagnostics ----------

    /// <summary>Where every window would go, and why. Titles included: the file stays on this PC.</summary>
    static int List(string path, bool test)
    {
        Log.Init(null, "list");
        var rules = new RuleStore(Dispatcher.CurrentDispatcher);
        rules.Load();
        using var wt = new WindowTracker(test ? "Altlane test" : null, exe => rules.Current.IsBrowser(exe));
        var lines = new List<string>();
        if (rules.Error != null) lines.Add("RULES ERROR: " + rules.Error);
        foreach (var w in rules.Current.Warnings) lines.Add("warning: " + w);
        foreach (var w in wt.Snapshot())
        {
            var p = rules.Place(w);
            lines.Add($"{p.Group?.Name ?? "(hidden)",-14} {p.Level,-8} {w.Exe,-28} {(w.Minimized ? "min " : "")}{w.Title}  [{w.AppId}]  -- {p.Why}");
        }
        File.WriteAllLines(path, lines);
        Log.Flush();
        return 0;
    }
}
