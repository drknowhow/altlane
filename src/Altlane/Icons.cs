using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Altlane;

/// <summary>
/// Window icons as frozen WPF images, cached. Order: the icon the window reports (WM_GETICON, with a
/// short timeout so a hung app costs at most that once), its class icon, then its program's icon.
/// UI thread only.
/// </summary>
static class Icons
{
    static readonly Dictionary<IntPtr, ImageSource?> ByWindow = new();
    static readonly Dictionary<string, ImageSource?> ByPath = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? For(WinInfo w)
    {
        if (ByWindow.TryGetValue(w.Hwnd, out var img)) return img;
        img = FromHandle(WindowIcon(w.Hwnd)) ?? FromPath(w.ExePath);
        if (ByWindow.Count > 512) Prune();
        return ByWindow[w.Hwnd] = img;
    }

    static void Prune()
    {
        foreach (var h in ByWindow.Keys.Where(h => !N.IsWindow(h)).ToList()) ByWindow.Remove(h);
    }

    static IntPtr WindowIcon(IntPtr h)
    {
        foreach (int kind in new[] { N.ICON_BIG, N.ICON_SMALL2, N.ICON_SMALL })
            if (N.SendMessageTimeout(h, N.WM_GETICON, (IntPtr)kind, IntPtr.Zero, N.SMTO_ABORTIFHUNG | N.SMTO_BLOCK, 30, out var r) != IntPtr.Zero && r != IntPtr.Zero)
                return r;
        IntPtr c = N.GetClassLongPtr(h, N.GCLP_HICON);
        return c != IntPtr.Zero ? c : N.GetClassLongPtr(h, N.GCLP_HICONSM);
    }

    /// <summary>The window owns the handle; this only copies the pixels.</summary>
    static ImageSource? FromHandle(IntPtr hicon)
    {
        if (hicon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch (Exception e) when (e is ArgumentException or System.Runtime.InteropServices.COMException or InvalidOperationException) { return null; }
    }

    static ImageSource? FromPath(string? path)
    {
        if (path == null) return null;
        if (ByPath.TryGetValue(path, out var img)) return img;
        try
        {
            using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
            img = ico == null ? null : FromHandle(ico.Handle);
        }
        catch (Exception e) when (e is ArgumentException or System.IO.IOException or UnauthorizedAccessException) { img = null; }
        return ByPath[path] = img;
    }
}
