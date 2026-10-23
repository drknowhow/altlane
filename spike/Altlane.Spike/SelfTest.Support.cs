using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Altlane.Spike;

static partial class SelfTest
{
    // ---------- input ----------

    static void Check()
    {
        if (_abort != null) throw new AbortException(_abort);
        IntPtr fg = N.GetForegroundWindow();
        // Injected keys into an elevated window are dropped silently, so a run there measures nothing.
        if (fg != IntPtr.Zero && Win.IsElevated(Win.Pid(fg)) == true)
            throw new AbortException("elevated window in front: " + Win.Title(fg));
    }

    static void Pause(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (_abort != null) throw new AbortException(_abort);
            Thread.Sleep(2);
        }
    }

    static void Down(ushort vk) { Check(); Keys.Send(Keys.Key(vk, false, Drv)); Held.Add(vk); }
    static void Up(ushort vk) { Keys.Send(Keys.Key(vk, true, Drv)); Held.Remove(vk); }
    static void Tap(ushort vk, int gap = 15) { Down(vk); Pause(gap); Up(vk); }

    static void Activate(IntPtr h)
    {
        Check();
        var r = Activator.Activate(h, Drv);
        Log.Write("driver_activate", new { title = Win.Title(h), r.Ok, r.Method, ms = Math.Round(r.Ms, 1) });
        if (!r.Ok)
        {
            // Window class and process only: the foreground may be one of the user's own windows.
            IntPtr fg = N.GetForegroundWindow();
            Log.Write("activate_failed_fg", new
            {
                fg = Sig(fg), menu = Win.MenuFlags(fg), elevated = Win.IsElevated(Win.Pid(fg))?.ToString() ?? "unknown",
                idle_ms = Win.IdleMs(), iconic_target = N.IsIconic(h),
            });
            throw new AbortException("driver could not bring '" + Win.Title(h) + "' to the front; in front: " + Sig(fg));
        }
    }

    static bool WaitIdle(double needMs, double maxS)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < maxS)
        {
            if (Win.IdleMs() >= needMs) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    /// <summary>Low-level keyboard and mouse hooks that only watch: any event without the injected flag is a person.</summary>
    static void StartGuard()
    {
        using var ready = new ManualResetEventSlim();
        _kbGuard = (n, w, l) =>
        {
            if (n >= 0)
            {
                var k = System.Runtime.InteropServices.Marshal.PtrToStructure<N.KBDLLHOOKSTRUCT>(l);
                if ((k.flags & N.LLKHF_INJECTED) == 0) _abort ??= $"real key pressed (vk 0x{k.vkCode:X2})";
            }
            return N.CallNextHookEx(IntPtr.Zero, n, w, l);
        };
        _msGuard = (n, w, l) =>
        {
            if (n >= 0)
            {
                var m = System.Runtime.InteropServices.Marshal.PtrToStructure<N.MSLLHOOKSTRUCT>(l);
                if ((m.flags & N.LLMHF_INJECTED) == 0) _abort ??= "real mouse input";
            }
            return N.CallNextHookEx(IntPtr.Zero, n, w, l);
        };
        new Thread(() =>
        {
            _guardTid = N.GetCurrentThreadId();
            IntPtr hk = N.SetWindowsHookEx(N.WH_KEYBOARD_LL, _kbGuard, N.GetModuleHandle(null), 0);
            IntPtr hm = N.SetWindowsHookEx(N.WH_MOUSE_LL, _msGuard, N.GetModuleHandle(null), 0);
            ready.Set();
            while (N.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { N.TranslateMessage(ref msg); N.DispatchMessage(ref msg); }
            N.UnhookWindowsHookEx(hk); N.UnhookWindowsHookEx(hm);
        }) { IsBackground = true, Name = "guard" }.Start();
        ready.Wait();
    }

    static void StopGuard() { if (_guardTid != 0) N.PostThreadMessage(_guardTid, (uint)N.WM_QUIT, IntPtr.Zero, IntPtr.Zero); }

    // ---------- processes ----------

    static Process Spawn(string args) => Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args)
    {
        UseShellExecute = false, CreateNoWindow = true,
    })!;

    static void StartTestWindows()
    {
        var ready = new EventWaitHandle(false, EventResetMode.ManualReset, TestWindows.ReadyEvent);
        ready.Reset();
        _tw = Spawn($"testwindows --minutes 6 --log \"{Path.Combine(_dir, "testwindows.jsonl")}\"");
        if (!ready.WaitOne(15000)) throw new AbortException("test windows never came up");
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            foreach (var name in TestWindows.Names)
                if (!W.ContainsKey(name))
                {
                    var h = Win.TopLevel().FirstOrDefault(x => Win.Pid(x) == _tw.Id && Win.Title(x) == TestWindows.Prefix + name);
                    if (h != IntPtr.Zero) W[name] = h;
                }
            if (W.Count == TestWindows.Names.Length) break;
            Thread.Sleep(50);
        }
        if (W.Count != TestWindows.Names.Length) throw new AbortException("could not find all six test windows");
        Log.Write("test_windows", new { pid = _tw.Id, found = W.Count });
    }

    static void StartSwitcher(string commit)
    {
        _stop = new EventWaitHandle(false, EventResetMode.ManualReset, Program.StopEvent);
        _stop.Reset();
        var ready = new EventWaitHandle(false, EventResetMode.ManualReset, Program.ReadyEvent);
        ready.Reset();
        _sw = Spawn($"run --test --minutes 6 --commit {commit} --log \"{Path.Combine(_dir, "switcher.jsonl")}\" --shot-dir \"{_dir}\"");
        if (!ready.WaitOne(15000)) throw new AbortException("switcher never signalled ready");
        _overlay = Win.TopLevel().FirstOrDefault(h => Win.Pid(h) == _sw.Id && Win.Title(h) == "Altlane overlay");
        Log.Write("switcher", new { pid = _sw.Id, commit, overlay = _overlay.ToInt64() });
        if (_overlay == IntPtr.Zero) throw new AbortException("switcher overlay window not found");
    }

    /// <summary>Stops only the two processes this run started.</summary>
    static void StopChildren()
    {
        _stop?.Set();
        if (_sw != null && !_sw.WaitForExit(4000)) { Log.Write("killed", new { which = "switcher" }); _sw.Kill(); }
        if (_tw != null && !_tw.HasExited)
        {
            foreach (var h in W.Values) N.PostMessage(h, N.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            if (!_tw.WaitForExit(4000)) { Log.Write("killed", new { which = "testwindows" }); _tw.Kill(); }
        }
    }

    // ---------- observation ----------

    static bool Shown(IntPtr h) => h != IntPtr.Zero && N.IsWindowVisible(h) && !Win.IsCloaked(h);

    static string Sig(IntPtr h) => h == IntPtr.Zero ? "(none)" : Win.Class(h) + "|" + Win.ProcessName(Win.Pid(h));

    static Dictionary<IntPtr, string> Visible() =>
        Win.TopLevel().Where(h => N.IsWindowVisible(h) && !Win.IsCloaked(h)).ToDictionary(h => h, Sig);

    /// <summary>Which calibrated native surfaces (alt_tab, task_view) look present now.</summary>
    static List<string> NativeCheck(Dictionary<IntPtr, string> before)
    {
        string fg = Sig(N.GetForegroundWindow());
        var appeared = Visible().Where(kv => !before.ContainsKey(kv.Key)).Select(kv => kv.Value).ToHashSet();
        return NativeSigs.Where(kv => kv.Value.Contains(fg) || appeared.Overlaps(kv.Value)).Select(kv => kv.Key).ToList();
    }

    static bool StartOpen() => StartProcs.Contains(Win.ProcessName(Win.Pid(N.GetForegroundWindow())));

    static bool WatchStart(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (StartOpen()) return true;
            Pause(10);
        }
        return false;
    }
}
