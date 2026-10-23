using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Altlane.Spike;

/// <summary>
/// The switcher surface: a grid of tiles with live DWM thumbnails. Built in code, created once and
/// reused. Never takes activation (WS_EX_NOACTIVATE); the keyboard hook feeds it keys instead.
/// AllowsTransparency stays false because DWM thumbnails don't draw into layered windows.
/// </summary>
sealed class Overlay : Window
{
    static readonly Brush Bg = Frozen(0x1C, 0x1C, 0x21), Tile = Frozen(0x2A, 0x2A, 0x31), TileSel = Frozen(0x3A, 0x3F, 0x55);
    static readonly Brush Accent = Frozen(0x7A, 0xA2, 0xF7), Text = Frozen(0xE8, 0xE8, 0xEC), Dim = Frozen(0x9A, 0x9A, 0xA6);

    readonly Canvas _canvas = new();
    readonly List<Border> _tiles = new();
    readonly List<(IntPtr thumb, Rect slot)> _thumbs = new();
    IntPtr _hwnd;

    public event Action<int>? TileClicked;
    public event Action? BackdropClicked;
    /// <summary>Fires once per show with (ms from show request to first composed frame).</summary>
    public event Action<double>? Composed;

    public IntPtr Hwnd => _hwnd;

    public Overlay()
    {
        Title = "Altlane overlay";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = false;
        Background = Bg;
        Content = _canvas;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = Win.ExStyle(_hwnd) | N.WS_EX_TOOLWINDOW | N.WS_EX_NOACTIVATE;
            N.SetWindowLongPtr(_hwnd, N.GWL_EXSTYLE, (IntPtr)ex);
            int round = 2;
            N.DwmSetWindowAttribute(_hwnd, N.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
        };
        MouseLeftButtonUp += (_, e) => { if (!e.Handled) BackdropClicked?.Invoke(); };
    }

    static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

    /// <summary>Create the HWND and run one show/hide off-screen so the first real show is warm.</summary>
    public void Prewarm()
    {
        new WindowInteropHelper(this).EnsureHandle();
        Left = -20000; Top = -20000; Width = 200; Height = 120;
        Show(); Hide();
    }

    /// <param name="full">Win+Tab overview: cover the primary screen so a click outside the tiles cancels.</param>
    public void ShowItems(IReadOnlyList<WinInfo> items, int sel, string header, bool full)
    {
        long tReq = Stopwatch.GetTimestamp();
        ClearThumbs();
        _canvas.Children.Clear();
        _tiles.Clear();

        var work = SystemParameters.WorkArea;
        int n = Math.Max(items.Count, 1);
        double tileW = full ? 320 : 240, thumbH = full ? 200 : 150, titleH = 26, gap = 14, pad = 22, headH = 34;
        int cols = Math.Min(n, full ? 5 : 6);
        int rows = (n + cols - 1) / cols;
        double gridW = cols * tileW + (cols - 1) * gap, gridH = rows * (thumbH + titleH) + (rows - 1) * gap;
        double maxW = work.Width * 0.92 - 2 * pad, maxH = work.Height * 0.88 - 2 * pad - headH;
        double scale = Math.Min(1, Math.Min(maxW / gridW, maxH / gridH));
        tileW *= scale; thumbH *= scale; gap *= scale;
        gridW = cols * tileW + (cols - 1) * gap; gridH = rows * (thumbH + titleH) + (rows - 1) * gap;

        double winW, winH, ox, oy;
        if (full)
        {
            winW = SystemParameters.PrimaryScreenWidth; winH = SystemParameters.PrimaryScreenHeight;
            ox = (winW - gridW) / 2; oy = (winH - gridH - headH) / 2 + headH;
            Left = 0; Top = 0;
        }
        else
        {
            winW = gridW + 2 * pad; winH = gridH + 2 * pad + headH;
            ox = pad; oy = pad + headH;
            Left = work.Left + (work.Width - winW) / 2; Top = work.Top + (work.Height - winH) / 2;
        }
        Width = winW; Height = winH;

        var head = new TextBlock { Text = header, Foreground = Dim, FontSize = 14, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI") };
        Canvas.SetLeft(head, ox); Canvas.SetTop(head, oy - headH + 4);
        _canvas.Children.Add(head);

        var slots = new List<Rect>();
        for (int i = 0; i < items.Count; i++)
        {
            int c = i % cols, r = i / cols;
            double x = ox + c * (tileW + gap), y = oy + r * (thumbH + titleH + gap);
            var w = items[i];
            var label = new TextBlock
            {
                Text = (w.Minimized ? "▾ " : "") + w.Title,
                Foreground = Text, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            };
            // The title row is the only child; LastChildFill would stretch it under the thumbnail.
            var dock = new DockPanel { LastChildFill = false };
            var titleRow = new Border { Height = titleH, Child = label };
            DockPanel.SetDock(titleRow, Dock.Bottom);
            dock.Children.Add(titleRow);
            var tile = new Border
            {
                Width = tileW, Height = thumbH + titleH, CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(2), Child = dock, Tag = i, Cursor = Cursors.Hand,
            };
            tile.MouseLeftButtonUp += (s, e) => { e.Handled = true; TileClicked?.Invoke((int)((Border)s).Tag); };
            Canvas.SetLeft(tile, x); Canvas.SetTop(tile, y);
            _canvas.Children.Add(tile);
            _tiles.Add(tile);
            slots.Add(new Rect(x + 6, y + 6, tileW - 12, thumbH - 6));
        }
        SetSelection(sel);

        if (!IsVisible) Show();
        UpdateLayout();
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        for (int i = 0; i < items.Count; i++) AddThumb(items[i].Hwnd, slots[i], dpi);

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
        _thumbs.Add((thumb, slot));
    }

    public void SetSelection(int sel)
    {
        for (int i = 0; i < _tiles.Count; i++)
        {
            _tiles[i].Background = i == sel ? TileSel : Tile;
            _tiles[i].BorderBrush = i == sel ? Accent : Tile;
        }
    }

    void ClearThumbs()
    {
        foreach (var (t, _) in _thumbs) N.DwmUnregisterThumbnail(t);
        _thumbs.Clear();
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
        ClearThumbs();
        Hide();
    }
}
