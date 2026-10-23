using System;
using System.Collections.Generic;
using System.Linq;

namespace Altlane;

/// <summary>A window as the overlay shows it: what it is, where the rules put it, and why.</summary>
sealed record Item(WinInfo W, Placement P, bool IsCurrent);

/// <param name="Num">The group's number key; null for the Recent row.</param>
sealed record Section(string Key, string Label, int? Num, string Color, List<Item> Items);

/// <summary>One card in reading order. The same window can appear twice: in Recent and in its group.</summary>
readonly record struct Slot(string Section, IntPtr Hwnd);

/// <summary>
/// Alt+Tab's sections, from windows in MRU order, and how the keys move between them. The grid and the
/// columns layouts show the same sections. Pure, so the self-test's unit mode can check it.
/// </summary>
static class Sections
{
    public const string RecentKey = "recent";
    public const int RecentCount = 3;

    /// <param name="mru">Visible windows, most recent first. When the window in front is one of them it is
    /// first and marked IsCurrent; when the desktop or a tool window has focus, none is.</param>
    /// <param name="only">Alt+` scope: just this group, and no Recent row.</param>
    public static List<Section> Build(IReadOnlyList<Item> mru, IReadOnlyList<Group> groups, string filter, string? only = null)
    {
        var q = filter.Trim();
        bool Matches(Item it) => q.Length == 0
            || (it.W.Title + " " + it.W.AppName + " " + Exe(it.W.Exe) + " " + it.P.Group?.Name).Contains(q, StringComparison.OrdinalIgnoreCase);

        var outp = new List<Section>();
        if (q.Length == 0 && only == null)
        {
            int skip = mru.Count > 0 && mru[0].IsCurrent ? 1 : 0;
            var rec = mru.Skip(skip).Take(RecentCount).ToList();
            if (rec.Count > 0) outp.Add(new Section(RecentKey, "Recent", null, "", rec));
        }
        foreach (var g in groups)
        {
            if (only != null && g.Key != only) continue;
            var items = mru.Where(it => it.P.Group == g && Matches(it)).ToList();
            if (items.Count > 0) outp.Add(new Section(g.Key, g.Name, g.Num, g.Color, items));
        }
        return outp;
    }

    static string Exe(string exe) => exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;

    public static List<Slot> Flat(List<Section> secs) => secs.SelectMany(s => s.Items.Select(it => new Slot(s.Key, it.W.Hwnd))).ToList();

    public static int IndexOf(List<Slot> flat, Slot? sel) => sel is Slot s ? flat.IndexOf(s) : -1;

    /// <summary>Step through every card in reading order, wrapping.</summary>
    public static Slot? Move(List<Section> secs, Slot? sel, int d)
    {
        var fl = Flat(secs);
        if (fl.Count == 0) return null;
        int i = IndexOf(fl, sel);
        if (i < 0) return d > 0 ? fl[0] : fl[^1];
        return fl[((i + d) % fl.Count + fl.Count) % fl.Count];
    }

    /// <summary>First card of the next (or previous) section, wrapping.</summary>
    public static Slot? JumpSection(List<Section> secs, Slot? sel, int d)
    {
        if (secs.Count == 0) return null;
        int k = sel is Slot s ? secs.FindIndex(x => x.Key == s.Section) : -1;
        k = k < 0 ? (d > 0 ? 0 : secs.Count - 1) : ((k + d) % secs.Count + secs.Count) % secs.Count;
        return new Slot(secs[k].Key, secs[k].Items[0].W.Hwnd);
    }

    /// <summary>Columns layout, ↑ ↓: the card above or below in the same column, stopping at its ends.</summary>
    public static Slot? MoveInColumn(List<Section> secs, Slot? sel, int d)
    {
        if (secs.Count == 0) return null;
        var (k, j) = Locate(secs, sel);
        if (k < 0) return new Slot(secs[0].Key, secs[0].Items[0].W.Hwnd);
        var items = secs[k].Items;
        return new Slot(secs[k].Key, items[Math.Clamp(j + d, 0, items.Count - 1)].W.Hwnd);
    }

    /// <summary>
    /// Columns layout, ← →: the same row in the next (or previous) column, wrapping; the column's last
    /// card when it is shorter.
    /// </summary>
    public static Slot? MoveAcrossColumns(List<Section> secs, Slot? sel, int d)
    {
        if (secs.Count == 0) return null;
        var (k, j) = Locate(secs, sel);
        if (k < 0) (k, j) = (d > 0 ? 0 : secs.Count - 1, 0);
        else k = ((k + d) % secs.Count + secs.Count) % secs.Count;
        var items = secs[k].Items;
        return new Slot(secs[k].Key, items[Math.Min(j, items.Count - 1)].W.Hwnd);
    }

    /// <summary>Section and row of a card, or (-1, -1) when it isn't in view.</summary>
    static (int k, int j) Locate(List<Section> secs, Slot? sel)
    {
        if (sel is not Slot s) return (-1, -1);
        int k = secs.FindIndex(x => x.Key == s.Section);
        int j = k < 0 ? -1 : secs[k].Items.FindIndex(it => it.W.Hwnd == s.Hwnd);
        return j < 0 ? (-1, -1) : (k, j);
    }

    /// <summary>First card of group n, or null when the group has no windows in view.</summary>
    public static Slot? Group(List<Section> secs, int n)
    {
        var s = secs.FirstOrDefault(x => x.Num == n);
        return s == null ? null : new Slot(s.Key, s.Items[0].W.Hwnd);
    }

    public static Item? ItemAt(List<Section> secs, Slot? sel) =>
        sel is Slot s ? secs.FirstOrDefault(x => x.Key == s.Section)?.Items.FirstOrDefault(it => it.W.Hwnd == s.Hwnd) : null;

    /// <summary>
    /// Alt+number: the most recent window in that group, skipping the one in front so pressing it
    /// again from inside the group moves on.
    /// </summary>
    public static Item? Direct(IReadOnlyList<Item> mru, Group g) => mru.Where(it => it.P.Group == g && !it.IsCurrent).FirstOrDefault();
}
