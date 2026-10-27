using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using static Altlane.Layouts;

namespace Altlane;

/// <summary>Grid and Columns are Alt+Tab's two layouts; Lanes is Win+Tab's full-screen overview.</summary>
enum ViewKind { Grid, Columns, Lanes }

/// <summary>
/// Alt+Tab's view: a Recent section, then one section per group, each with cards that hold live DWM
/// thumbnails. The grid (layout A) wraps the sections into rows; the columns layout (layout C) gives
/// each section a column. Win+Tab's lanes overview fills the work area with one lane per group under a
/// search box. Built in code on a Canvas so every thumbnail rect is known exactly. Never takes activation
/// (WS_EX_NOACTIVATE); the keyboard hook feeds it keys instead. AllowsTransparency stays false
/// because DWM thumbnails don't draw into layered windows.
/// </summary>
sealed class Overlay : Window
{
    /// <param name="Needs">Claude Code waiting for you: text, and the pill behind it.</param>
    /// <param name="Work">Claude Code working.</param>
    sealed record Theme(Brush Pane, Brush Ink, Brush Muted, Brush Edge, Brush CardHi, Brush Accent, Brush ThumbBg, Brush Badge, Brush Lane,
        Brush Needs, Brush NeedsBg, Brush Work, Brush WorkBg);

    static readonly Theme Dark = new(B(0x26, 0x28, 0x2B), B(0xF2, 0xF2, 0xF2), B(0xA6, 0xAF, 0xB8), B(0x3A, 0x3D, 0x41),
        B(0x34, 0x37, 0x3B), B(0x60, 0xCD, 0xFF), B(0x1C, 0x1E, 0x21), B(0x31, 0x33, 0x37), B(0x2C, 0x2E, 0x32),
        B(0xF3, 0xB4, 0x4A), B(0x4A, 0x36, 0x13), B(0x7D, 0xB0, 0xFF), B(0x1D, 0x32, 0x54));
    static readonly Theme Light = new(B(0xF1, 0xF3, 0xF5), B(0x1B, 0x1F, 0x23), B(0x5D, 0x68, 0x72), B(0xD5, 0xD9, 0xDD),
        B(0xE1, 0xE5, 0xE9), B(0x00, 0x5F, 0xB8), B(0xE4, 0xE8, 0xEB), B(0xE4, 0xE8, 0xEB), B(0xE8, 0xEB, 0xEE),
        B(0xB8, 0x6E, 0x00), B(0xFB, 0xE7, 0xC4), B(0x1F, 0x63, 0xC9), B(0xDB, 0xE8, 0xFB));

    /// <summary>Self-test: draw in this theme instead of following Windows ("dark" or "light").</summary>
    public static string? ThemeOverride;
    static readonly FontFamily Ui = new("Segoe UI Variable Text, Segoe UI"), Mono = new("Cascadia Mono, Consolas"),
        Glyphs = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    const double Pad = 14, FilterH = 30, FootH = 44;

    readonly Canvas _canvas = new();
    readonly Dictionary<Slot, Border> _cards = new();
    readonly Dictionary<Slot, Item> _cardItems = new();
    /// <summary>
    /// Cards from recent views, reused while nothing they show has changed. With dozens of windows,
    /// building and measuring the cards was most of a view's time; a reused card keeps its measured
    /// layout. Keyed by everything a card draws, and pruned when the overlay hides.
    /// </summary>
    readonly Dictionary<CardKey, (Border card, int session)> _cardCache = new();
    readonly record struct CardKey(Slot Slot, Item Item, double Cw, double ThumbH, Theme T, string? Ago);
    int _session;
    readonly List<IntPtr> _thumbs = new();
    readonly Dictionary<Slot, IntPtr> _thumbOf = new();
    // Where a dragged card can be dropped: each group's column, lane or block, in canvas DIPs.
    readonly List<(Group g, Rect r)> _drops = new();
    readonly DispatcherTimer _toastTimer;
    TextBlock? _why;
    Theme _t = Dark;
    IntPtr _hwnd;
    bool _toastOnly;
    (IntPtr monitor, double left, double top, double width, double height, double scale) _mon;
    ViewKind _view;
    bool? _rounded;
    N.POINT _cursor;
    bool _moved;
    string _whyText = "";
    // Dragging a card onto another group.
    Slot? _press;
    Point _pressAt;
    bool _dragging;
    Group? _over;
    Popup? _ghost;
    UIElement? _dropMark, _dropLabel;
    // Each card's close button, and the one the left button went down on.
    readonly Dictionary<Border, Border> _closeOf = new();
    Slot? _selected, _closePress;

