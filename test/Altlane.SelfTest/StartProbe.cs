using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Altlane;

/// <summary>
/// startprobe: which way of switching windows still works while Start is open. Real use showed every
/// method in Activator failing with Start in front. Opens Start over one test window, tries one method on
/// another, records the result, closes Start, and repeats. Same safety rules as the self-test.
/// </summary>
static partial class SelfTest
{
    public static int StartProbe(Dictionary<string, string> o)
    {
        Activator.timeBeginPeriod(1);
        _dir = o.GetValueOrDefault("log-dir") ?? Path.Combine(RepoRoot() ?? AppContext.BaseDirectory, "logs", "startprobe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(_dir);
        Log.Init(Path.Combine(_dir, "driver.jsonl"), "driver");
        if (!WaitIdle(o.Num("idle-ms", 8000), o.Num("idle-wait-s", 120))) { Console.WriteLine("gave up: never idle"); return 3; }

        int reps = (int)o.Num("reps", 3);
        bool taskbar = o.GetValueOrDefault("open") == "taskbar"; // Win+T: keyboard focus on the taskbar instead of Start
        var methods = new (string name, Func<IntPtr, bool> run)[]
        {
            ("plain", h => { N.SetForegroundWindow(h); return Activator.Verify(h, 300); }),
            ("activator", h => Activator.Activate(h, Drv).Ok),
            ("esc_then_plain", h => { CloseStart(); N.SetForegroundWindow(h); return Activator.Verify(h, 300); }),
            ("esc_then_activator", h => { CloseStart(); return Activator.Activate(h, Drv).Ok; }),
        };
        var tally = methods.ToDictionary(m => m.name, _ => 0);
        IntPtr original = N.GetForegroundWindow();
        string result = "completed";
        StartGuard();
        try
        {
            StartTestWindows();
            foreach (var (name, run) in methods)
                for (int i = 0; i < reps; i++)
                {
                    Activate(W["alpha"]);
                    Pause(300);
                    bool open;
                    if (taskbar)
                    {
                        Down(N.VK_LWIN); Pause(25); Tap(0x54); Pause(25); Up(N.VK_LWIN);
                        var t = Stopwatch.StartNew();
                        while (Win.ProcessName(Win.Pid(N.GetForegroundWindow())) != "explorer" && t.ElapsedMilliseconds < 1500) Pause(10);
                        open = Win.ProcessName(Win.Pid(N.GetForegroundWindow())) == "explorer";
                    }
                    else open = WatchStart(1500);
                    Pause(300);
                    string before = Sig(N.GetForegroundWindow());
                    var sw = Stopwatch.StartNew();
                    bool ok = open && run(W["bravo"]);
                    Log.Write("probe", new { method = name, rep = i, start_open = open, fg_before = before, menu = Win.MenuFlags(N.GetForegroundWindow()), ok, ms = sw.ElapsedMilliseconds, fg_after = Sig(N.GetForegroundWindow()) });
                    if (ok) tally[name]++;
                    CloseStart();
                    Pause(800);
                }
        }
        catch (AbortException e) { result = "aborted: " + e.Message; }
        catch (Exception e) { result = "error: " + e; }
        finally
        {
            foreach (var vk in Held.ToList()) Keys.Send(Keys.Key(vk, true, Drv));
            Held.Clear();
            StopGuard();
            StopChildren();
            if (result == "completed" && N.IsWindow(original)) Activator.Activate(original, Drv);
        }
        Log.Write("probe_done", new { result, tally });
        Log.Flush();
        Console.WriteLine(result);
        foreach (var (name, n) in tally) Console.WriteLine($"{name}: {n}/{reps}");
        return result == "completed" ? 0 : 1;
    }

    static void CloseStart()
    {
        if (!StartOpen() && Win.ProcessName(Win.Pid(N.GetForegroundWindow())) != "explorer") return;
        Keys.Send(Keys.Key(N.VK_ESCAPE, false, Drv), Keys.Key(N.VK_ESCAPE, true, Drv));
        var sw = Stopwatch.StartNew();
        while (StartOpen() && sw.ElapsedMilliseconds < 3000) Pause(10);
    }
}
