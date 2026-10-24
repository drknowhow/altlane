using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Altlane;

/// <summary>
/// Usage, from the tray menu: how often Altlane switched, how each window was picked, which groups it
/// went to, and what went unused. Counted from the logs on this PC when the window opens.
/// </summary>
sealed class UsageWindow : Window
{
    static readonly FontFamily Ui = new("Segoe UI Variable Text, Segoe UI");

    static readonly (string key, string label, string color)[] PickKinds =
    {
        ("quick", "Quick tap", "#8a8f98"), ("keys", "Tab or arrows", "#3a7bd5"), ("mouse", "Mouse", "#d97745"),
        ("number", "Number key", "#4c9a5f"), ("search", "Search", "#b0569f"), ("unrecorded", "Keys, before 0.4.0", "#c5cad0"),
    };

    public UsageWindow(Usage u, Settings s, IReadOnlyList<Group> groups)
    {
        Title = "Altlane usage";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = Ui;
        FontSize = 13.5;
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(Application.GetResourceStream(new Uri("pack://application:,,,/Assets/altlane.ico")).Stream); }
        catch (Exception) { }

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        root.Children.Add(new TextBlock { Text = "Usage", FontSize = 24, FontWeight = FontWeights.SemiBold });
        root.Children.Add(Note($"Counted from Altlane's own logs on this PC. Nothing is sent anywhere. Last {u.Days} days."));

        var stats = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        stats.Children.Add(Stat(u.Switches.ToString(), "switches"));
        stats.Children.Add(Stat(u.Landed.ToString(), "landed"));
        stats.Children.Add(Stat(u.MedianMs is double m ? (m / 1000).ToString("0.0", CultureInfo.CurrentCulture) + " s" : "–", "median, Alt+Tab to switch"));
        stats.Children.Add(Stat(u.Quick.ToString(), "quick taps"));
        root.Children.Add(stats);

        if (u.Switches == 0)
        {
            root.Children.Add(Note("No switches logged in these days yet.", top: 14));
        }
        else
        {
            root.Children.Add(Head("How you picked the window"));
            var parts = PickKinds.Select(k => (k.label, n: u.Picks.GetValueOrDefault(k.key), k.color)).Where(p => p.n > 0).ToList();
            root.Children.Add(StackBar(parts));
            var legend = new WrapPanel();
            foreach (var (label, n, color) in parts)
                legend.Children.Add(Legend($"{label} {Pct(n, u.Switches)}", color));
            root.Children.Add(legend);
            if (u.Picks.ContainsKey("unrecorded"))
                root.Children.Add(Note("Before 0.4.0 the log said only whether Alt was released or a card was clicked, so those key presses can't be told apart.", top: 4));

            root.Children.Add(Head("Where you switched to"));
            var rows = u.Sections.OrderByDescending(kv => kv.Value).ToList();
            int max = rows.Max(kv => kv.Value);
            foreach (var (key, n) in rows)
            {
                var g = groups.FirstOrDefault(x => x.Key == key);
                string name = key == Sections.RecentKey ? "Recent" : g?.Name ?? key;
                root.Children.Add(Bar(name, n, max, g?.Color ?? "#8a8f98"));
            }
        }

        var unused = u.Unused(s, groups);
        if (unused.Count > 0)
        {
            root.Children.Add(Head($"Not used in these {u.Days} days"));
            var chips = new WrapPanel();
            foreach (var name in unused) chips.Children.Add(Chip(name));
            root.Children.Add(chips);
        }

        var close = new Button { Content = "Close", Padding = new Thickness(12, 4, 12, 4), MinWidth = 90, IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        close.Click += (_, _) => Close();
        root.Children.Add(close);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        MaxHeight = SystemParameters.WorkArea.Height - 40;
    }

    static string Pct(int n, int of) => $"{Math.Round(100.0 * n / of)}%";

    static Brush Hex(string hex)
    {
        try { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
        catch (FormatException) { return Brushes.Gray; }
    }

    static TextBlock Head(string text) => new() { Text = text, FontSize = 15.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 6) };

    static TextBlock Note(string text, double top = 3) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.72, FontSize = 12.5, Margin = new Thickness(0, top, 0, 0) };

    static UIElement Stat(string value, string label)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 28, 6) };
        p.Children.Add(new TextBlock { Text = value, FontSize = 24, FontWeight = FontWeights.SemiBold });
        p.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.72 });
        return p;
    }

    /// <summary>One bar split in proportion, in the legend's colours.</summary>
    static UIElement StackBar(List<(string label, int n, string color)> parts)
    {
        var g = new Grid { Height = 16, Margin = new Thickness(0, 2, 0, 8) };
        foreach (var (_, n, color) in parts)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(n, GridUnitType.Star) });
            var cell = new Border { Background = Hex(color) };
            Grid.SetColumn(cell, g.ColumnDefinitions.Count - 1);
            g.Children.Add(cell);
        }
        return new Border { Child = g }.Rounded(4);
    }

    static UIElement Legend(string text, string color)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 4) };
        p.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = Hex(color), Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        p.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Opacity = 0.8 });
        return p;
    }

    static UIElement Bar(string name, int n, int max, string color)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        g.Children.Add(new TextBlock { Text = name, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var track = new Grid { Height = 10, VerticalAlignment = VerticalAlignment.Center };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(n, GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, max - n), GridUnitType.Star) });
        track.Children.Add(new Border { Background = Hex(color), CornerRadius = new CornerRadius(3) });
        Grid.SetColumn(track, 1);
        g.Children.Add(track);
        var v = new TextBlock { Text = n.ToString(), FontSize = 12.5, Opacity = 0.72, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(v, 2);
        g.Children.Add(v);
        return g;
    }

    static UIElement Chip(string text) => new Border
    {
        BorderBrush = Hex("#d5d9dd"), BorderThickness = new Thickness(1), Background = Hex("#eef0f2"), CornerRadius = new CornerRadius(4),
        Padding = new Thickness(7, 2, 7, 3), Margin = new Thickness(0, 0, 6, 6),
        Child = new TextBlock { Text = text, FontSize = 12.5 },
    };
}

static class BorderExtensions
{
    /// <summary>Clip a border's content to its rounded corners, which WPF doesn't do by itself.</summary>
    public static Border Rounded(this Border b, double radius)
    {
        b.SizeChanged += (_, e) => b.Clip = new RectangleGeometry(new Rect(e.NewSize), radius, radius);
        return b;
    }
}