    public event Action<Slot>? CardClicked;
    /// <summary>A middle click on a card: close that window.</summary>
    public event Action<Slot>? CardMiddleClicked;
    /// <summary>A click on a card's close button.</summary>
    public event Action<Slot>? CardCloseClicked;
    /// <summary>Cards have a close button, shown on the selected card and the one under the mouse (Settings).</summary>
    public bool CloseButtons { get; set; } = true;
    /// <summary>The mouse moved onto a card. Not raised when a redraw puts a card under a still cursor.</summary>
    public event Action<Slot>? CardHovered;
    /// <summary>A card was dragged onto another group's column or lane.</summary>
    public event Action<Slot, Group>? CardDropped;
    /// <summary>Cards can be dragged onto another group (Settings' mouse option).</summary>
    public bool DragEnabled { get; set; } = true;
    /// <summary>Lanes only: a click on the overview outside every card and the search box.</summary>
    public event Action? BackgroundClicked;
    /// <summary>Fires once per show with (ms from show request to first composed frame).</summary>
    public event Action<double>? Composed;
    /// <summary>The last show's stages in ms, for the log: clearing, building cards, layout and showing, thumbnails.</summary>
    public (double clear, double draw, double place, double show, double layout, double thumbs) LastParts { get; private set; }
    /// <summary>How many of the last show's cards were reused from earlier views.</summary>
    public int LastReused { get; private set; }
    /// <summary>
    /// The last show's garbage collections (count and pause ms) and the UI thread's CPU cycles in
    /// millions. A slow show with few cycles and no GC spent its time waiting for a CPU.
    /// </summary>
    public (int gcs, double gcMs, double mcycles) LastCost { get; private set; }

    public IntPtr Hwnd => _hwnd;
    public bool IsUp => IsVisible && !_offscreen;
    bool _offscreen;

