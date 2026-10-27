using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Altlane;

/// <summary>
/// The settings window. Every change applies at once; there is no OK button. Built in code like the
/// overlay. Admin mode and start-at-login call back into App, which owns the side effects.
/// </summary>
sealed class SettingsWindow : Window
{
    static readonly FontFamily Ui = new("Segoe UI Variable Text, Segoe UI");

    readonly Settings _s;
    readonly RuleStore _rules;
    readonly Action _changed;
    readonly CheckBox _login;
    readonly TextBlock _adminText, _rulesText;
    readonly Button _adminButton;

    public Func<bool>? TurnOnAdmin, TurnOffAdmin;
    public Action? OpenRules, OpenLogs, OpenUsage, QuitApp;

    public SettingsWindow(Settings s, RuleStore rules, string version, Action changed)
    {
        _s = s; _rules = rules; _changed = changed;
        Title = "Altlane settings";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = Ui;
        FontSize = 13.5;
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(Application.GetResourceStream(new Uri("pack://application:,,,/Assets/altlane.ico")).Stream); }
        catch (Exception) { }

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        root.Children.Add(new TextBlock { Text = "Altlane", FontSize = 24, FontWeight = FontWeights.SemiBold });
        root.Children.Add(Note($"Version {version}{(AdminTask.SelfElevated ? ", running as administrator" : "")}. Alt+Tab and Win+Tab, grouped by kind of window."));

