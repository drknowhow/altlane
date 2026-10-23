using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Altlane.Spike;

/// <summary>
/// Bring a window to the front and prove it got there. Tries the cheap way first and records which
/// fallback was needed, because the foreground lock rules are exactly what the spike has to measure.
/// </summary>
static class Activator
{
    [DllImport("winmm")] public static extern uint timeBeginPeriod(uint ms);

    public readonly record struct Result(bool Ok, string Method, double Ms, bool WasMinimized);

    public static Result Activate(IntPtr h, IntPtr marker)
    {
        long t0 = Stopwatch.GetTimestamp();
        bool wasMin = N.IsIconic(h);
        // Async so a hung target can't hang the switcher.
        if (wasMin) N.ShowWindowAsync(h, N.SW_RESTORE);

        N.SetForegroundWindow(h);
        if (Verify(h, 120)) return new(true, "plain", Clock.Between(t0, Stopwatch.GetTimestamp()), wasMin);

        uint me = N.GetCurrentThreadId(), fg = Win.Thread(N.GetForegroundWindow());
        if (fg != 0 && fg != me && N.AttachThreadInput(me, fg, true))
        {
            N.BringWindowToTop(h);
            N.SetForegroundWindow(h);
            N.AttachThreadInput(me, fg, false);
        }
        if (Verify(h, 120)) return new(true, "attach", Clock.Between(t0, Stopwatch.GetTimestamp()), wasMin);

        // The documented unlock: the system lets SetForegroundWindow through while Alt is pressed.
        // Masked so the Alt-up doesn't open the target's menu bar.
        Keys.Send(Keys.Key(N.VK_LMENU, false, marker));
        N.SetForegroundWindow(h);
        Keys.Send(Keys.Key(N.VK_MASK, false, marker), Keys.Key(N.VK_MASK, true, marker), Keys.Key(N.VK_LMENU, true, marker));
        bool ok = Verify(h, 300);
        return new(ok, ok ? "alttrick" : "failed", Clock.Between(t0, Stopwatch.GetTimestamp()), wasMin);
    }

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
