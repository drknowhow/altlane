using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Altlane;

/// <summary>
/// Bring a window to the front and prove it got there. Tries the cheap way first and records which
/// fallback was needed, because Windows' foreground lock decides which one works.
/// </summary>
static class Activator
{
    [DllImport("winmm")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm")] public static extern uint timeEndPeriod(uint ms);

    [StructLayout(LayoutKind.Sequential)] struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32")] static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, uint size);
    [DllImport("kernel32")] static extern IntPtr GetCurrentProcess();

    /// <summary>
    /// Windows 11 ignores timeBeginPeriod in a process with no visible window, which is Altlane between
    /// switches, so each 2 ms sleep in Verify took a 15.6 ms tick. This is the documented opt-out.
    /// </summary>
    static Activator()
    {
        var s = new PowerThrottlingState { Version = 1, ControlMask = 0x4 /* IGNORE_TIMER_RESOLUTION */, StateMask = 0 };
        SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref s, (uint)Marshal.SizeOf<PowerThrottlingState>());
    }

    public readonly record struct Result(bool Ok, string Method, double Ms, bool WasMinimized);

    /// <summary>Shell surfaces that keep the foreground while they are open: Start, Search, the notification panes.</summary>
    public static readonly string[] ShellSurfaces = { "StartMenuExperienceHost", "SearchHost", "SearchApp", "ShellExperienceHost" };

    /// <summary>1 ms sleeps only while switching, so the verify loop polls finely without keeping the timer fast all day.</summary>
    /// <param name="altReleased">The hook just saw Alt go up. Windows' key state lags behind the hook, so
    /// on a quick tap it still says Alt is held.</param>
    public static Result Activate(IntPtr h, IntPtr marker, bool altReleased = false)
    {
        timeBeginPeriod(1);
        try { return ActivateCore(h, marker, altReleased); }
        finally { timeEndPeriod(1); }
    }

    static Result ActivateCore(IntPtr h, IntPtr marker, bool altReleased)
    {
        long t0 = Stopwatch.GetTimestamp();
        bool wasMin = N.IsIconic(h);
        // Async so a hung target can't hang the switcher.
        if (wasMin) N.ShowWindowAsync(h, N.SW_RESTORE);

        // With Start or Search open, every method below fails (startprobe: 0 of 6, and 3 failed switches
        // in real use). Closing it first, as Windows' own Alt+Tab does, worked 6 of 6.
        string esc = Dismiss(N.GetForegroundWindow(), h, marker, altReleased) ? "esc+" : "";

        N.SetForegroundWindow(h);
        if (Verify(h, 120)) return new(true, esc + "plain", Clock.Between(t0, Stopwatch.GetTimestamp()), wasMin);
        if (esc != "") Log.Write("plain_failed_after_esc", new { fg = Who(N.GetForegroundWindow()) });

        uint me = N.GetCurrentThreadId(), fg = Win.Thread(N.GetForegroundWindow());
        if (fg != 0 && fg != me && N.AttachThreadInput(me, fg, true))
        {
            N.BringWindowToTop(h);
            N.SetForegroundWindow(h);
            N.AttachThreadInput(me, fg, false);
        }
        if (Verify(h, 120)) return new(true, esc + "attach", Clock.Between(t0, Stopwatch.GetTimestamp()), wasMin);

        // The documented unlock: the system lets SetForegroundWindow through while Alt is pressed.
        // Masked so the Alt-up doesn't open the target's menu bar.
        Keys.Send(Keys.Key(N.VK_LMENU, false, marker));
        N.SetForegroundWindow(h);
        Keys.Send(Keys.Key(N.VK_MASK, false, marker), Keys.Key(N.VK_MASK, true, marker), Keys.Key(N.VK_LMENU, true, marker));
        if (Verify(h, 200)) return new(true, esc + "alttrick", Clock.Between(t0, Stopwatch.GetTimestamp()), wasMin);

        // Last resort, the call Windows' own task switching was built on. The spike's run 4 saw all
        // three methods above fail once; this one goes through a different path in user32.
        SwitchToThisWindow(h, true);
        bool ok = Verify(h, 200);
        return new(ok, ok ? esc + "switchto" : "failed", Clock.Between(t0, Stopwatch.GetTimestamp()), wasMin);
    }

    [DllImport("user32")] static extern void SwitchToThisWindow(IntPtr h, bool altTab);

    public static bool IsShellSurface(IntPtr h) => h != IntPtr.Zero && Array.IndexOf(ShellSurfaces, Win.ProcessName(Win.Pid(h))) >= 0;

    [DllImport("user32")] static extern short GetAsyncKeyState(int vk);

    /// <summary>
    /// Esc to a shell surface or an open menu in front, then wait up to 300 ms for it to let go of the
    /// foreground. Any other window in front gets no key: Esc there could cancel what the user was doing.
    /// Not while Alt is held (Alt+number, Alt+`): Alt+Esc is Windows' own send-to-back shortcut. When
    /// the hook has seen Alt go up, wait up to 50 ms for Windows' key state to agree.
    /// </summary>
    static bool Dismiss(IntPtr fg, IntPtr target, IntPtr marker, bool altReleased)
    {
        if (fg == IntPtr.Zero || fg == target) return false;
        if (!IsShellSurface(fg) && Win.MenuFlags(fg) == 0) return false;
        // On a quick tap the switch starts before Windows has processed the Alt-up, and Start ignored an
        // Esc sent that early (T12: Start stayed open, while 10 ms later in a held Alt+Tab it closed).
        var sw = Stopwatch.StartNew();
        while ((GetAsyncKeyState(N.VK_MENU) & 0x8000) != 0)
        {
            if (!altReleased || sw.ElapsedMilliseconds > 50) return false;
            Thread.Sleep(1);
        }
        long altWait = sw.ElapsedMilliseconds;
        Keys.Send(Keys.Key(N.VK_ESCAPE, false, marker), Keys.Key(N.VK_ESCAPE, true, marker));
        sw.Restart();
        // Every window that held the foreground while Start closed, with when: process and class only.
        var seen = new List<string> { Who(fg) };
        IntPtr last = fg;
        while (sw.ElapsedMilliseconds < 300)
        {
            IntPtr now = N.GetForegroundWindow();
            if (now != last) { seen.Add($"{sw.ElapsedMilliseconds}ms {Who(now)}"); last = now; }
            // Not on "none": Start closing leaves no foreground for a moment, and a switch made then was
            // overridden when Windows gave the foreground back to the window behind Start.
            if (now != IntPtr.Zero && now != fg && !IsShellSurface(now)) break;
            Thread.Sleep(2);
        }
        Log.Write("dismiss", new { seen = string.Join(" > ", seen), ms = sw.ElapsedMilliseconds, alt_wait_ms = altWait });
        return true;
    }

    static string Who(IntPtr h) => h == IntPtr.Zero ? "none" : $"{Win.ProcessName(Win.Pid(h))}|{Win.Class(h)}";

    /// <summary>Foreground and not minimized within the time limit.</summary>
    public static bool Verify(IntPtr h, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (N.GetForegroundWindow() == h && !N.IsIconic(h)) return true;
            Thread.Sleep(2);
        }
        return N.GetForegroundWindow() == h && !N.IsIconic(h);
    }
}
