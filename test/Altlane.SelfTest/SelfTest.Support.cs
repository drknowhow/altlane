using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace Altlane;

static partial class SelfTest
{
    static uint _guardTid;
    static N.HookProc? _kbGuard, _msGuard;
    static long _tailPos;

    // ---------- input ----------

    static void Check()
    {
        if (_abort != null) throw new AbortException(_abort);
        IntPtr fg = N.GetForegroundWindow();
        // Injected keys into an elevated window are dropped silently, so a run there measures nothing.
        if (fg != IntPtr.Zero && Win.IsElevated(Win.Pid(fg)) == true)
            throw new AbortException("elevated window in front: " + Sig(fg));
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

    /// <summary>
    /// Keys go only where the run expects them: a test window, Altlane's overlay, or a native surface the
    /// run opened itself. Anything else in front (the user's own window, say) stops the run before a key lands.
    /// </summary>
    static void CheckTarget()
    {
        IntPtr fg = N.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _overlay || W.ContainsValue(fg) || StartOpen()) return;
        string sig = Sig(fg);
        if (NativeSigs.Values.Any(s => s.Contains(sig))) return;
        throw new AbortException("keys would go to a window this run didn't open: " + sig);
    }

    static void Down(ushort vk) { Check(); CheckTarget(); Keys.Send(Keys.Key(vk, false, Drv)); Held.Add(vk); }
    static void Up(ushort vk) { Keys.Send(Keys.Key(vk, true, Drv)); Held.Remove(vk); }
    static void Tap(ushort vk, int gap = 15) { Down(vk); Pause(gap); Up(vk); }

