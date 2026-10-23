using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Altlane.Spike;

/// <summary>
/// Altlane phase 0 spike.
///   run [--minutes N] [--test] [--commit s1|s2|alt] [--log path]   the switcher
///   selftest [--log-dir dir] [--idle-wait-s N]                       drives the switcher with injected keys
///   testwindows [--minutes N] [--log path]                           six "Altlane test …" windows
/// Panic quit for the switcher: Ctrl+Alt+Shift+Q.
/// </summary>
static class Program
{
    public const string ReadyEvent = "Altlane.Spike.Ready", StopEvent = "Altlane.Spike.Stop";
    static Application? _app;
    static KeyboardHook? _hook;
    static EventWaitHandle? _ready;
    static int _quitting;

    [STAThread]
    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "run";
        var o = Parse(args);
        Activator.timeBeginPeriod(1); // 1 ms timers and sleeps, so latency numbers mean something
        return mode switch
        {
            "run" => Run(o),
            "selftest" => SelfTest.Run(o),
            "testwindows" => TestWindows.Run(o),
            _ => Usage(),
        };
    }

    static int Usage()
    {
        Console.Error.WriteLine("usage: Altlane.Spike run|selftest|testwindows [--options]");
        return 2;
    }

    static Dictionary<string, string> Parse(string[] args)
    {
        var o = new Dictionary<string, string>();
        for (int i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            string k = args[i][2..];
            bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
            o[k] = hasValue ? args[++i] : "true";
        }
        return o;
    }

    public static double Num(this Dictionary<string, string> o, string k, double d) =>
        o.TryGetValue(k, out var v) && double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : d;

    static int Run(Dictionary<string, string> o)
    {
        Log.Init(o.GetValueOrDefault("log"), "switcher");
        bool test = o.ContainsKey("test");
        string strategy = o.GetValueOrDefault("commit", "s1");
        double minutes = o.Num("minutes", 30);

        _app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        _app.DispatcherUnhandledException += (_, e) => { Log.Write("crash", new { error = e.Exception.ToString() }); e.Handled = true; Quit("crash"); };

        var ov = new Overlay();
        ov.Prewarm();
        var wt = new WindowTracker(test ? "Altlane test" : null);
        var ctl = new SwitchController(ov, wt) { ShotDir = o.GetValueOrDefault("shot-dir") };
        _hook = new KeyboardHook(_app.Dispatcher, ctl.OnHook, strategy);
        ctl.Attach(_hook);
        _hook.Start();

        var limit = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
        limit.Tick += (_, _) => Quit("time_limit");
        limit.Start();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _app.Dispatcher.BeginInvoke(() => Quit("ctrl_c")); };
        var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopEvent);
        stop.Reset();
        new Thread(() => { stop.WaitOne(); _app.Dispatcher.BeginInvoke(() => Quit("stop_event")); }) { IsBackground = true }.Start();

        Log.Write("ready", new
        {
            pid = Environment.ProcessId, test, strategy, minutes,
            elevated = Win.IsElevated((uint)Environment.ProcessId),
            overlay = ov.Hwnd.ToInt64(),
        });
        _ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEvent);
        _ready.Set();

        _app.Run();
        Log.Flush();
        return 0;
    }

    public static void Quit(string why)
    {
        if (Interlocked.Exchange(ref _quitting, 1) == 1) return;
        _hook?.Stop();
        if (_hook != null)
        {
            var (n, p50, p99, max) = _hook.Stats();
            Log.Write("hook_stats", new { n, p50_us = Math.Round(p50, 1), p99_us = Math.Round(p99, 1), max_us = Math.Round(max, 1) });
        }
        Log.Write("exit", new { why });
        _app?.Shutdown();
    }
}
