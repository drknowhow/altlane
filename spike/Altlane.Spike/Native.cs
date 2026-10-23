using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Altlane.Spike;

/// <summary>P/Invoke surface for the spike. x64 only.</summary>
static class N
{
    public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    public const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    public const int WM_CHAR = 0x102, WM_SYSCHAR = 0x106, WM_SYSCOMMAND = 0x112, WM_ENTERMENULOOP = 0x211;
    public const int WM_CLOSE = 0x10, WM_QUIT = 0x12, WM_ACTIVATE = 0x6;
    public const int SC_KEYMENU = 0xF100;
    public const uint LLKHF_INJECTED = 0x10, LLMHF_INJECTED = 0x01;
    public const uint KEYEVENTF_KEYUP = 0x2, KEYEVENTF_EXTENDEDKEY = 0x1;
    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;
    public const int SW_RESTORE = 9, SW_MINIMIZE = 6, SW_SHOWNOACTIVATE = 4;
    public const uint GW_OWNER = 4;
    public const uint GA_ROOTOWNER = 3;
    public const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
    public const uint GUI_INMENUMODE = 0x4, GUI_SYSTEMMENUMODE = 0x8, GUI_POPUPMENUMODE = 0x10;
    public const int DWMWA_CLOAKED = 14, DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWM_TNP_RECTDESTINATION = 1, DWM_TNP_OPACITY = 4, DWM_TNP_VISIBLE = 8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_QUERY = 0x8;