    static void Activate(IntPtr h)
    {
        Check();
        var r = Activator.Activate(h, Drv);
        Log.Write("driver_activate", new { win = Name(h), r.Ok, r.Method, ms = Math.Round(r.Ms, 1) });
        if (!r.Ok)
        {
            // Window class and process only: the foreground may be one of the user's own windows.
            IntPtr fg = N.GetForegroundWindow();
            Log.Write("activate_failed_fg", new { fg = Sig(fg), menu = Win.MenuFlags(fg), idle_ms = Win.IdleMs(), iconic_target = N.IsIconic(h) });
            throw new AbortException("driver could not bring " + Name(h) + " to the front; in front: " + Sig(fg));
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
                var k = Marshal.PtrToStructure<N.KBDLLHOOKSTRUCT>(l);
                if ((k.flags & N.LLKHF_INJECTED) == 0) _abort ??= $"real key pressed (vk 0x{k.vkCode:X2})";
            }
            return N.CallNextHookEx(IntPtr.Zero, n, w, l);
        };
        _msGuard = (n, w, l) =>
        {
            if (n >= 0)
            {
                var m = Marshal.PtrToStructure<N.MSLLHOOKSTRUCT>(l);
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

    // ---------- the driver's model ----------

    static List<IntPtr> LiveMru() => Mru.Where(N.IsWindow).ToList();

    static void MoveFront(IntPtr h) { Mru.Remove(h); Mru.Insert(0, h); }

    static void Front(IntPtr h) { Activate(h); MoveFront(h); }

    /// <summary>Make sure the window the model calls current really is in front.</summary>
    static void EnsureFront()
    {
        var m = LiveMru();
        if (m.Count > 0 && N.GetForegroundWindow() != m[0]) Activate(m[0]);
    }

    /// <summary>Restore every live test window and bring them forward in reverse, so recency is exactly <paramref name="order"/>.</summary>
    static void Reset(params string[] order)
    {
        var names = (order.Length > 0 ? order : TestWindows.Names).Where(n => W.TryGetValue(n, out var h) && N.IsWindow(h)).ToList();
        foreach (var n in names) if (N.IsIconic(W[n])) N.ShowWindowAsync(W[n], N.SW_RESTORE);
        Pause(250);
        for (int i = names.Count - 1; i >= 0; i--) { Activate(W[names[i]]); Pause(120); }
        Mru.Clear();
        Mru.AddRange(names.Select(n => W[n]));
        Log.Write("reset", new { order = names });
    }

    static IntPtr OtherIn(string group)
    {
        var m = LiveMru();
        return m.First(h => Grp[h] == group && h != m[0]);
    }

    /// <summary>Layout A in reading order, as the model predicts it: Recent (the three after the current window), then each group's windows, most recent first.</summary>
    static List<(string sec, IntPtr h)> ExpectedFlat()
    {
        var live = LiveMru();
        var flat = new List<(string sec, IntPtr h)>();
        flat.AddRange(live.Skip(1).Take(Sections.RecentCount).Select(h => (Sections.RecentKey, h)));
        foreach (var g in GroupOrder) flat.AddRange(live.Where(h => Grp[h] == g).Select(h => (g, h)));
        return flat;
    }

    /// <summary>The Win+Tab overview in reading order, as the model predicts it: each group's windows, most recent first, no Recent.</summary>
    static List<(string sec, IntPtr h)> ExpectedLanes()
    {
        var live = LiveMru();
        return GroupOrder.SelectMany(g => live.Where(h => Grp[h] == g).Select(h => (g, h))).ToList();
    }

    /// <summary>"recent:bravo,charlie,delta agents:alpha,bravo …", readable in a failed row.</summary>
    static string ViewString(List<(string sec, IntPtr h)> flat) =>
        string.Join(" ", flat.GroupBy(x => x.sec).Select(g => g.Key + ":" + string.Join(",", g.Select(x => Name(x.h)))));

    /// <summary>The same string from one of Altlane's "view" log lines.</summary>
    static string? ViewOf(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Object || !v.TryGetProperty("sections", out var secs)) return null;
        return string.Join(" ", secs.EnumerateArray().Select(s =>
            s.GetProperty("key").GetString() + ":" + string.Join(",", s.GetProperty("hwnds").EnumerateArray().Select(x => Name((IntPtr)x.GetInt64())))));
    }

    static string Name(IntPtr h)
    {
        foreach (var (n, x) in W) if (x == h) return n;
        return h == IntPtr.Zero ? "(none)" : "other:" + Sig(h);
    }

    // ---------- processes ----------

    static string? RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "src", "Altlane", "Altlane.csproj"))) return d.FullName;
        return null;
    }

    static string? FindApp()
    {
        if (RepoRoot() is not string root) return null;
        string bin = Path.Combine(root, "src", "Altlane", "bin");
        return new[] { "Release", "Debug" }
            .Select(c => Path.Combine(bin, c, "net10.0-windows", "win-x64", "Altlane.exe"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Another hook answering Alt+Tab would make every result meaningless.</summary>
    static string? Refusal()
    {
        if (N.FindWindowEx(N.HWND_MESSAGE, IntPtr.Zero, null, "Altlane.Control") != IntPtr.Zero || Process.GetProcessesByName("Altlane").Length > 0)
            return "Altlane is running. Quit it from the tray first: two keyboard hooks would both answer Alt+Tab.";
        if (Process.GetProcessesByName("Altlane.Spike").Length > 0) return "the phase 0 spike (Altlane.Spike) is running";
        if (Process.GetProcessesByName("Altlane.SelfTest").Any(p => p.Id != Environment.ProcessId)) return "another Altlane.SelfTest is running";
        return null;
    }

    /// <summary>
    /// The test's own rules.json and settings.json. The groups are the shipped defaults; title rules put
    /// each test window in a group, except foxtrot, which no rule names.
    /// </summary>
    static void WriteConfig()
    {
        var defaults = JsonNode.Parse(Rules.Default, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!;
        var keys = defaults["groups"]!.AsArray().Select(g => (string)g!["key"]!).ToArray();
        // The tests press number keys by position, so they are written against this exact list.
        if (!keys.SequenceEqual(GroupOrder))
            throw new AbortException($"Rules.Default's groups are now [{string.Join(",", keys)}]; update SelfTest.GroupOrder and the number keys the tests press");

        var titles = new JsonArray();
        foreach (var (name, g) in HomeGroup)
            if (g != "other") titles.Add(new JsonObject { ["contains"] = TestWindows.Prefix + name, ["group"] = g });
        titles.Add(new JsonObject { ["contains"] = TestWindows.Prefix + "tab", ["group"] = "term" }); // T16's Windows Terminal tabs
        foreach (var g in TestWindows.BulkGroups) // T17's extra windows
            if (g != "other") titles.Add(new JsonObject { ["contains"] = TestWindows.Prefix + "bulk " + g, ["group"] = g });
        var rules = new JsonObject
        {
            ["groups"] = defaults["groups"]!.DeepClone(),
            ["titles"] = titles,
            ["browsers"] = new JsonArray(),
            ["browserGroup"] = "web",
            ["otherGroup"] = "other",
        };
        _cfg = Path.Combine(_dir, "cfg");
        Directory.CreateDirectory(_cfg);
        Directory.CreateDirectory(Path.Combine(_dir, "claude-sessions")); // T16's stand-in for Claude Code's status files
        File.WriteAllText(Path.Combine(_cfg, "rules.json"), rules.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(_cfg, "settings.json"), """{ "ShowDelayMs": 120, "SameGroupKey": true, "DirectJump": true }""");
    }

    static Process Spawn(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    static void StartTestWindows()
    {
        var ready = new EventWaitHandle(false, EventResetMode.ManualReset, TestWindows.ReadyEvent);
        ready.Reset();
        _tw = Spawn(Environment.ProcessPath!, "testwindows", "--minutes", "14", "--log", Path.Combine(_dir, "testwindows.jsonl"));
        if (!ready.WaitOne(15000)) throw new AbortException("test windows never came up");
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            foreach (var name in TestWindows.Names)
                if (!W.ContainsKey(name))
                {
                    var h = Win.TopLevel().FirstOrDefault(x => Win.Pid(x) == _tw.Id && Win.Title(x) == TestWindows.Prefix + name);
                    if (h != IntPtr.Zero) { W[name] = h; Grp[h] = HomeGroup[name]; }
                }
            if (W.Count == TestWindows.Names.Length) break;
            Thread.Sleep(50);
        }
        if (W.Count != TestWindows.Names.Length) throw new AbortException("could not find all six test windows");
        Log.Write("test_windows", new { pid = _tw.Id, found = W.Count });
    }

    /// <summary>--theme dark|light for Altlane, instead of the Windows app theme.</summary>
    static string? _theme;

    static void StartSwitcher(string app)
    {
        var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEvent);
        ready.Reset();
        var args = new List<string> { "--test", "--config-dir", _cfg, "--log", Path.Combine(_dir, "switcher.jsonl"), "--shot-dir", _dir, "--minutes", "12",
            "--claude-dir", Path.Combine(_dir, "claude-sessions") };
        if (_theme != null) args.AddRange(new[] { "--theme", _theme });
        _sw = Spawn(app, args.ToArray());
        if (!ready.WaitOne(20000)) throw new AbortException("Altlane never signalled ready");
        _overlay = Win.TopLevel().FirstOrDefault(h => Win.Pid(h) == _sw.Id && Win.Title(h) == "Altlane overlay");
        if (_overlay == IntPtr.Zero) throw new AbortException("Altlane's overlay window not found");
        var t = Stopwatch.StartNew();
        while (Shown(_overlay) && t.ElapsedMilliseconds < 3000) Pause(20);
        Log.Write("switcher", new { pid = _sw.Id, app, overlay = _overlay.ToInt64(), hidden_after_ms = t.ElapsedMilliseconds });
        if (Shown(_overlay)) throw new AbortException("Altlane's overlay is still visible 3 s after ready, with no session open");
    }

    /// <summary>Stops only the two processes this run started.</summary>
    static void StopChildren()
    {
        if (_sw != null && !_sw.HasExited)
        {
            IntPtr ctl = N.FindWindowEx(N.HWND_MESSAGE, IntPtr.Zero, null, ControlTitle);
            if (ctl != IntPtr.Zero) N.PostMessage(ctl, (int)N.RegisterWindowMessage("Altlane.Quit"), IntPtr.Zero, IntPtr.Zero);
            if (!_sw.WaitForExit(5000)) { Log.Write("killed", new { which = "switcher" }); _sw.Kill(); }
        }
        if (_tw != null && !_tw.HasExited)
        {
            foreach (var (name, h) in W) if (name != "wt") N.PostMessage(h, N.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
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

    static bool StartOpen() => Activator.IsShellSurface(N.GetForegroundWindow());

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

    /// <summary>Altlane's log lines written since the last call. Only whole lines; a half-written one waits.</summary>
    static List<JsonElement> Tail()
    {
        var list = new List<JsonElement>();
        string path = Path.Combine(_dir, "switcher.jsonl");
        if (!File.Exists(path)) return list;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length <= _tailPos) return list;
        fs.Seek(_tailPos, SeekOrigin.Begin);
        var buf = new byte[fs.Length - _tailPos];
        int n = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
        int end = n == 0 ? -1 : Array.LastIndexOf(buf, (byte)'\n', n - 1);
        if (end < 0) return list;
        _tailPos += end + 1;
        foreach (var line in Encoding.UTF8.GetString(buf, 0, end + 1).Split('\n'))
        {
            string s = line.TrimEnd('\r');
            if (s.Length == 0) continue;
            try { list.Add(JsonDocument.Parse(s).RootElement.Clone()); } catch (JsonException) { }
        }
        return list;
    }

    static string Ev(JsonElement e) => Str(e, "ev") ?? "";

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
