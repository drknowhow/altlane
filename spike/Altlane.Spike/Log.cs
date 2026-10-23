using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace Altlane.Spike;

/// <summary>
/// JSONL log. Write() only enqueues, so the keyboard hook can call it without touching the disk;
/// a background thread serializes and appends.
/// </summary>
static class Log
{
    static readonly BlockingCollection<(double t, string ev, object? data)> Q = new();
    static StreamWriter? _w;
    static Thread? _thread;
    public static string Source = "?";

    public static void Init(string? path, string source)
    {
        Source = source;
        if (path != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            _w = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        }
        _thread = new Thread(Pump) { IsBackground = true, Name = "log" };
        _thread.Start();
    }

    public static void Write(string ev, object? data = null) => Q.Add((Clock.Unix, ev, data));

    static void Pump()
    {
        foreach (var (t, ev, data) in Q.GetConsumingEnumerable())
        {
            var row = new Dictionary<string, object?> { ["t"] = Math.Round(t, 2), ["src"] = Source, ["ev"] = ev };
            if (data != null)
                foreach (var p in data.GetType().GetProperties()) row[p.Name] = p.GetValue(data);
            string line = JsonSerializer.Serialize(row);
            _w?.WriteLine(line);
            Console.WriteLine(line);
        }
    }

    /// <summary>Drain the queue before exit so the last rows (often the interesting ones) land.</summary>
    public static void Flush(int timeoutMs = 2000)
    {
        Q.CompleteAdding();
        _thread?.Join(timeoutMs);
        _w?.Flush();
    }
}
