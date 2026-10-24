using System;
using System.Drawing;
using System.Windows.Forms;

namespace Altlane;

/// <summary>The notification-area icon and its menu. WinForms NotifyIcon, pumped by the WPF dispatcher.</summary>
sealed class Tray : IDisposable
{
    readonly NotifyIcon _icon;
    readonly ToolStripMenuItem _pause;
    readonly Icon _live, _paused;

    public event Action? PauseClicked, SettingsClicked, UsageClicked, RulesClicked, LogsClicked, QuitClicked;

    public Tray(string version, bool elevated)
    {
        _live = Load("altlane.ico");
        _paused = Load("altlane-paused.ico");
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"Altlane {version}{(elevated ? " (admin)" : "")}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        _pause = new ToolStripMenuItem("Pause", null, (_, _) => PauseClicked?.Invoke());
        menu.Items.Add(_pause);
        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => SettingsClicked?.Invoke()));
        menu.Items.Add(new ToolStripMenuItem("Usage…", null, (_, _) => UsageClicked?.Invoke()));
        menu.Items.Add(new ToolStripMenuItem("Open rules", null, (_, _) => RulesClicked?.Invoke()));
        menu.Items.Add(new ToolStripMenuItem("Open log folder", null, (_, _) => LogsClicked?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => QuitClicked?.Invoke()));
        _icon = new NotifyIcon { Icon = _live, Text = "Altlane", ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => SettingsClicked?.Invoke();
    }

    static Icon Load(string name)
    {
        var s = System.Windows.Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{name}"))!.Stream;
        return new Icon(s, SystemInformation.SmallIconSize);
    }

    public void SetPaused(bool paused)
    {
        _pause.Checked = paused;
        _icon.Icon = paused ? _paused : _live;
        _icon.Text = paused ? "Altlane (paused): Windows' own Alt+Tab is back" : "Altlane";
    }

    public void Balloon(string title, string text, bool warning) =>
        _icon.ShowBalloonTip(6000, title, text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