        root.Children.Add(Head("Switching"));
        var layoutRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        layoutRow.Children.Add(new TextBlock { Text = "Alt+Tab layout:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        layoutRow.Children.Add(Radio("Grid", Settings.GridLayout));
        layoutRow.Children.Add(Radio("Columns", Settings.ColumnsLayout));
        root.Children.Add(layoutRow);
        root.Children.Add(Note("Grid: the groups wrap into rows, and ← → step through the cards. Columns: one column per group, ↑ ↓ move within it and ← → across. Tab, ` and the number keys work the same in both.", indent: false));
        var winTabRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        winTabRow.Children.Add(new TextBlock { Text = "Win+Tab:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        winTabRow.Children.Add(WinTabRadio("Lanes overview", true));
        winTabRow.Children.Add(WinTabRadio("Windows' Task View", false));
        root.Children.Add(winTabRow);
        root.Children.Add(Note("The lanes overview shows every group in its own column, full screen, and stays open until Enter, a click or Esc. With it on, Shift+Win+Tab opens Windows' Task View.", indent: false));
        root.Children.Add(Check("Alt+` cycles the windows in the current window's group", s.SameGroupKey, v => s.SameGroupKey = v));
        root.Children.Add(Check("Alt+1 to Alt+9 jump to the most recent window in that group", s.DirectJump, v => s.DirectJump = v));
        root.Children.Add(Note("Off unless you want it: some apps use Alt+number for their own shortcuts.", indent: true));
        var delayRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        delayRow.Children.Add(new TextBlock { Text = "Wait before drawing the switcher:", VerticalAlignment = VerticalAlignment.Center });
        var delay = new TextBox { Text = s.ShowDelayMs.ToString(), Width = 56, Margin = new Thickness(8, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
        void ApplyDelay()
        {
            if (int.TryParse(delay.Text, out int ms)) { s.ShowDelayMs = Math.Clamp(ms, 0, 1000); _changed(); }
            delay.Text = s.ShowDelayMs.ToString();
        }
        delay.LostFocus += (_, _) => ApplyDelay();
        delay.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyDelay(); };
        delayRow.Children.Add(delay);
        delayRow.Children.Add(new TextBlock { Text = "ms", VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(delayRow);
        root.Children.Add(Note("A quick Alt+Tab released before this switches back without drawing anything.", indent: false));

        root.Children.Add(Head("Terminals and Claude Code"));
        root.Children.Add(Check("Show each Windows Terminal tab as its own card", s.TerminalTabs, v => s.TerminalTabs = v));
        root.Children.Add(Note("The tab in front shows a live picture. Other tabs show their name, since Windows only draws the tab in front. Switching to one selects that tab.", indent: true));
        root.Children.Add(Check("Mark Claude Code sessions that are working or waiting for you", s.ClaudeStatus, v => s.ClaudeStatus = v));
        root.Children.Add(Note(@"Reads the status files Claude Code keeps in your user folder (.claude\sessions). Nothing is sent anywhere.", indent: true));
        root.Children.Add(Check("List sessions waiting for you first in their group", s.NeedsFirst, v => s.NeedsFirst = v));

        root.Children.Add(Head("Mouse"));
        root.Children.Add(Check("Pointing at a card selects it in Alt+Tab too", s.HoverSelects, v => s.HoverSelects = v));
        root.Children.Add(Note("Only when the mouse moves, so a view that opens under a still pointer keeps its keyboard selection. Win+Tab's overview always works this way.", indent: true));
        root.Children.Add(Check("Show a close button on the selected card and the one under the mouse", s.CloseButton, v => s.CloseButton = v));
        root.Children.Add(Note("Del closes the selected window too. An app that asks whether to save stays open, and its card comes back.", indent: true));
        root.Children.Add(Check("Middle-click closes a window; drag a card onto a group to move it there", s.MouseActions, v => s.MouseActions = v));

        root.Children.Add(Head("Groups and rules"));
        _rulesText = Note("", indent: false);
        root.Children.Add(_rulesText);
        var ruleButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        ruleButtons.Children.Add(Btn("Open rules.json", () => OpenRules?.Invoke()));
        ruleButtons.Children.Add(Btn("Open log folder", () => OpenLogs?.Invoke()));
        ruleButtons.Children.Add(Btn("Usage…", () => OpenUsage?.Invoke()));
        root.Children.Add(ruleButtons);

        root.Children.Add(Head("Starting"));
        _login = Check("Start Altlane when I sign in", Autostart.IsOn, v => Autostart.Set(v));
        root.Children.Add(_login);
        root.Children.Add(new TextBlock { Text = "Admin windows", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
        _adminText = Note("Checking…", indent: false);
        root.Children.Add(_adminText);
        _adminButton = Btn("…", () => { });
        _adminButton.Margin = new Thickness(0, 8, 0, 0);
        _adminButton.HorizontalAlignment = HorizontalAlignment.Left;
        _adminButton.IsEnabled = false;
        root.Children.Add(_adminButton);

        var bottom = new DockPanel { Margin = new Thickness(0, 22, 0, 0), LastChildFill = false };
        var quit = Btn("Quit Altlane", () => QuitApp?.Invoke());
        var close = Btn("Close", Close);
        close.IsDefault = true;
        DockPanel.SetDock(quit, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        bottom.Children.Add(quit);
        bottom.Children.Add(close);
        root.Children.Add(bottom);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        _rules.Changed += ShowRules;
        Closed += (_, _) => _rules.Changed -= ShowRules;
        ShowRules();
        // schtasks takes a moment; draw the window first.
        Loaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, RefreshAdmin);
    }

    void ShowRules()
    {
        if (_rules.Error != null)
            _rulesText.Text = $"rules.json has an error, so the last good rules are in use: {_rules.Error}";
        else if (_rules.Current.Warnings.Count > 0)
            _rulesText.Text = $"{_rules.Current.Groups.Count} groups. Some rules were skipped: " + string.Join(" ", _rules.Current.Warnings);
        else
            _rulesText.Text = $"{_rules.Current.Groups.Count} groups, no problems. Ctrl+number in the switcher moves a window and saves the rule here. Save the file and Altlane reloads it.";
    }

    void RefreshAdmin()
    {
        bool on = AdminTask.Exists();
        bool self = AdminTask.SelfElevated;
        // In admin mode the scheduled task is what starts Altlane; the Run key stays off.
        _quiet = true;
        _login.IsEnabled = !on;
        _login.IsChecked = on || Autostart.IsOn;
        _quiet = false;
        if (on)
        {
            _adminText.Text = self
                ? "On. Altlane starts as administrator when you sign in, so Alt+Tab works over admin windows too."
                : "On, but this copy isn't running as administrator. Quit and start Altlane again, or sign out and in.";
            Rewire("Turn off admin mode", () => TurnOffAdmin);
        }
        else
        {
            _adminText.Text = self
                ? "Off. This copy is running as administrator anyway, so it works over admin windows until you quit it."
                : "Off. While an admin window is in front, Windows' own Alt+Tab appears instead of Altlane's. Admin mode starts Altlane as administrator at sign-in through a scheduled task. Turning it on asks for admin permission once.";
            Rewire("Turn on admin mode…", () => TurnOnAdmin);
        }
    }

    void Rewire(string label, Func<Func<bool>?> action)
    {
        var b = _adminButton;
        b.Content = label;
        b.IsEnabled = true;
        b.Click -= OnAdminClick;
        _adminAction = action;
        b.Click += OnAdminClick;
    }

    Func<Func<bool>?>? _adminAction;

    void OnAdminClick(object sender, RoutedEventArgs e)
    {
        _adminButton.IsEnabled = false;
        _adminAction?.Invoke()?.Invoke();
        RefreshAdmin();
    }

    static TextBlock Head(string text) => new() { Text = text, FontSize = 15.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 6) };

    static TextBlock Note(string text, bool indent = false) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.72, FontSize = 12.5,
        Margin = new Thickness(indent ? 22 : 0, 3, 0, 0),
    };

    bool _quiet;

    CheckBox Check(string text, bool value, Action<bool> set)
    {
        var c = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = value, Margin = new Thickness(0, 6, 0, 0) };
        c.Checked += (_, _) => { if (!_quiet) { set(true); _changed(); } };
        c.Unchecked += (_, _) => { if (!_quiet) { set(false); _changed(); } };
        return c;
    }

    RadioButton Radio(string text, string layout)
    {
        var r = new RadioButton { Content = text, GroupName = "layout", IsChecked = _s.Layout == layout, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        r.Checked += (_, _) => { if (!_quiet) { _s.Layout = layout; _changed(); } };
        return r;
    }

    RadioButton WinTabRadio(string text, bool lanes)
    {
        var r = new RadioButton { Content = text, GroupName = "wintab", IsChecked = _s.WinTabLanes == lanes, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        r.Checked += (_, _) => { if (!_quiet) { _s.WinTabLanes = lanes; _changed(); } };
        return r;
    }

    static Button Btn(string text, Action a)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0), MinWidth = 90 };
        b.Click += (_, _) => a();
        return b;
    }
}
