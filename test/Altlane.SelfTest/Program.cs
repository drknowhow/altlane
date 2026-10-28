using System;
using System.Collections.Generic;
using System.Globalization;

namespace Altlane;

/// <summary>
/// Altlane self-test.
///   unit                                              rules and sections logic, no windows
///   selftest [--app PATH] [--log-dir DIR] [--idle-wait-s N]   drives Altlane.exe with injected keys
///   testwindows [--minutes N] [--log PATH]            six "Altlane test …" windows
///   testwindows --bulk N [--minutes N] [--log PATH]   N more, spread over the groups (the speed phase)
///   selftest ... --theme dark|light                   Altlane's theme for the run, whatever Windows uses
///   selftest ... --only T17,T16                       just those phases, for timing work (verdict NO-GO)
///   selftest ... --gc-trim-ms N                       Altlane trims its heap N ms after a session (default 2000; 60000 as shipped)
/// </summary>
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "unit";
        var o = Parse(args);
        return mode switch
        {
            "unit" => UnitChecks.Run(),
            "selftest" => SelfTest.Run(o),
            "testwindows" => TestWindows.Run(o),
            "startprobe" => SelfTest.StartProbe(o),
            _ => Usage(),
        };
    }

    static int Usage()
    {
        Console.Error.WriteLine("usage: Altlane.SelfTest unit|selftest|testwindows [--options]");
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
        o.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : d;
}
