using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Altlane.Spike;

static partial class SelfTest
{
    static readonly List<double> RecoveryTimes = new();
    static readonly string[] SwitchTests = { "T1", "T2", "T3" };

    static void Report(string result)
    {
        System.Threading.Thread.Sleep(300); // let the children's log writers finish
        var sw = ReadJsonl(Path.Combine(_dir, "switcher.jsonl"));
        var tw = ReadJsonl(Path.Combine(_dir, "testwindows.jsonl"));

        double[] show = Nums(sw, "overlay_composed", "show_ms"), fromKey = Nums(sw, "overlay_composed", "from_key_ms");
        var switches = sw.Where(e => Ev(e) == "switch").ToList();
        var methods = switches.GroupBy(e => e.GetProperty("method").GetString() ?? "?").ToDictionary(g => g.Key, g => g.Count());
        var byStrategy = switches.GroupBy(e => e.GetProperty("s2").GetBoolean() ? "s2" : "s1")
            .ToDictionary(g => g.Key, g => $"{g.Count(e => e.GetProperty("ok").GetBoolean())}/{g.Count()}");
        var hook = sw.FirstOrDefault(e => Ev(e) == "hook_stats");
        var ready = sw.FirstOrDefault(e => Ev(e) == "ready");
        int lags = sw.Count(e => Ev(e) == "dispatch_lag"), crashes = sw.Count(e => Ev(e) == "crash");

        // What reached the test windows that shouldn't have, by phase. Rows right after a recovery Esc are ours.
        var side = tw.Where(e => Ev(e) is "leak_key" or "leak_char" or "keymenu" or "menuloop")
            .Select(e =>
            {
                double t = e.GetProperty("t").GetDouble();
                return new
                {
                    ev = Ev(e), phase = PhaseAt(t), win = e.TryGetProperty("win", out var w) ? w.GetString() : null,
                    detail = e.TryGetProperty("vk", out var vk) ? $"vk 0x{vk.GetInt32():X2}" : e.TryGetProperty("ch", out var ch) ? $"char {ch.GetInt32()}" : "",
                    excused = RecoveryTimes.Any(r => t >= r && t - r < 400),
                };
            }).ToList();
        var counted = side.Where(s => !s.excused && s.phase.StartsWith("T") && !s.phase.StartsWith("T0")).ToList();

        var swRows = Rows.Where(r => SwitchTests.Contains(r["test"] as string)).ToList();
        int landed = swRows.Count(r => r["landed"] is true), okRows = swRows.Count(r => r["ok"] is true);
        double[] switchMs = swRows.Where(r => r["landed"] is true).Select(r => Convert.ToDouble(r["switch_ms"])).ToArray();
        var perTest = Rows.GroupBy(r => (string)r["test"]!).ToDictionary(g => g.Key, g => $"{g.Count(r => r.GetValueOrDefault("ok") is true)}/{g.Count()}");
        // The zero-leak and zero-menu results only count if the same detectors fire on purpose.
        var controls = side.Where(s => s.phase.StartsWith("C_")).ToList();
        bool controlsFired = controls.Any(s => s.ev == "leak_key") && controls.Any(s => s.ev is "keymenu" or "menuloop")
                             && Rows.Any(r => r["test"] is "C" && r.GetValueOrDefault("what") is "win_alone" && r["ok"] is true);

        static bool Empty(object? list) => list is not IEnumerable<string> l || !l.Any();
        var t1t2 = Rows.Where(r => r["test"] is "T1" or "T2").ToList();
        var t5 = Rows.Where(r => r["test"] is "T5").ToList();
        var t6 = Rows.Where(r => r["test"] is "T6").ToList();

        var criteria = new Dictionary<string, bool>
        {
            ["alt_tab_swallowed (no native switcher in T1/T2)"] = t1t2.Count > 0 && t1t2.All(r => Empty(r["native"]) && Empty(r.GetValueOrDefault("native_mid"))),
            ["win_tab_swallowed (no Start, no Task View in T5)"] = t5.Count == 5 && t5.All(r => r["start_open"] is false && Empty(r["native"])),
            ["no_menu_bar (no menu mode T1-T5)"] = !counted.Any(s => s.ev is "keymenu" or "menuloop") && Rows.All(r => r.GetValueOrDefault("menu") is null or 0u),
            ["no_key_leaks (T1-T6)"] = !counted.Any(s => s.ev is "leak_key" or "leak_char"),
            ["overlay_p95_le_50ms"] = show.Length > 0 && Pct(show, 0.95) <= 50,
            ["switches_50_of_50"] = swRows.Count == 50 && landed == 50,
            ["shift_win_tab_reaches_task_view"] = t6.Count == 2 && t6.All(r => r["ok"] is true),
            ["detectors_proven (controls: stray key leaks, lone Alt opens menu, lone Win opens Start)"] = controlsFired,
        };

        var summary = new Dictionary<string, object?>
        {
            ["result"] = result,
            ["verdict"] = result == "completed" && criteria.Values.All(v => v) ? "GO" : "NO-GO (see criteria)",
            ["criteria"] = criteria,
            ["switches"] = new { attempted = swRows.Count, landed, clean = okRows, p50_ms = Pct(switchMs, 0.5), p95_ms = Pct(switchMs, 0.95), max_ms = switchMs.DefaultIfEmpty().Max() },
            ["activation_methods"] = methods,
            ["by_commit_strategy"] = byStrategy,
            ["overlay_show_ms"] = new { n = show.Length, p50 = Pct(show, 0.5), p95 = Pct(show, 0.95), max = show.DefaultIfEmpty().Max() },
            ["overlay_from_key_ms"] = new { p50 = Pct(fromKey, 0.5), p95 = Pct(fromKey, 0.95) },
            ["hook_callback"] = hook.ValueKind == JsonValueKind.Object ? JsonSerializer.Deserialize<object>(hook.GetRawText()) : null,
            ["switcher_elevated"] = ready.ValueKind == JsonValueKind.Object ? ready.GetProperty("elevated").ToString() : null,
            ["dispatch_lag_over_30ms"] = lags,
            ["switcher_crashes"] = crashes,
            ["per_test_ok"] = perTest,
            ["native_signatures"] = NativeSigs.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
            ["side_effects"] = side,
            ["failed_rows"] = Rows.Where(r => r.GetValueOrDefault("ok") is false).ToList(),
        };
        string json = JsonSerializer.Serialize(summary, new JsonSerializerOptions
        {
            WriteIndented = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals, // NaN when a test never ran
        });
        File.WriteAllText(Path.Combine(_dir, "summary.json"), json);
        Console.WriteLine("\n==== Altlane phase 0 self-test ====");
        Console.WriteLine(json);
        Console.WriteLine("logs: " + _dir);
    }

    static List<JsonElement> ReadJsonl(string path)
    {
        var list = new List<JsonElement>();
        if (!File.Exists(path)) return list;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var rd = new StreamReader(fs);
        string? line;
        while ((line = rd.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            try { list.Add(JsonDocument.Parse(line).RootElement.Clone()); } catch (JsonException) { }
        }
        return list;
    }

    static string Ev(JsonElement e) => e.TryGetProperty("ev", out var v) ? v.GetString() ?? "" : "";

    static double[] Nums(List<JsonElement> rows, string ev, string field) =>
        rows.Where(e => Ev(e) == ev && e.TryGetProperty(field, out _)).Select(e => e.GetProperty(field).GetDouble()).ToArray();

    static double Pct(double[] xs, double p)
    {
        if (xs.Length == 0) return double.NaN;
        var a = xs.OrderBy(x => x).ToArray();
        return Math.Round(a[Math.Clamp((int)Math.Ceiling(p * a.Length) - 1, 0, a.Length - 1)], 2);
    }

    static string PhaseAt(double t) =>
        Phases.Where(p => t >= p.t0 && t <= p.t1 + 300).Select(p => p.name).LastOrDefault() ?? "between";
}
