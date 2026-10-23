using System;
using System.Collections.Generic;
using System.Linq;

namespace Altlane.Spike;

sealed record WinInfo(IntPtr Hwnd, string Title, string Process, bool Minimized);

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

    /// <param name="titlePrefix">Test mode: only windows whose title starts with this.</param>
    public WindowTracker(string? titlePrefix)
    {
        _titlePrefix = titlePrefix;
        foreach (var h in Win.TopLevel()) if (Wanted(h)) _mru.AddLast(h);
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
        _mru.Remove(hwnd);
        _mru.AddFirst(hwnd);
    }

    /// <summary>Current eligible windows, MRU first; windows never seen in front follow in Z-order.</summary>
    public List<WinInfo> Snapshot()
    {
        var live = Win.TopLevel().Where(Wanted).ToList();
        var set = new HashSet<IntPtr>(live);
        var order = _mru.Where(set.Contains).ToList();
        var seen = new HashSet<IntPtr>(order);
        order.AddRange(live.Where(h => !seen.Contains(h)));
        foreach (var h in _mru.Where(h => !N.IsWindow(h)).ToList()) _mru.Remove(h);
        return order.Select(h => new WinInfo(h, Win.Title(h), Win.ProcessName(Win.Pid(h)), N.IsIconic(h))).ToList();
    }

    public void Dispose() { if (_hook != IntPtr.Zero) N.UnhookWinEvent(_hook); }
}