    public Overlay()
    {
        Title = "Altlane overlay";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = false;
        Content = _canvas;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = Win.ExStyle(_hwnd) | N.WS_EX_TOOLWINDOW | N.WS_EX_NOACTIVATE;
            N.SetWindowLongPtr(_hwnd, N.GWL_EXSTYLE, (IntPtr)ex);
            Corners(true);
        };
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); if (_toastOnly) HideOverlay(); };
        // WPF raises MouseMove when a redraw puts a card under a cursor that hasn't moved; only a real move hovers.
        PreviewMouseMove += (_, _) => { N.GetCursorPos(out var p); _moved = p.X != _cursor.X || p.Y != _cursor.Y; _cursor = p; };
        MouseLeftButtonUp += (_, _) => { if (_view == ViewKind.Lanes && IsUp && !_toastOnly) BackgroundClicked?.Invoke(); };
        // A close button closes only when the button went down on it too; this runs before its own handler.
        PreviewMouseLeftButtonDown += (_, _) => _closePress = null;
    }

    /// <summary>Rounded corners, except on the overview: it fills the work area, so round corners would show the desktop.</summary>
    void Corners(bool round)
    {
        if (_rounded == round || _hwnd == IntPtr.Zero) return;
        int pref = round ? 2 : 1;
        N.DwmSetWindowAttribute(_hwnd, N.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
        _rounded = round;
    }

    static Brush B(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

    static Brush Hex(string hex)
    {
        try { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
        catch (FormatException) { return B(0x8A, 0x8F, 0x98); }
    }

    static bool LightTheme()
    {
        if (ThemeOverride != null) return ThemeOverride == "light";
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("AppsUseLightTheme") is int v && v != 0;
    }

    /// <summary>The work area of the monitor under the mouse, in DIPs of that monitor.</summary>
    static (IntPtr, double, double, double, double, double) MonitorUnderCursor()
    {
        N.GetCursorPos(out var p);
        IntPtr m = N.MonitorFromPoint(p, N.MONITOR_DEFAULTTONEAREST);
        var mi = new N.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<N.MONITORINFO>() };
        N.GetMonitorInfo(m, ref mi);
        double s = N.GetDpiForMonitor(m, 0, out uint dx, out _) == 0 ? dx / 96.0 : 1.0;
        var r = mi.rcWork;
        return (m, r.Left, r.Top, (r.Right - r.Left) / s, (r.Bottom - r.Top) / s, s);
    }

    // ---------- show ----------

    /// <summary>Create the HWND and draw real cards with real thumbnails once, off-screen, so the first
    /// real show doesn't pay for first-time WPF and DWM setup.</summary>
    /// <param name="warm">Called once the off-screen frame is drawn and the window is hidden again.</param>
    public void Prewarm(List<Section> secs, ViewKind view, Action? warm = null)
    {
        new WindowInteropHelper(this).EnsureHandle();
        _offscreen = true;
        ShowSections(secs, null, "", 0, "", "", view, offscreen: true);
        EventHandler? done = null;
        done = (_, _) =>
        {
            CompositionTarget.Rendering -= done;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { HideOverlay(); _offscreen = false; warm?.Invoke(); });
        };
        CompositionTarget.Rendering += done;
    }

    /// <summary>
    /// Draw a view's cards into the hidden overlay and lay them out, without showing anything, so the next
    /// show reuses them. Returns how many cards weren't already built.
    /// </summary>
    public int Prebuild(List<Section> secs, ViewKind view)
    {
        if (IsVisible || _hwnd == IntPtr.Zero) return 0;
        _canvas.Children.Clear();
        _cards.Clear();
        _cardItems.Clear();
        _drops.Clear();
        _mon = MonitorUnderCursor();
        _t = LightTheme() ? Light : Dark;
        _session++;
        LastReused = 0;
        var slots = new List<(Slot, IntPtr, Rect)>();
        var (w, h) = view == ViewKind.Lanes ? DrawLanes(secs, "", 0, "", "", slots) : DrawPanel(secs, "", 0, "", "", view == ViewKind.Columns, slots);
        Width = w; Height = h;
        UpdateLayout();
        PruneCards();
        return _cards.Count - LastReused;
    }

    /// <param name="why">Footer line for the selected window.</param>
    /// <param name="hints">Key hints for the footer's second line.</param>
    public void ShowSections(List<Section> secs, Slot? sel, string filter, int hidden, string why, string hints, ViewKind view = ViewKind.Grid, bool offscreen = false)
    {
        long tReq = Stopwatch.GetTimestamp();
        int gc0 = GC.CollectionCount(0);
        var pause0 = GC.GetTotalPauseDuration();
        N.QueryThreadCycleTime(N.GetCurrentThread(), out ulong cyc0);
        _toastTimer.Stop();
        _toastOnly = false;
        EndDrag(); // a redraw while dragging (a tab list arrived, say) drops nothing
        ClearThumbs();
        _canvas.Children.Clear();
        _cards.Clear();
        _cardItems.Clear();
        _drops.Clear();
        long tClear = Stopwatch.GetTimestamp();

        if (!IsUp || offscreen) { _mon = MonitorUnderCursor(); _t = LightTheme() ? Light : Dark; N.GetCursorPos(out _cursor); _session++; }
        LastReused = 0;
        Background = _t.Pane;
        _view = view;
        var slots = new List<(Slot slot, IntPtr src, Rect r)>();
        var (winW, winH) = view == ViewKind.Lanes
            ? DrawLanes(secs, filter, hidden, why, hints, slots)
            : DrawPanel(secs, filter, hidden, why, hints, view == ViewKind.Columns, slots);
        long tDraw = Stopwatch.GetTimestamp();

        Position(winW, winH, offscreen, bottom: false);
        Corners(view != ViewKind.Lanes);
        SetSelection(sel, null);
        long tPlace = Stopwatch.GetTimestamp();

        if (!IsVisible) Show();
        long tShow = Stopwatch.GetTimestamp();
        UpdateLayout();
        long tLayout = Stopwatch.GetTimestamp();
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        foreach (var (slot, src, r) in slots) AddThumb(slot, src, r, dpi);
        long tThumbs = Stopwatch.GetTimestamp();
        LastParts = (Clock.Between(tReq, tClear), Clock.Between(tClear, tDraw), Clock.Between(tDraw, tPlace), Clock.Between(tPlace, tShow),
            Clock.Between(tShow, tLayout), Clock.Between(tLayout, tThumbs));
        N.QueryThreadCycleTime(N.GetCurrentThread(), out ulong cyc1);
        LastCost = (GC.CollectionCount(0) - gc0, (GC.GetTotalPauseDuration() - pause0).TotalMilliseconds, (cyc1 - cyc0) / 1e6);
        if (!offscreen) ReportComposed(tReq);
    }

    /// <summary>Alt+Tab: a pane sized to its cards, centered. Returns the window's size in DIPs.</summary>
    (double w, double h) DrawPanel(List<Section> secs, string filter, int hidden, string why, string hints, bool columns, List<(Slot, IntPtr, Rect)> slots)
    {
        double workW = _mon.width, workH = _mon.height;
        double maxW = workW * 0.92 - 2 * Pad;
        double maxH = workH * 0.88 - 2 * Pad - (filter.Length > 0 ? FilterH : 0) - FootH;
        var L = columns ? Layouts.Columns(secs, maxW, maxH) : Layouts.Grid(secs, maxW, maxH);

        double innerW = Math.Max(L.width, 560);
        double winW = innerW + 2 * Pad;
        double top = Pad;

        if (filter.Length > 0)
        {
            var bar = new TextBlock { FontFamily = Ui, FontSize = 12.5, Foreground = _t.Muted, TextTrimming = TextTrimming.CharacterEllipsis, Width = innerW };
            bar.Inlines.Add(new Run("Filter  "));
            bar.Inlines.Add(new Run(filter) { FontFamily = Mono, Foreground = _t.Ink, Background = _t.CardHi });
            int n = secs.Sum(s => s.Items.Count);
            bar.Inlines.Add(new Run($"   {n} match{(n == 1 ? "" : "es")} · Backspace to edit · Esc to clear"));
            Place(bar, Pad, top + 4);
            top += FilterH;
        }

        double ox = Pad + (innerW - L.width) / 2, oy = top;
        if (secs.Count == 0)
        {
            Place(Empty(filter, innerW), Pad, oy + 24);
            L = L with { height = 70 };
        }
        foreach (var h in L.heads) Place(Header(h.S, h.W), ox + h.X + (h.W > 0 ? 0 : 4), oy + h.Y);
        PlaceCards(L, ox, oy, slots);
        foreach (var h in L.heads)
        {
            if (h.S.Num is not int || _cardItems.Values.FirstOrDefault(i => i.P.Group?.Key == h.S.Key)?.P.Group is not Group g) continue;
            Rect r = columns
                ? new Rect(ox + h.X, oy + h.Y, h.W, L.height)
                : L.cards.Where(c => c.Slot.Section == h.S.Key).Aggregate(new Rect(ox + h.X, oy + h.Y, 1, HeadH),
                    (u, c) => Rect.Union(u, new Rect(ox + c.X, oy + c.Y, L.cw, CardH(L.thumbH))));
            r.Inflate(5, 5);
            _drops.Add((g, r));
        }
        top = oy + L.height + 8;

        Footer(why, hints, hidden, Pad, top, innerW);
        return (winW, top + FootH - 4 + Pad);
    }

    /// <summary>Win+Tab: the whole work area, the search box on top, one full-height lane per section.</summary>
    (double w, double h) DrawLanes(List<Section> secs, string filter, int hidden, string why, string hints, List<(Slot, IntPtr, Rect)> slots)
    {
        double workW = _mon.width, workH = _mon.height;
        var o = Layouts.Lanes(secs, workW, workH);
        Place(SearchBox(secs, filter), Math.Round((workW - SearchW) / 2), SearchTop);
        foreach (var h in o.L.heads)
            Place(new Border
            {
                Width = h.W + 2 * LanePad, Height = o.LaneH + 2 * LanePad, CornerRadius = new CornerRadius(10),
                Background = _t.Lane, BorderBrush = _t.Edge, BorderThickness = new Thickness(1),
            }, o.X + h.X - LanePad, o.Y - LanePad);
        if (secs.Count == 0) Place(Empty(filter, workW), 0, o.Y + 120);
        foreach (var h in o.L.heads) Place(Header(h.S, h.W), o.X + h.X, o.Y + h.Y);
        PlaceCards(o.L, o.X, o.Y, slots);
        foreach (var h in o.L.heads)
            if (_cardItems.Values.FirstOrDefault(i => i.P.Group?.Key == h.S.Key)?.P.Group is Group g)
                _drops.Add((g, new Rect(o.X + h.X - LanePad, o.Y - LanePad, h.W + 2 * LanePad, o.LaneH + 2 * LanePad)));
        Footer(why, hints, hidden, LanesSide, o.FootY, workW - 2 * LanesSide);
        return (workW, workH);
    }

    void PlaceCards(Layouts.Result L, double ox, double oy, List<(Slot, IntPtr, Rect)> slots)
    {
        foreach (var c in L.cards)
        {
            // A waiting session's card counts the minutes, so the minutes are part of what it shows.
            var key = new CardKey(c.Slot, c.Item, L.cw, L.thumbH, _t, c.Item.W.Agent is { State: "needs" } a ? Terminals.Ago(a.Since) : null);
            Border card;
            if (_cardCache.TryGetValue(key, out var hit)) { card = hit.card; LastReused++; }
            else card = Card(c.Item, c.Slot, L.cw, L.thumbH);
            _cardCache[key] = (card, _session);
            Place(card, ox + c.X, oy + c.Y);
            _cards[c.Slot] = card;
            _cardItems[c.Slot] = c.Item;
            // A tab that isn't in front has no picture: the window's thumbnail would show another tab.
            if (!c.Item.W.Minimized && !c.Item.W.HiddenTab)
                slots.Add((c.Slot, c.Item.W.Hwnd, new Rect(ox + c.X + CardPad, oy + c.Y + CardPad, L.cw - 2 * CardPad, L.thumbH)));
        }
    }

    TextBlock Empty(string filter, double w) =>
        new() { Text = filter.Length > 0 ? "No windows match" : "No windows", FontFamily = Ui, FontSize = 14, Foreground = _t.Muted, Width = w, TextAlignment = TextAlignment.Center };

    /// <summary>The Why line for the selected window, then the key hints.</summary>
    void Footer(string why, string hints, int hidden, double x, double y, double w)
    {
        _why = new TextBlock { FontFamily = Ui, FontSize = 12, Foreground = _t.Muted, TextTrimming = TextTrimming.CharacterEllipsis, Width = w };
        SetWhy(why);
        Place(_why, x, y);
        var hintRow = new TextBlock { FontFamily = Ui, FontSize = 11, Foreground = _t.Muted, TextTrimming = TextTrimming.CharacterEllipsis, Width = w, Opacity = 0.85 };
        hintRow.Text = hidden > 0 ? $"{hints}   ·   {hidden} hidden by a rule" : hints;
        Place(hintRow, x, y + 20);
    }

    /// <summary>The overview's search box: what has been typed, or a prompt to type.</summary>
    UIElement SearchBox(List<Section> secs, string filter)
    {
        int n = secs.Sum(s => s.Items.Count);
        var dock = new DockPanel { LastChildFill = true };
        var glyph = new TextBlock { Text = "\uE721", FontFamily = Glyphs, FontSize = 13, Foreground = _t.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 9, 0) };
        DockPanel.SetDock(glyph, Dock.Left);
        dock.Children.Add(glyph);
        var count = new TextBlock
        {
            Text = filter.Length > 0 ? $"{n} match{(n == 1 ? "" : "es")} · Backspace to edit · Esc to clear" : $"{n} window{(n == 1 ? "" : "s")}",
            FontFamily = Ui, FontSize = 12, Foreground = _t.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
        };
        DockPanel.SetDock(count, Dock.Right);
        dock.Children.Add(count);
        var text = new TextBlock { FontFamily = Ui, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        text.Inlines.Add(filter.Length > 0
            ? new Run(filter) { FontFamily = Mono, Foreground = _t.Ink, Background = _t.CardHi }
            : new Run("Type to search windows") { Foreground = _t.Muted });
        dock.Children.Add(text);
        var box = new Border
        {
            Width = SearchW, Height = SearchH, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = _t.Edge,
            Background = _t.Badge, Padding = new Thickness(13, 0, 13, 0), Child = dock,
        };
        box.MouseLeftButtonUp += (_, e) => e.Handled = true; // typing is the search; a click on the box isn't a click outside
        return box;
    }

    void ReportComposed(long tReq)
    {
        EventHandler? onFrame = null;
        onFrame = (_, _) =>
        {
            CompositionTarget.Rendering -= onFrame;
            // Rendering fires before the frame is drawn; wait until after render, then for DWM to compose.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                N.DwmFlush();
                Composed?.Invoke(Clock.Between(tReq, Stopwatch.GetTimestamp()));
            });
        };
        CompositionTarget.Rendering += onFrame;
    }

    /// <summary>Size in DIPs, position in physical pixels, so a monitor with another scale still centers right.</summary>
    void Position(double w, double h, bool offscreen, bool bottom)
    {
        if (_hwnd == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();
        Width = w; Height = h;
        if (offscreen) { Left = -20000; Top = -20000; return; }
        double s = _mon.scale;
        int left = (int)_mon.left, top = (int)_mon.top, workW = (int)Math.Round(_mon.width * s), workH = (int)Math.Round(_mon.height * s);
        var (x, y) = Layouts.Center(left, top, workW, workH, (int)Math.Round(w * s), (int)Math.Round(h * s), s, bottom);
        double before = VisualTreeHelper.GetDpi(this).DpiScaleX;
        N.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, N.SWP_NOSIZE | N.SWP_NOZORDER | N.SWP_NOACTIVATE);
        // Onto a monitor of another scale, WPF resizes the window to the new scale during the move, and
        // Windows may shift it while it does. Center it again on the size it ended up with.
        if (VisualTreeHelper.GetDpi(this).DpiScaleX != before && N.GetWindowRect(_hwnd, out var r))
        {
            var (x2, y2) = Layouts.Center(left, top, workW, workH, r.Right - r.Left, r.Bottom - r.Top, s, bottom);
            if (x2 != r.Left || y2 != r.Top) N.SetWindowPos(_hwnd, IntPtr.Zero, x2, y2, 0, 0, N.SWP_NOSIZE | N.SWP_NOZORDER | N.SWP_NOACTIVATE);
        }
    }

    void Place(UIElement e, double x, double y) { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); _canvas.Children.Add(e); }

    // ---------- pieces ----------

    /// <param name="w">Column width in the columns layout, where the header spans its column; 0 in the grid.</param>
    UIElement Header(Section s, double w = 0)
    {
        var parts = new List<FrameworkElement>();
        if (s.Num is int num)
            parts.Add(new Border
            {
                MinWidth = 17, Height = 17, Padding = new Thickness(4, 0, 4, 0), CornerRadius = new CornerRadius(4),
                Background = _t.Badge, BorderBrush = _t.Edge, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = num.ToString(), FontFamily = Mono, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = _t.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
        if (s.Color.Length > 0)
            parts.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = Hex(s.Color), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = s.Label, FontFamily = Ui, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = s.Num == null ? _t.Muted : _t.Ink, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var count = new TextBlock { Text = "  " + s.Items.Count, FontFamily = Ui, FontSize = 12.5, Foreground = _t.Muted, VerticalAlignment = VerticalAlignment.Center };
        if (w <= 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Height = HeadH };
            foreach (var part in parts) row.Children.Add(part);
            row.Children.Add(label);
            row.Children.Add(count);
            return row;
        }
        // Columns: the count at the right edge, the name cut short to fit, a rule in the group's colour.
        var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(4, 0, 4, 0) };
        foreach (var part in parts) { DockPanel.SetDock(part, Dock.Left); dock.Children.Add(part); }
        DockPanel.SetDock(count, Dock.Right);
        dock.Children.Add(count);
        dock.Children.Add(label);
        return new Border { Width = w, Height = HeadH, BorderThickness = new Thickness(0, 0, 0, 2), BorderBrush = s.Color.Length > 0 ? Hex(s.Color) : _t.Edge, Child = dock };
    }

    Border Card(Item it, Slot slot, double cw, double thumbH)
    {
        var icon = Icons.For(it.W);
        var thumb = new Border { Height = thumbH, CornerRadius = new CornerRadius(5), Background = _t.ThumbBg, ClipToBounds = true };
        if (it.W.HiddenTab) thumb.Child = TabFace(it.W, thumbH);
        else if (it.W.Minimized)
        {
            // DWM keeps no live image of a minimized window; show what it is instead.
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            if (icon != null) stack.Children.Add(new Image { Source = icon, Width = 36, Height = 36 });
            stack.Children.Add(new TextBlock { Text = "minimized", FontFamily = Ui, FontSize = 10.5, Foreground = _t.Muted, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
            thumb.Child = stack;
        }

        var cap = new DockPanel { Height = CapH, LastChildFill = true, Margin = new Thickness(0, 4, 0, 0) };
        if (icon != null)
        {
            var img = new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(1, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(img, Dock.Left);
            cap.Children.Add(img);
        }
        // In the caption, not over the thumbnail: DWM draws thumbnails on top of everything.
        var close = CloseX(slot);
        DockPanel.SetDock(close, Dock.Right);
        cap.Children.Add(close);
        var pill = it.IsCurrent ? Pill("NOW", _t.Badge, _t.Muted)
            : it.W.Agent?.State switch { "needs" => Pill("Needs you", _t.NeedsBg, _t.Needs), "working" => Pill("Working", _t.WorkBg, _t.Work), _ => null };
        if (pill != null)
        {
            DockPanel.SetDock(pill, Dock.Right);
            cap.Children.Add(pill);
        }
        cap.Children.Add(new TextBlock { Text = it.W.Title, FontFamily = Ui, FontSize = 12, Foreground = _t.Ink, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });

        var body = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(thumb, Dock.Top);
        DockPanel.SetDock(cap, Dock.Top);
        body.Children.Add(thumb);
        body.Children.Add(cap);

        var card = new Border
        {
            Width = cw, Height = CardH(thumbH), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2), Padding = new Thickness(CardPad - 2),
            Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Child = body, Cursor = Cursors.Hand, Tag = slot,
        };
        // The window sets these too, but a card taken off the canvas loses inherited values and gets them
        // back when it's added again, which re-measures it. Set on the card, a reused card stays measured.
        card.UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(card, TextFormattingMode.Display);
        RenderOptions.SetBitmapScalingMode(card, BitmapScalingMode.HighQuality);
        card.MouseLeftButtonDown += (s, e) =>
        {
            e.Handled = true;
            _press = (Slot)((Border)s).Tag;
            _pressAt = e.GetPosition(_canvas);
            ((Border)s).CaptureMouse();
        };
        card.MouseLeftButtonUp += (s, e) =>
        {
            e.Handled = true;
            var b = (Border)s;
            var slot = (Slot)b.Tag;
            bool pressed = _press == slot, dropped = _dragging;
            var over = _over;
            EndDrag();
            b.ReleaseMouseCapture();
            if (dropped) { if (over != null) CardDropped?.Invoke(slot, over); }
            else if (pressed) CardClicked?.Invoke(slot);
        };
        card.LostMouseCapture += (_, _) => { if (_dragging) EndDrag(); };
        card.MouseEnter += (s, _) => ShowClose((Border)s);
        card.MouseLeave += (s, _) => ShowClose((Border)s);
        card.MouseUp += (s, e) => { if (e.ChangedButton == MouseButton.Middle) { e.Handled = true; CardMiddleClicked?.Invoke((Slot)((Border)s).Tag); } };
        card.MouseMove += (s, e) =>
        {
            if (_press is Slot p && e.LeftButton == MouseButtonState.Pressed && DragEnabled) { DragTo(p, e.GetPosition(_canvas)); return; }
            if (_moved) { _moved = false; CardHovered?.Invoke((Slot)((Border)s).Tag); }
        };
        _closeOf[card] = close;
        return card;
    }

    static readonly Brush CloseRed = B(0xC4, 0x2B, 0x1C);

    /// <summary>A card's close button, red under the mouse like a window's own. Hidden until ShowClose shows it.</summary>
    Border CloseX(Slot slot)
    {
        var t = _t;
        var glyph = new TextBlock { Text = "\uE8BB", FontFamily = Glyphs, FontSize = 9, Foreground = t.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var x = new Border
        {
            Width = 22, Height = 18, CornerRadius = new CornerRadius(4), Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent, Child = glyph, Cursor = Cursors.Arrow, Visibility = CloseButtons ? Visibility.Hidden : Visibility.Collapsed,
        };
        x.MouseEnter += (_, _) => { x.Background = CloseRed; glyph.Foreground = Brushes.White; };
        x.MouseLeave += (_, _) => { x.Background = Brushes.Transparent; glyph.Foreground = t.Muted; };
        // Handled, so the card neither starts a drag nor switches to the window.
        x.MouseLeftButtonDown += (_, e) => { e.Handled = true; _closePress = slot; };
        x.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            bool pressed = _closePress == slot;
            _closePress = null;
            if (pressed) CardCloseClicked?.Invoke(slot);
        };
        return x;
    }

    /// <summary>The close button shows on the selected card and the one under the mouse; elsewhere it keeps its space.</summary>
    void ShowClose(Border card)
    {
        if (!_closeOf.TryGetValue(card, out var x)) return;
        bool on = CloseButtons && (card.IsMouseOver || _selected == (Slot)card.Tag);
        x.Visibility = !CloseButtons ? Visibility.Collapsed : on ? Visibility.Visible : Visibility.Hidden;
        if (!on || !x.IsMouseOver)
        {
            // A reused card can come back with the red it had when the view closed under the mouse.
            x.Background = Brushes.Transparent;
            if (x.Child is TextBlock g) g.Foreground = _t.Muted;
        }
    }

    Border Pill(string text, Brush bg, Brush fg) => new()
    {
        CornerRadius = new CornerRadius(4), Background = bg, Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontFamily = Ui, FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = fg },
    };

    /// <summary>A terminal tab that isn't in front. Windows draws no picture of it, so the card says what it is.</summary>
    UIElement TabFace(WinInfo w, double thumbH)
    {
        var t = w.Tab!;
        var a = w.Agent;
        var g = new System.Windows.Controls.Grid { Margin = new Thickness(8, 6, 8, 6) };
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition());
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (thumbH >= 60)
            g.Children.Add(new TextBlock { Text = $"Terminal tab {t.Index + 1} of {t.Count}", FontFamily = Ui, FontSize = 10, Foreground = _t.Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        var proj = new TextBlock
        {
            Text = a?.Project ?? Terminals.Bare(w.Title), FontFamily = Ui, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = _t.Ink,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Controls.Grid.SetRow(proj, 1);
        g.Children.Add(proj);
        var (text, brush) = a?.State switch
        {
            "needs" => ($"Waiting for you · {Terminals.Ago(a.Since)}", _t.Needs),
            "working" => ("Working", _t.Work),
            "idle" => ("Idle", _t.Muted),
            _ => ("Not the tab in front", _t.Muted),
        };
        var state = new TextBlock
        {
            Text = text, FontFamily = Ui, FontSize = 10.5, Foreground = brush, TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = a?.State is "needs" or "working" ? FontWeights.SemiBold : FontWeights.Normal,
        };
        System.Windows.Controls.Grid.SetRow(state, 2);
        g.Children.Add(state);
        return g;
    }

    // ---------- drag to regroup ----------

    void DragTo(Slot slot, Point p)
    {
        if (!_dragging)
        {
            if ((p - _pressAt).Length < 6 || !_cardItems.TryGetValue(slot, out var it)) return;
            _dragging = true;
            if (_cards.TryGetValue(slot, out var card)) card.Opacity = 0.35;
            ThumbOpacity(slot, 90);
            // DWM draws thumbnails over everything in this window, so the card being dragged is a popup of its own.
            _ghost = new Popup { AllowsTransparency = true, Placement = PlacementMode.Relative, PlacementTarget = _canvas, IsHitTestVisible = false, Child = Ghost(it) };
            _ghost.IsOpen = true;
        }
        _ghost!.HorizontalOffset = p.X + 10;
        _ghost.VerticalOffset = p.Y + 10;
        var item = _cardItems.GetValueOrDefault(slot);
        var over = _drops.FirstOrDefault(d => d.r.Contains(p));
        var g = over.g == item?.P.Group ? null : over.g;
        if (g == _over) return;
        _over = g;
        if (_dropMark != null) _canvas.Children.Remove(_dropMark);
        if (_dropLabel != null) _canvas.Children.Remove(_dropLabel);
        _dropMark = _dropLabel = null;
        if (g == null) { SetWhy(_whyText); return; }
        var r = over.r;
        _dropMark = new Rectangle
        {
            Width = r.Width, Height = r.Height, RadiusX = 9, RadiusY = 9, Stroke = _t.Accent, StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 3, 2 }, IsHitTestVisible = false,
        };
        Place(_dropMark, r.X, r.Y);
        _dropLabel = new Border
        {
            CornerRadius = new CornerRadius(5), BorderBrush = _t.Accent, BorderThickness = new Thickness(1), Background = _t.Pane, Padding = new Thickness(7, 2, 7, 3), IsHitTestVisible = false,
            Child = new TextBlock { Text = "Move to " + g.Name, FontFamily = Ui, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = _t.Accent },
        };
        Place(_dropLabel, r.X + 8, r.Bottom - 30);
        string what = item?.W.Tab != null ? "this tab" : "this window";
        SetWhy($"Drop to move {what} to {g.Name}", keep: false);
    }

    UIElement Ghost(Item it)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (Icons.For(it.W) is ImageSource icon) row.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = it.W.Title, FontFamily = Ui, FontSize = 12, Foreground = _t.Ink, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200, VerticalAlignment = VerticalAlignment.Center });
        return new Border
        {
            Child = row, Background = _t.Pane, BorderBrush = _t.Accent, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 6, 9, 6), Margin = new Thickness(12), RenderTransform = new RotateTransform(-2),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 6, Opacity = 0.35 },
        };
    }

    void EndDrag()
    {
        bool was = _dragging;
        _dragging = false;
        _over = null;
        if (_ghost != null) { _ghost.IsOpen = false; _ghost = null; }
        if (_dropMark != null) _canvas.Children.Remove(_dropMark);
        if (_dropLabel != null) _canvas.Children.Remove(_dropLabel);
        _dropMark = _dropLabel = null;
        if (was && _press is Slot s)
        {
            if (_cards.TryGetValue(s, out var card)) card.Opacity = 1;
            ThumbOpacity(s, 255);
            SetWhy(_whyText);
        }
        _press = null;
    }

    void ThumbOpacity(Slot s, byte opacity)
    {
        if (!_thumbOf.TryGetValue(s, out var thumb)) return;
        var p = new N.DWM_THUMBNAIL_PROPERTIES { dwFlags = N.DWM_TNP_OPACITY, opacity = opacity };
        N.DwmUpdateThumbnailProperties(thumb, ref p);
    }

    void AddThumb(Slot card, IntPtr src, Rect slot, double dpi)
    {
        if (N.DwmRegisterThumbnail(_hwnd, src, out IntPtr thumb) != 0) return;
        // Fit the source's aspect ratio inside the slot.
        Rect r = slot;
        if (N.DwmQueryThumbnailSourceSize(thumb, out var s) == 0 && s.cx > 0 && s.cy > 0)
        {
            double k = Math.Min(slot.Width / s.cx, slot.Height / s.cy);
            double w = s.cx * k, h = s.cy * k;
            r = new Rect(slot.X + (slot.Width - w) / 2, slot.Y + (slot.Height - h) / 2, w, h);
        }
        var p = new N.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = N.DWM_TNP_RECTDESTINATION | N.DWM_TNP_VISIBLE | N.DWM_TNP_OPACITY | N.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new N.RECT((int)(r.Left * dpi), (int)(r.Top * dpi), (int)(r.Right * dpi), (int)(r.Bottom * dpi)),
            opacity = 255, fVisible = true, fSourceClientAreaOnly = false,
        };
        N.DwmUpdateThumbnailProperties(thumb, ref p);
        _thumbs.Add(thumb);
        _thumbOf[card] = thumb;
    }

    // ---------- updates ----------

    public void SetSelection(Slot? sel, string? why)
    {
        _selected = sel;
        foreach (var (slot, card) in _cards)
        {
            bool on = sel is Slot s && s == slot;
            card.Background = on ? _t.CardHi : Brushes.Transparent;
            card.BorderBrush = on ? _t.Accent : Brushes.Transparent;
            ShowClose(card);
        }
        if (why != null) SetWhy(why);
    }

    /// <param name="keep">False for a passing message (drop here) that the Why line comes back from.</param>
    void SetWhy(string why, bool keep = true)
    {
        if (keep) _whyText = why;
        if (_why == null) return;
        _why.Inlines.Clear();
        int colon = why.IndexOf(": ", StringComparison.Ordinal);
        if (colon > 0 && colon < 40)
        {
            _why.Inlines.Add(new Run(why[..(colon + 1)]) { Foreground = _t.Ink, FontWeight = FontWeights.SemiBold });
            _why.Inlines.Add(new Run(why[(colon + 1)..]));
        }
        else _why.Inlines.Add(new Run(why));
    }

    /// <summary>A short message. Over an open view it replaces the footer line; alone it's a small pane near the bottom.</summary>
    public void Toast(string text)
    {
        if (IsUp && !_toastOnly) { SetWhy(text); return; }
        EndDrag();
        ClearThumbs();
        _canvas.Children.Clear();
        _cards.Clear();
        _cardItems.Clear();
        _drops.Clear();
        _why = null;
        _mon = MonitorUnderCursor();
        _t = LightTheme() ? Light : Dark;
        Background = _t.Pane;
        _view = ViewKind.Grid;
        Corners(true);
        var tb = new TextBlock { Text = text, FontFamily = Ui, FontSize = 13, Foreground = _t.Ink, MaxWidth = _mon.width * 0.6, TextTrimming = TextTrimming.CharacterEllipsis };
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Place(tb, 16, 10);
        Position(Math.Ceiling(tb.DesiredSize.Width) + 32, Math.Ceiling(tb.DesiredSize.Height) + 20, false, bottom: true);
        _toastOnly = true;
        if (!IsVisible) Show();
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    void ClearThumbs()
    {
        foreach (var t in _thumbs) N.DwmUnregisterThumbnail(t);
        _thumbs.Clear();
        _thumbOf.Clear();
    }

    /// <summary>Self-test: where each card is on screen, in physical pixels.</summary>
    public List<object> CardRects()
    {
        var list = new List<object>();
        foreach (var (slot, card) in _cards)
        {
            var a = card.PointToScreen(new Point(0, 0));
            var b = card.PointToScreen(new Point(card.Width, card.Height));
            // The close button's rect, and whether it shows.
            var x = _closeOf[card];
            var xa = x.PointToScreen(new Point(0, 0));
            var xb = x.PointToScreen(new Point(x.ActualWidth, x.ActualHeight));
            list.Add(new
            {
                sec = slot.Section, hwnd = slot.Hwnd.ToInt64(), tab = slot.Tab, x = (int)a.X, y = (int)a.Y, w = (int)(b.X - a.X), h = (int)(b.Y - a.Y),
                close = new { x = (int)xa.X, y = (int)xa.Y, w = (int)(xb.X - xa.X), h = (int)(xb.Y - xa.Y), shown = x.Visibility == Visibility.Visible },
            });
        }
        return list;
    }

    /// <summary>Self-test: where each group's drop area is on screen, in physical pixels.</summary>
    public List<object> DropRects()
    {
        var list = new List<object>();
        foreach (var (g, r) in _drops)
        {
            var a = _canvas.PointToScreen(r.TopLeft);
            var b = _canvas.PointToScreen(r.BottomRight);
            list.Add(new { group = g.Key, x = (int)a.X, y = (int)a.Y, w = (int)(b.X - a.X), h = (int)(b.Y - a.Y) });
        }
        return list;
    }

    /// <summary>Save what is on screen under the overlay, thumbnails included, as a PNG.</summary>
    public void Capture(string path)
    {
        if (!N.GetWindowRect(_hwnd, out var r)) return;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(w, h));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    public void HideOverlay()
    {
        EndDrag();
        _toastTimer.Stop();
        _toastOnly = false;
        ClearThumbs();
        Hide();
        PruneCards();
    }

    /// <summary>Cards not drawn in the last three views go; after a filter or a closed window most of the rest stay.</summary>
    void PruneCards()
    {
        foreach (var k in _cardCache.Where(kv => kv.Value.session < _session - 2).Select(kv => kv.Key).ToList()) _cardCache.Remove(k);
        var kept = _cardCache.Values.Select(v => v.card).ToHashSet();
        foreach (var c in _closeOf.Keys.Where(c => !kept.Contains(c)).ToList()) _closeOf.Remove(c);
    }
}
