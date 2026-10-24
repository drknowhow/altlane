using System;
using System.Collections.Generic;
using System.Linq;

namespace Altlane;

/// <summary>A window as the overlay shows it: what it is, where the rules put it, and why.</summary>
sealed record Item(WinInfo W, Placement P, bool IsCurrent);

/// <param name="Num">The group's number key; null for the Recent row.</param>
sealed record Section(string Key, string Label, int? Num, string Color, List<Item> Items);

/// <summary>One card in reading order. The same window can appear twice: in Recent and in its group.
/// Tab is the Windows Terminal tab's index when the window's tabs have a card each, else -1.</summary>
readonly record struct Slot(string Section, IntPtr Hwnd, int Tab = -1)
{
    public static Slot Of(string section, Item it) => new(section, it.W.Hwnd, it.W.Tab?.Index ?? -1);
    /// <summary>The same card, whichever section it is in.</summary>
    public bool SameCard(Slot o) => Hwnd == o.Hwnd && Tab == o.Tab;
}

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
    /// <param name="recent">False for Win+Tab's overview, where each window appears once, in its group.</param>
    /// <param name="needsFirst">Claude Code sessions waiting for you go first in their group.</param>
    public static List<Section> Build(IReadOnlyList<Item> mru, IReadOnlyList<Group> groups, string filter, string? only = null, bool recent = true, bool needsFirst = false)
    {
        var q = filter.Trim();
        bool Matches(Item it) => q.Length == 0
            || (it.W.Title + " " + it.W.AppName + " " + Exe(it.W.Exe) + " " + it.P.Group?.Name).Contains(q, StringComparison.OrdinalIgnoreCase);

        var outp = new List<Section>();
        if (q.Length == 0 && only == null && recent)
        {
            int skip = mru.Count > 0 && mru[0].IsCurrent ? 1 : 0;
            // Recent is windows: a terminal tab that isn't in front has no recency of its own.
            var rec = mru.Skip(skip).Where(it => !it.W.HiddenTab).Take(RecentCount).ToList();
            if (rec.Count > 0) outp.Add(new Section(RecentKey, "Recent", null, "", rec));
        }
        foreach (var g in groups)
        {
            if (only != null && g.Key != only) continue;
            var items = mru.Where(it => it.P.Group == g && Matches(it)).ToList();
            if (needsFirst) items = items.OrderBy(it => it.W.Agent?.State == "needs" ? 0 : 1).ToList(); // stable: MRU order otherwise
            if (items.Count > 0) outp.Add(new Section(g.Key, g.Name, g.Num, g.Color, items));
        }
        return outp;
    }

    static string Exe(string exe) => exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;

    public static List<Slot> Flat(List<Section> secs) => secs.SelectMany(s => s.Items.Select(it => Slot.Of(s.Key, it))).ToList();

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
        return Slot.Of(secs[k].Key, secs[k].Items[0]);
    }

    /// <summary>Columns layout, ↑ ↓: the card above or below in the same column, stopping at its ends.</summary>
    public static Slot? MoveInColumn(List<Section> secs, Slot? sel, int d)
    {
        if (secs.Count == 0) return null;
        var (k, j) = Locate(secs, sel);
        if (k < 0) return Slot.Of(secs[0].Key, secs[0].Items[0]);
        var items = secs[k].Items;
        return Slot.Of(secs[k].Key, items[Math.Clamp(j + d, 0, items.Count - 1)]);
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
        return Slot.Of(secs[k].Key, items[Math.Min(j, items.Count - 1)]);
    }

    /// <summary>Section and row of a card, or (-1, -1) when it isn't in view.</summary>
    static (int k, int j) Locate(List<Section> secs, Slot? sel)
    {
        if (sel is not Slot s) return (-1, -1);
        int k = secs.FindIndex(x => x.Key == s.Section);
        int j = k < 0 ? -1 : secs[k].Items.FindIndex(it => Slot.Of(s.Section, it) == s);
        return j < 0 ? (-1, -1) : (k, j);
    }

    /// <summary>First card of group n, or null when the group has no windows in view.</summary>
    public static Slot? Group(List<Section> secs, int n)
    {
        var s = secs.FirstOrDefault(x => x.Num == n);
        return s == null ? null : Slot.Of(s.Key, s.Items[0]);
    }

    public static Item? ItemAt(List<Section> secs, Slot? sel) =>
        sel is Slot s ? secs.FirstOrDefault(x => x.Key == s.Section)?.Items.FirstOrDefault(it => Slot.Of(s.Section, it) == s) : null;

    /// <summary>
    /// Alt+number: the most recent window in that group, skipping the one in front so pressing it
    /// again from inside the group moves on.
    /// </summary>
    public static Item? Direct(IReadOnlyList<Item> mru, Group g) => mru.Where(it => it.P.Group == g && !it.IsCurrent && !it.W.HiddenTab).FirstOrDefault();
}
