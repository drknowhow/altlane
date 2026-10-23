using System;
using System.Collections.Generic;
using System.Linq;

namespace Altlane;

/// <summary>
/// Where the overlay puts each header and card, in DIPs, for the grid (layout A), the columns layout
/// (layout C) and Win+Tab's lanes overview. Pure, so the self-test's unit mode can check the fitting.
/// </summary>
static class Layouts
{
    public const double CardGap = 6, SecGapX = 16, SecGapY = 12, HeadH = 22, HeadGap = 4, CapH = 20, CardPad = 5;
    public const double MaxCard = 232, MinCard = 96;

    public sealed record CardPos(Slot Slot, Item Item, double X, double Y);
    /// <param name="W">The column's width in the columns layout; 0 in the grid, where a header is as wide as its text.</param>
    public sealed record HeadPos(Section S, double X, double Y, double W = 0);
    public sealed record Result(double width, double height, double cw, double thumbH, List<CardPos> cards, List<HeadPos> heads);

    /// <summary>Card height: ring and padding, the thumbnail, a gap, the caption, padding and ring.</summary>
    public static double CardH(double thumbH) => CardPad + thumbH + 4 + CapH + CardPad;

    /// <summary>
    /// Sections flow left to right and wrap, each centered row as in the design. Card width starts
    /// large and shrinks until everything fits the screen; a section wider than the screen wraps its cards.
    /// </summary>
    public static Result Grid(List<Section> secs, double maxW, double maxH)
    {
        for (double cw = MaxCard; ; cw -= 8)
        {
            double thumbH = Math.Round(cw * 10 / 16);
            double cardH = CardH(thumbH);
            int perLine = Math.Max(1, (int)((maxW + CardGap) / (cw + CardGap)));
            var sizes = secs.Select(s =>
            {
                int n = s.Items.Count, k = Math.Min(n, perLine), lines = (n + perLine - 1) / perLine;
                return (w: k * cw + (k - 1) * CardGap, h: HeadH + HeadGap + lines * cardH + (lines - 1) * CardGap);
            }).ToList();

            var rows = new List<List<int>>();
            double rowW = 0;
            for (int i = 0; i < sizes.Count; i++)
            {
                if (rows.Count == 0 || rowW + SecGapX + sizes[i].w > maxW) { rows.Add(new List<int>()); rowW = -SecGapX; }
                rows[^1].Add(i);
                rowW += SecGapX + sizes[i].w;
            }
            double width = rows.Count == 0 ? 0 : rows.Max(r => r.Sum(i => sizes[i].w) + (r.Count - 1) * SecGapX);
            double height = rows.Sum(r => r.Max(i => sizes[i].h)) + Math.Max(0, rows.Count - 1) * SecGapY;
            if (height > maxH && cw - 8 >= MinCard) continue;

            var cards = new List<CardPos>();
            var heads = new List<HeadPos>();
            double y = 0;
            foreach (var r in rows)
            {
                double rw = r.Sum(i => sizes[i].w) + (r.Count - 1) * SecGapX;
                double x = (width - rw) / 2;
                foreach (int i in r)
                {
                    var s = secs[i];
                    heads.Add(new HeadPos(s, x, y));
                    for (int j = 0; j < s.Items.Count; j++)
                    {
                        int line = j / perLine, col = j % perLine;
                        cards.Add(new CardPos(new Slot(s.Key, s.Items[j].W.Hwnd), s.Items[j],
                            x + col * (cw + CardGap), y + HeadH + HeadGap + line * (cardH + CardGap)));
                    }
                    x += sizes[i].w + SecGapX;
                }
                y += r.Max(i => sizes[i].h) + SecGapY;
            }
            return new Result(width, height, cw, thumbH, cards, heads);
        }
    }

    /// <summary>
    /// One column per section, side by side, cards stacked down each one. Columns share one card width,
    /// as wide as fits up to MaxCard. A column too long for the screen continues in a second column of
    /// its own beside it, and cards shrink until the whole row fits the screen's width. The arrow keys
    /// still go by position in the section, so the down arrow at the foot of a column moves to the top
    /// of its next one.
    /// </summary>
    /// <param name="gapX">Space between columns; the lanes overview leaves room for its lane boxes.</param>
    public static Result Columns(List<Section> secs, double maxW, double maxH, double gapX = SecGapX)
    {
        int n = Math.Max(1, secs.Count);
        double start = Math.Max(MinCard, Math.Min(MaxCard, Math.Floor((maxW - (n - 1) * gapX) / n)));
        for (double cw = start; ; cw -= 8)
        {
            double thumbH = Math.Round(cw * 10 / 16);
            double cardH = CardH(thumbH);
            int fit = Math.Max(1, (int)((maxH - HeadH - HeadGap + CardGap) / (cardH + CardGap)));
            var widths = secs.Select(s =>
            {
                int subs = (s.Items.Count + fit - 1) / fit;
                return subs * cw + (subs - 1) * CardGap;
            }).ToList();
            double width = widths.Sum() + Math.Max(0, secs.Count - 1) * gapX;
            if (width > maxW && cw - 8 >= MinCard) continue;

            var cards = new List<CardPos>();
            var heads = new List<HeadPos>();
            double x = 0, height = 0;
            for (int i = 0; i < secs.Count; i++)
            {
                var s = secs[i];
                heads.Add(new HeadPos(s, x, 0, widths[i]));
                for (int j = 0; j < s.Items.Count; j++)
                    cards.Add(new CardPos(new Slot(s.Key, s.Items[j].W.Hwnd), s.Items[j],
                        x + j / fit * (cw + CardGap), HeadH + HeadGap + j % fit * (cardH + CardGap)));
                int rows = Math.Min(s.Items.Count, fit);
                height = Math.Max(height, HeadH + HeadGap + rows * cardH + (rows - 1) * CardGap);
                x += widths[i] + gapX;
            }
            return new Result(width, height, cw, thumbH, cards, heads);
        }
    }

    public const double LanesSide = 24, LanesTop = 84, LanePad = 8, LaneGapX = 28, LanesFootH = 46;
    public const double SearchTop = 20, SearchW = 460, SearchH = 36;

    /// <param name="X">Left edge of the columns, centered in the work area.</param>
    /// <param name="Y">Top of the columns, below the search box.</param>
    /// <param name="LaneH">Height of every lane: the columns' room down to the footer.</param>
    /// <param name="FootY">Top of the footer's two lines.</param>
    public sealed record Overview(Result L, double X, double Y, double LaneH, double FootY);

    /// <summary>
    /// Win+Tab's overview fills the work area: the search box on top, one full-height lane per section
    /// laid out by Columns, and the footer at the bottom. A lane's box is its column grown by LanePad.
    /// </summary>
    public static Overview Lanes(List<Section> secs, double workW, double workH)
    {
        double footY = workH - LanesFootH - 10;
        double laneH = footY - 14 - LanePad - LanesTop;
        var L = Columns(secs, workW - 2 * LanesSide - 2 * LanePad, laneH, LaneGapX);
        return new Overview(L, Math.Round((workW - L.width) / 2), LanesTop, laneH, footY);
    }
}
