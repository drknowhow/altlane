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
    static DailyFile? _daily;
    static Thread? _thread;
    public static string Source = "?";

    /// <summary>Log to one file for the life of the process (tests, --log), or to the console only when <paramref name="path"/> is null.</summary>
    public static void Init(string? path, string source)
    {
        Source = source;
        if (path != null) _w = DailyFile.Open(Path.GetFullPath(path));
        Start();
    }

    /// <summary>Log to altlane-yyyyMMdd.jsonl in <paramref name="dir"/>, moving to the next day's file at local midnight.</summary>
    public static void InitDaily(string dir, string source, int keepDays)
    {
        Source = source;
        _daily = new DailyFile(dir, keepDays);
        Start();
    }

    static void Start()
    {
        _thread = new Thread(Pump) { IsBackground = true, Name = "log" };
        _thread.Start();
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
            // A row goes in the file for the day it was logged, not the day it was written.
            (_daily?.For(DateTimeOffset.FromUnixTimeMilliseconds((long)t).LocalDateTime) ?? _w)?.WriteLine(line);
            Console.WriteLine(line);
        }
    }

    /// <summary>Drain the queue before exit so the last rows (often the interesting ones) land.</summary>
    public static void Flush(int timeoutMs = 2000)
    {
        Q.CompleteAdding();
        _thread?.Join(timeoutMs);
        _w?.Flush();
        _daily?.Flush();
    }
}

/// <summary>
/// One log file per local day. Before 0.5.1 the app kept the file of the day it started, so a copy left
/// running for a week wrote everything under that first date, and pruning never reached the open file.
/// </summary>
sealed class DailyFile : IDisposable
{
    readonly string _dir;
    readonly int _keepDays;
    DateTime _day;
    StreamWriter? _w;

    public DailyFile(string dir, int keepDays)
    {
        _dir = Path.GetFullPath(dir);
        _keepDays = keepDays;
    }

    public static string Name(DateTime day) => $"altlane-{day:yyyyMMdd}.jsonl";

    /// <summary>The writer for a row logged at local time <paramref name="now"/>. A new day closes the old file and prunes.</summary>
    public StreamWriter For(DateTime now)
    {
        if (_w != null && now.Date == _day) return _w;
        _w?.Dispose();
        _day = now.Date;
        _w = Open(Path.Combine(_dir, Name(_day)));
        Prune(now);
        return _w;
    }

    public void Flush() => _w?.Flush();
    public void Dispose() => _w?.Dispose();

    public static StreamWriter Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    /// <summary>Delete daily logs last written more than <c>keepDays</c> before <paramref name="now"/>.</summary>
    void Prune(DateTime now)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "altlane-*.jsonl").Where(f => File.GetLastWriteTime(f) < now.AddDays(-_keepDays)))
                File.Delete(f);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
