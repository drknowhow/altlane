using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Altlane;

/// <param name="Exe">File name with extension, e.g. "chrome.exe": what rules match on.</param>
/// <param name="AppName">The file description, e.g. "Google Chrome": what the filter also matches.</param>
/// <param name="AppId">Explicit AppUserModelID; only looked up for browsers, where it marks installed web apps.</param>
sealed record WinInfo(IntPtr Hwnd, string Title, string Exe, string AppName, string? ExePath, string? AppId, bool Minimized);

/// <summary>
/// Alt+Tab-eligible windows in most-recently-used order. The order is seeded from Z-order at start
/// and kept current by EVENT_SYSTEM_FOREGROUND. Must be created on a thread with a message loop.
/// </summary>
sealed class WindowTracker : IDisposable
{
    readonly LinkedList<IntPtr> _mru = new();
    readonly N.WinEventProc _proc; // held in a field for the same reason as the keyboard hook's delegate
    readonly IntPtr _hook;
    readonly uint _ownPid = (uint)Environment.ProcessId;
    readonly string? _titlePrefix;
    readonly Func<string, bool> _isBrowser;
    readonly Dictionary<uint, (string? path, string exe, string app)> _procs = new();
    readonly Dictionary<IntPtr, string?> _appIds = new();

    /// <summary>Fires on the UI thread whenever another app's window comes to the front.</summary>
    public event Action<IntPtr>? Foreground;

    /// <param name="titlePrefix">Test mode: only windows whose title starts with this.</param>
    public WindowTracker(string? titlePrefix, Func<string, bool> isBrowser)
    {
        _titlePrefix = titlePrefix;
        _isBrowser = isBrowser;
        foreach (var h in Win.TopLevel()) if (Wanted(h)) { var k = Win.MruKey(h); if (!_mru.Contains(k)) _mru.AddLast(k); }
        _proc = OnForeground;
        _hook = N.SetWinEventHook(N.EVENT_SYSTEM_FOREGROUND, N.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0,
            N.WINEVENT_OUTOFCONTEXT | N.WINEVENT_SKIPOWNPROCESS);
    }

    bool Wanted(IntPtr h) =>
        Win.Pid(h) != _ownPid && Win.IsAltTabEligible(h) &&
        (_titlePrefix == null || Win.Title(h).StartsWith(_titlePrefix, StringComparison.Ordinal));

    void OnForeground(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero) return;
        var k = Win.MruKey(hwnd);
        _mru.Remove(k);
        _mru.AddFirst(k);
        Foreground?.Invoke(hwnd);
    }

    /// <summary>The window the user was last in, as the MRU knows it (root owner).</summary>
    public IntPtr Current => _mru.First?.Value ?? IntPtr.Zero;

    /// <summary>Current eligible windows, MRU first; windows never seen in front follow in Z-order.</summary>
    public List<WinInfo> Snapshot()
    {
        var live = Win.TopLevel().Where(Wanted).ToList();
        var rank = new Dictionary<IntPtr, int>();
        int i = 0;
        foreach (var k in _mru) rank[k] = i++;
        // Stable sort: known windows by recency, unknown ones keep Z-order after them.
        var order = live.Select((h, z) => (h, z, r: rank.TryGetValue(Win.MruKey(h), out var x) ? x : int.MaxValue))
            .OrderBy(t => t.r).ThenBy(t => t.z).Select(t => t.h).ToList();

        foreach (var h in _mru.Where(h => !N.IsWindow(h)).ToList()) _mru.Remove(h);
        foreach (var h in _appIds.Keys.Where(h => !N.IsWindow(h)).ToList()) _appIds.Remove(h);
        if (_procs.Count > 256) _procs.Clear();

        return order.Select(Describe).ToList();
    }

    WinInfo Describe(IntPtr h)
    {
        uint pid = Win.Pid(h);
        var (path, exe, app) = Proc(pid);
        // UWP apps live inside ApplicationFrameHost; describe the app, not its host.
        if (exe.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            uint real = Win.AppPid(h, pid, "ApplicationFrameHost");
            if (real != pid) (path, exe, app) = Proc(real);
        }
        string? appId = null;
        if (_isBrowser(exe) && !_appIds.TryGetValue(h, out appId)) _appIds[h] = appId = Win.AppUserModelId(h);
        return new WinInfo(h, Win.Title(h), exe, app, path, appId, N.IsIconic(h));
    }

    (string? path, string exe, string app) Proc(uint pid)
    {
        if (_procs.TryGetValue(pid, out var p)) return p;
        string? path = Win.ProcessPath(pid);
        string exe = path != null ? Path.GetFileName(path) : "?";
        string app = Path.GetFileNameWithoutExtension(exe);
        if (path != null)
        {
            try
            {
                var d = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(d)) app = d.Trim();
            }
            catch (FileNotFoundException) { }
            catch (UnauthorizedAccessException) { }
        }
        return _procs[pid] = (path, exe, app);
    }

    public void Dispose() { if (_hook != IntPtr.Zero) N.UnhookWinEvent(_hook); }
}