    public const ushort VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
    public const ushort VK_ESCAPE = 0x1B, VK_BACK = 0x08, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;
    public const ushort VK_DELETE = 0x2E, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;
    public const ushort VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5, VK_OEM_3 = 0xC0;
    /// <summary>Unassigned VK. Pressing it between Alt/Win down and up stops the menu bar / Start from opening.</summary>
    public const ushort VK_MASK = 0xE8;

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; } }
    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    // MOUSEINPUT is the largest union member; leaving it out makes cbSize wrong and SendInput fails silently.
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize; public uint flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO { public uint cbSize, dwTime; }
    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags; public RECT rcDestination, rcSource; public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    [DllImport("user32", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32")] public static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32")] public static extern uint GetCurrentThreadId();
    [DllImport("user32")] public static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32")] public static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32")] public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int cb);
    [DllImport("user32")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO lii);

    [DllImport("user32")] public static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    [DllImport("user32")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32")] public static extern bool ShowWindowAsync(IntPtr h, int cmd);
    [DllImport("user32")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32")] public static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
    [DllImport("user32")] public static extern bool PostMessage(IntPtr h, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32")] public static extern bool GetWindowRect(IntPtr h, out RECT rect);
    [DllImport("user32")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32")] public static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("dwmapi")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("dwmapi")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("dwmapi")] public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi")] public static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
    [DllImport("dwmapi")] public static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);
    [DllImport("dwmapi")] public static extern int DwmFlush();

    [DllImport("kernel32", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("advapi32", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32", SetLastError = true)] public static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int len, out int retLen);
}

/// <summary>Window facts the switcher and the self-test both need.</summary>
static class Win
{
    public static string Title(IntPtr h)
    {
        int n = N.GetWindowTextLength(h);
        if (n <= 0) return "";
        var sb = new StringBuilder(n + 1);
        N.GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string Class(IntPtr h)
    {
        var sb = new StringBuilder(256);
        N.GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static uint Pid(IntPtr h) { N.GetWindowThreadProcessId(h, out uint pid); return pid; }
    public static uint Thread(IntPtr h) => N.GetWindowThreadProcessId(h, out _);

    public static string ProcessName(uint pid)
    {
        IntPtr p = N.OpenProcess(N.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (p == IntPtr.Zero) return "?";
        try
        {
            var sb = new StringBuilder(1024); int size = sb.Capacity;
            return N.QueryFullProcessImageName(p, 0, sb, ref size) ? System.IO.Path.GetFileNameWithoutExtension(sb.ToString()) : "?";
        }
        finally { N.CloseHandle(p); }
    }

    /// <summary>null when the token can't be read (treat as "assume elevated" for safety).</summary>
    public static bool? IsElevated(uint pid)
    {
        IntPtr p = N.OpenProcess(N.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (p == IntPtr.Zero) return null;
        try
        {
            if (!N.OpenProcessToken(p, N.TOKEN_QUERY, out IntPtr tok)) return null;
            try { return N.GetTokenInformation(tok, 20 /*TokenElevation*/, out int elev, 4, out _) ? elev != 0 : null; }
            finally { N.CloseHandle(tok); }
        }
        finally { N.CloseHandle(p); }
    }

    public static bool IsCloaked(IntPtr h) => N.DwmGetWindowAttribute(h, N.DWMWA_CLOAKED, out int c, 4) == 0 && c != 0;
    public static long ExStyle(IntPtr h) => (long)N.GetWindowLongPtr(h, N.GWL_EXSTYLE);

    /// <summary>The visibility half of the Alt+Tab rule. Good enough for the spike; the full rule walks owner chains.</summary>
    public static bool IsAltTabEligible(IntPtr h)
    {
        if (!N.IsWindowVisible(h) || IsCloaked(h)) return false;
        long ex = ExStyle(h);
        bool app = (ex & N.WS_EX_APPWINDOW) != 0;
        if ((ex & N.WS_EX_TOOLWINDOW) != 0 && !app) return false;
        if ((ex & N.WS_EX_NOACTIVATE) != 0 && !app) return false;
        if (N.GetWindow(h, N.GW_OWNER) != IntPtr.Zero && !app) return false;
        return N.GetWindowTextLength(h) > 0;
    }

    /// <summary>Top-level windows in Z-order, topmost first.</summary>
    public static List<IntPtr> TopLevel()
    {
        var list = new List<IntPtr>(256);
        N.EnumWindows((h, _) => { list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }

    public static uint MenuFlags(IntPtr h)
    {
        var gi = new N.GUITHREADINFO { cbSize = Marshal.SizeOf<N.GUITHREADINFO>() };
        return N.GetGUIThreadInfo(Thread(h), ref gi) ? gi.flags & (N.GUI_INMENUMODE | N.GUI_SYSTEMMENUMODE | N.GUI_POPUPMENUMODE) : 0;
    }

    public static uint IdleMs()
    {
        var lii = new N.LASTINPUTINFO { cbSize = 8 };
        N.GetLastInputInfo(ref lii);
        return unchecked((uint)Environment.TickCount - lii.dwTime);
    }
}

/// <summary>SendInput helpers. Every key carries an extra-info marker so hooks can tell whose it is.</summary>
static class Keys
{
    static bool Extended(ushort vk) => vk is N.VK_RWIN or N.VK_LWIN or N.VK_RMENU or N.VK_RCONTROL or N.VK_DELETE
        or N.VK_LEFT or N.VK_RIGHT or N.VK_UP or N.VK_DOWN;

    public static N.INPUT Key(ushort vk, bool up, IntPtr marker) => new()
    {
        type = 1,
        u = new N.InputUnion
        {
            ki = new N.KEYBDINPUT
            {
                wVk = vk,
                dwFlags = (up ? N.KEYEVENTF_KEYUP : 0) | (Extended(vk) ? N.KEYEVENTF_EXTENDEDKEY : 0),
                dwExtraInfo = marker,
            }
        }
    };

    public static uint Send(params N.INPUT[] inputs) => N.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<N.INPUT>());

    public static uint Tap(ushort vk, IntPtr marker) => Send(Key(vk, false, marker), Key(vk, true, marker));

    /// <summary>Mask key down+up. Swallowing Tab leaves Alt/Win "pressed alone"; this breaks that.</summary>
    public static uint Mask(IntPtr marker) => Tap(N.VK_MASK, marker);
}

static class Clock
{
    static readonly long T0 = Stopwatch.GetTimestamp();
    public static double Ms(long ts) => (ts - T0) * 1000.0 / Stopwatch.Frequency;
    public static double NowMs => Ms(Stopwatch.GetTimestamp());
    public static double Between(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;
    /// <summary>Wall clock in unix ms with sub-ms precision, for lining up logs across processes.</summary>
    public static double Unix => (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10000.0;
}
