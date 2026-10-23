using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Altlane;

/// <summary>
/// JSONL log. Write() only enqueues, so the keyboard hook can call it without touching the disk;
/// a background thread serializes and appends. The app logs process names and timings, not window
/// titles, except in test mode where every title is one of the test windows.
/// </summary>
static class Log
{
    static readonly BlockingCollection<(double t, string ev, object? data)> Q = new();
    static StreamWriter? _w;
    static Thread? _thread;
    public static string Source = "?";
    public static string? FilePath { get; private set; }

    public static void Init(string? path, string source)
    {
        Source = source;
        if (path != null)
        {
            FilePath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            _w = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        }
        _thread = new Thread(Pump) { IsBackground = true, Name = "log" };
        _thread.Start();
    }

    /// <summary>Delete daily logs older than <paramref name="days"/> in the folder of the current log.</summary>
    public static void Prune(string dir, int days)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "altlane-*.jsonl").Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-days)))
                File.Delete(f);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Write(string ev, object? data = null)
    {
        if (!Q.IsAddingCompleted) Q.Add((Clock.Unix, ev, data));
    }

    static void Pump()
    {
        foreach (var (t, ev, data) in Q.GetConsumingEnumerable())
        {
            var row = new Dictionary<string, object?> { ["t"] = Math.Round(t, 2), ["src"] = Source, ["ev"] = ev };
            if (data != null)
                foreach (var p in data.GetType().GetProperties()) row[p.Name] = p.GetValue(data);
            string line;
            try { line = JsonSerializer.Serialize(row); }
            catch (Exception e) { line = JsonSerializer.Serialize(new { t, src = Source, ev = "log_error", error = e.Message }); }
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
