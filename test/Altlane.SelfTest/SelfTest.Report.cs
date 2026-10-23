using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Altlane;

static partial class SelfTest
{
    static readonly List<double> RecoveryTimes = new();

    static void Report(string result)
    {
        System.Threading.Thread.Sleep(300); // let the children's log writers finish
        var sw = ReadJsonl(Path.Combine(_dir, "switcher.jsonl"));
        var tw = ReadJsonl(Path.Combine(_dir, "testwindows.jsonl"));

        // Alt+Tab's pane and Win+Tab's full-screen overview have their own targets.
        var composedAlt = sw.Where(e => Ev(e) == "overlay_composed" && !Lanes(e)).ToList();
        var composedLanes = sw.Where(e => Ev(e) == "overlay_composed" && Lanes(e)).ToList();
        double[] show = Nums(composedAlt, "overlay_composed", "show_ms"), fromKey = Nums(composedAlt, "overlay_composed", "from_key_ms");
        double[] showLanes = Nums(composedLanes, "overlay_composed", "show_ms"), fromKeyLanes = Nums(composedLanes, "overlay_composed", "from_key_ms");
        var activations = sw.Where(e => Ev(e) is "switch" or "direct" && Str(e, "method") != null).ToList();
        var methods = activations.GroupBy(e => Str(e, "method")!).ToDictionary(g => g.Key, g => g.Count());
        var hook = sw.FirstOrDefault(e => Ev(e) == "hook_stats");
        var ready = sw.FirstOrDefault(e => Ev(e) == "ready");
        int lags = sw.Count(e => Ev(e) == "dispatch_lag"), crashes = sw.Count(e => Ev(e) is "crash" or "fatal" or "hook_error");

        // What reached the test windows that shouldn't have, by phase. Rows right after a recovery Esc are ours.
        var side = tw.Where(e => Ev(e) is "leak_key" or "leak_char" or "keymenu" or "menuloop")
            .Select(e =>
            {
                double t = e.GetProperty("t").GetDouble();
                return new
                {
                    ev = Ev(e), phase = PhaseAt(t), win = Str(e, "win"),
                    detail = e.TryGetProperty("vk", out var vk) ? $"vk 0x{vk.GetInt32():X2}" : e.TryGetProperty("ch", out var ch) ? $"char {ch.GetInt32()}" : "",
                    excused = RecoveryTimes.Any(r => t >= r && t - r < 400),
                };
            }).ToList();
        var counted = side.Where(s => !s.excused && s.phase.StartsWith("T") && !s.phase.StartsWith("T0")).ToList();

        var steps = Rows.Where(r => r["test"] is string t && t != "T0" && t != "C").ToList();
        var switchRows = Rows.Where(r => r.ContainsKey("landed")).ToList();
        int landed = switchRows.Count(r => r["landed"] is true);
        double[] switchMs = switchRows.Where(r => r["landed"] is true).Select(r => Convert.ToDouble(r["switch_ms"])).ToArray();
        var perTest = Rows.GroupBy(r => (string)r["test"]!).ToDictionary(g => g.Key, g => $"{g.Count(r => r.GetValueOrDefault("ok") is true)}/{g.Count()}");
        // The zero-leak and zero-menu results only count if the same detectors fire on purpose.
        var controls = side.Where(s => s.phase.StartsWith("C_")).ToList();
        bool controlsFired = controls.Any(s => s.ev == "leak_key") && controls.Any(s => s.ev is "keymenu" or "menuloop")
                             && Rows.Any(r => r["test"] is "C" && r.GetValueOrDefault("what") is "win_alone" && r["ok"] is true);

        static bool Empty(object? list) => list is not IEnumerable<string> l || !l.Any();
        var altRows = steps.Where(r => r["test"] is not "T11tv").ToList(); // Task View is the point of those rows
        var t10 = Rows.FirstOrDefault(r => r["test"] is "T10" && r.GetValueOrDefault("what") is "reinstall");
        var t11 = Rows.Where(r => r["test"] is "T11").ToList();
        var tv = Rows.Where(r => r["test"] is "T11tv").ToList();
        string[] shots = { "alt-tab", "columns", "same-group", "filter", "regroup", "lanes", "lanes-filter" };

        var criteria = new Dictionary<string, bool>
        {
            ["no_native_switcher (T1-T11)"] = altRows.Count > 0 && altRows.All(r => Empty(r.GetValueOrDefault("native")) && Empty(r.GetValueOrDefault("native_mid"))),
            ["no_menu_bar (T1-T11)"] = !counted.Any(s => s.ev is "keymenu" or "menuloop") && steps.All(r => r.GetValueOrDefault("menu") is null or 0u),
            ["no_key_leaks (T1-T11)"] = !counted.Any(s => s.ev is "leak_key" or "leak_char"),
            ["overlay_p95_le_50ms (Alt+Tab)"] = show.Length > 0 && Pct(show, 0.95) <= 50,
            ["lanes_p95_le_100ms (Win+Tab)"] = showLanes.Length > 0 && Pct(showLanes, 0.95) <= 100,
            ["every_switch_landed"] = switchRows.Count > 0 && landed == switchRows.Count,
            ["every_step_passed (views, sections, logs)"] = steps.Count > 0 && steps.All(r => r["ok"] is true),
            ["watchdog_reinstalls_dropped_hook"] = t10?["ok"] is true,
            ["win_tab_opens_lanes (T11, 16 steps)"] = t11.Count == 16 && t11.All(r => r["ok"] is true),
            ["task_view_still_reachable (Shift+Win+Tab, setting off)"] = tv.Count == 4 && tv.All(r => r["ok"] is true),
            ["no_crashes"] = crashes == 0,
            ["detectors_proven (stray key leaks, lone Alt opens menu, lone Win opens Start)"] = controlsFired,
        };

        var summary = new Dictionary<string, object?>
        {
            ["result"] = result,
            ["verdict"] = result == "completed" && criteria.Values.All(v => v) ? "GO" : "NO-GO (see criteria)",
            ["criteria"] = criteria,
            ["per_test_ok"] = perTest,
            ["switches"] = new { attempted = switchRows.Count, landed, p50_ms = Pct(switchMs, 0.5), p95_ms = Pct(switchMs, 0.95), max_ms = switchMs.DefaultIfEmpty().Max() },
            ["activation_methods"] = methods,
            ["overlay_show_ms"] = new { n = show.Length, p50 = Pct(show, 0.5), p95 = Pct(show, 0.95), max = show.DefaultIfEmpty().Max() },
            ["overlay_from_key_ms"] = new { p50 = Pct(fromKey, 0.5), p95 = Pct(fromKey, 0.95) },
            ["lanes_show_ms"] = new { n = showLanes.Length, p50 = Pct(showLanes, 0.5), p95 = Pct(showLanes, 0.95), max = showLanes.DefaultIfEmpty().Max() },
            ["lanes_from_key_ms"] = new { p50 = Pct(fromKeyLanes, 0.5), p95 = Pct(fromKeyLanes, 0.95) },
            ["hook"] = hook.ValueKind == JsonValueKind.Object ? JsonSerializer.Deserialize<object>(hook.GetRawText()) : null,
            ["switcher_elevated"] = ready.ValueKind == JsonValueKind.Object ? ready.GetProperty("elevated").ToString() : null,
            ["dispatch_lag_over_30ms"] = lags,
            ["switcher_errors"] = crashes,
            ["screenshots"] = shots.ToDictionary(s => s, s => File.Exists(Path.Combine(_dir, $"overlay-{s}.png"))),
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
        Console.WriteLine("\n==== Altlane self-test ====");
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

    static bool Lanes(JsonElement e) => e.TryGetProperty("lanes", out var v) && v.ValueKind == JsonValueKind.True;

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
