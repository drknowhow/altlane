using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
    sealed record Theme(Brush Pane, Brush Ink, Brush Muted, Brush Edge, Brush CardHi, Brush Accent, Brush ThumbBg, Brush Badge, Brush Lane);

    static readonly Theme Dark = new(B(0x26, 0x28, 0x2B), B(0xF2, 0xF2, 0xF2), B(0xA6, 0xAF, 0xB8), B(0x3A, 0x3D, 0x41),
        B(0x34, 0x37, 0x3B), B(0x60, 0xCD, 0xFF), B(0x1C, 0x1E, 0x21), B(0x31, 0x33, 0x37), B(0x2C, 0x2E, 0x32));
    static readonly Theme Light = new(B(0xF1, 0xF3, 0xF5), B(0x1B, 0x1F, 0x23), B(0x5D, 0x68, 0x72), B(0xD5, 0xD9, 0xDD),
        B(0xE1, 0xE5, 0xE9), B(0x00, 0x5F, 0xB8), B(0xE4, 0xE8, 0xEB), B(0xE4, 0xE8, 0xEB), B(0xE8, 0xEB, 0xEE));
    static readonly FontFamily Ui = new("Segoe UI Variable Text, Segoe UI"), Mono = new("Cascadia Mono, Consolas"),
        Glyphs = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    const double Pad = 14, FilterH = 30, FootH = 44;

    readonly Canvas _canvas = new();
    readonly Dictionary<Slot, Border> _cards = new();
    readonly List<IntPtr> _thumbs = new();
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

    public event Action<Slot>? CardClicked;
    /// <summary>Lanes only: the mouse moved onto a card. Not raised when a redraw puts a card under a still cursor.</summary>
    public event Action<Slot>? CardHovered;
    /// <summary>Lanes only: a click on the overview outside every card and the search box.</summary>
    public event Action? BackgroundClicked;
    /// <summary>Fires once per show with (ms from show request to first composed frame).</summary>
    public event Action<double>? Composed;

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

    /// <param name="why">Footer line for the selected window.</param>
    /// <param name="hints">Key hints for the footer's second line.</param>
    public void ShowSections(List<Section> secs, Slot? sel, string filter, int hidden, string why, string hints, ViewKind view = ViewKind.Grid, bool offscreen = false)
    {
        long tReq = Stopwatch.GetTimestamp();
        _toastTimer.Stop();
        _toastOnly = false;
        ClearThumbs();
        _canvas.Children.Clear();
        _cards.Clear();

        if (!IsUp || offscreen) { _mon = MonitorUnderCursor(); _t = LightTheme() ? Light : Dark; N.GetCursorPos(out _cursor); }
        Background = _t.Pane;
        _view = view;
        var slots = new List<(IntPtr src, Rect r)>();
        var (winW, winH) = view == ViewKind.Lanes
            ? DrawLanes(secs, filter, hidden, why, hints, slots)
            : DrawPanel(secs, filter, hidden, why, hints, view == ViewKind.Columns, slots);

        Position(winW, winH, offscreen, bottom: false);
        Corners(view != ViewKind.Lanes);
        SetSelection(sel, null);

        if (!IsVisible) Show();
        UpdateLayout();
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        foreach (var (src, r) in slots) AddThumb(src, r, dpi);
        if (!offscreen) ReportComposed(tReq);
    }

    /// <summary>Alt+Tab: a pane sized to its cards, centered. Returns the window's size in DIPs.</summary>
    (double w, double h) DrawPanel(List<Section> secs, string filter, int hidden, string why, string hints, bool columns, List<(IntPtr src, Rect r)> slots)
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
        top = oy + L.height + 8;

        Footer(why, hints, hidden, Pad, top, innerW);
        return (winW, top + FootH - 4 + Pad);
    }

    /// <summary>Win+Tab: the whole work area, the search box on top, one full-height lane per section.</summary>
    (double w, double h) DrawLanes(List<Section> secs, string filter, int hidden, string why, string hints, List<(IntPtr src, Rect r)> slots)
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
        Footer(why, hints, hidden, LanesSide, o.FootY, workW - 2 * LanesSide);
        return (workW, workH);
    }

    void PlaceCards(Layouts.Result L, double ox, double oy, List<(IntPtr src, Rect r)> slots)
    {
        foreach (var c in L.cards)
        {
            var card = Card(c.Item, c.Slot, L.cw, L.thumbH);
            Place(card, ox + c.X, oy + c.Y);
            _cards[c.Slot] = card;
            if (!c.Item.W.Minimized) slots.Add((c.Item.W.Hwnd, new Rect(ox + c.X + CardPad, oy + c.Y + CardPad, L.cw - 2 * CardPad, L.thumbH)));
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
        int pxW = (int)Math.Round(w * s), pxH = (int)Math.Round(h * s);
        int x = (int)(_mon.left + (_mon.width * s - pxW) / 2);
        int y = bottom ? (int)(_mon.top + _mon.height * s - pxH - 72 * s) : (int)(_mon.top + (_mon.height * s - pxH) / 2);
        N.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, N.SWP_NOSIZE | N.SWP_NOZORDER | N.SWP_NOACTIVATE);
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
        var thumb = new Border { Height = thumbH, CornerRadius = new CornerRadius(5), Background = _t.ThumbBg };
        if (it.W.Minimized)
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
        if (it.IsCurrent)
        {
            // In the caption, not over the thumbnail: DWM draws thumbnails on top of everything.
            var now = new Border
            {
                CornerRadius = new CornerRadius(4), Background = _t.Badge, Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "NOW", FontFamily = Ui, FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = _t.Muted },
            };
            DockPanel.SetDock(now, Dock.Right);
            cap.Children.Add(now);
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
        card.MouseLeftButtonUp += (s, e) => { e.Handled = true; CardClicked?.Invoke((Slot)((Border)s).Tag); };
        card.MouseMove += (s, _) => { if (_moved && _view == ViewKind.Lanes) { _moved = false; CardHovered?.Invoke((Slot)((Border)s).Tag); } };
        return card;
    }

    void AddThumb(IntPtr src, Rect slot, double dpi)
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
    }

    // ---------- updates ----------

    public void SetSelection(Slot? sel, string? why)
    {
        foreach (var (slot, card) in _cards)
        {
            bool on = sel is Slot s && s == slot;
            card.Background = on ? _t.CardHi : Brushes.Transparent;
            card.BorderBrush = on ? _t.Accent : Brushes.Transparent;
        }
        if (why != null) SetWhy(why);
    }

    void SetWhy(string why)
    {
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
        ClearThumbs();
        _canvas.Children.Clear();
        _cards.Clear();
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
    }

    /// <summary>Self-test: where each card is on screen, in physical pixels.</summary>
    public List<object> CardRects()
    {
        var list = new List<object>();
        foreach (var (slot, card) in _cards)
        {
            var a = card.PointToScreen(new Point(0, 0));
            var b = card.PointToScreen(new Point(card.Width, card.Height));
            list.Add(new { sec = slot.Section, hwnd = slot.Hwnd.ToInt64(), x = (int)a.X, y = (int)a.Y, w = (int)(b.X - a.X), h = (int)(b.Y - a.Y) });
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
        _toastTimer.Stop();
        _toastOnly = false;
        ClearThumbs();
        Hide();
    }
}
