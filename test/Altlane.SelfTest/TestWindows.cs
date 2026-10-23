using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Altlane;

/// <summary>
/// Six plain windows the self-test switches between. Each one reports what reaches it: activation,
/// any key that should have been swallowed, any menu-bar activation (the Alt-release side effect),
/// and closing.
/// </summary>
static class TestWindows
{
    public const string ReadyEvent = "Altlane.Test.WindowsReady", Prefix = "Altlane test ";
    public static readonly string[] Names = { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot" };
    static readonly Color[] Colors =
    {
        Color.FromArgb(0xC2, 0x63, 0x34), Color.FromArgb(0xA8, 0x4E, 0x2A), Color.FromArgb(0x3C, 0x8D, 0x5A),
        Color.FromArgb(0x8E, 0x45, 0x8A), Color.FromArgb(0xA8, 0x82, 0x1A), Color.FromArgb(0x5E, 0x63, 0x6B),
    };

    public static int Run(Dictionary<string, string> o)
    {
        Log.Init(o.GetValueOrDefault("log"), "testwindows");
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();

        var forms = new List<TestForm>();
        for (int i = 0; i < Names.Length; i++)
        {
            var f = new TestForm(Names[i], Colors[i]) { StartPosition = FormStartPosition.Manual, Location = new Point(80 + i * 60, 80 + i * 45) };
            forms.Add(f);
            f.Show();
        }

        var quit = new System.Windows.Forms.Timer { Interval = (int)(o.Num("minutes", 10) * 60_000) };
        quit.Tick += (_, _) => Application.Exit();
        quit.Start();

        Log.Write("windows_ready", new { pid = Environment.ProcessId, count = forms.Count });
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEvent);
        ready.Set();
        Application.Run();
        Log.Flush();
        return 0;
    }
}

sealed class TestForm : Form
{
    readonly string _name;

    public TestForm(string name, Color color)
    {
        _name = name;
        Text = TestWindows.Prefix + name;
        ClientSize = new Size(420, 280);
        BackColor = color;
        DoubleBuffered = true;
        FormClosed += (_, _) =>
        {
            Log.Write("closed", new { win = _name });
            if (Application.OpenForms.Count == 0) Application.Exit();
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var font = new Font("Segoe UI", 28, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, _name, font, ClientRectangle, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// <summary>Keys the switcher legitimately lets through: modifiers and the mask key.</summary>
    static bool Allowed(int vk) => vk is 0x10 or 0x11 or 0x12 or (>= 0xA0 and <= 0xA5) or 0x5B or 0x5C or N.VK_MASK;

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case N.WM_KEYDOWN or N.WM_SYSKEYDOWN:
                int vk = (int)m.WParam;
                if (!Allowed(vk)) Log.Write("leak_key", new { win = _name, vk, sys = m.Msg == N.WM_SYSKEYDOWN });
                break;
            case N.WM_CHAR or N.WM_SYSCHAR:
                Log.Write("leak_char", new { win = _name, ch = (int)m.WParam, sys = m.Msg == N.WM_SYSCHAR });
                break;
            case N.WM_SYSCOMMAND when ((int)m.WParam & 0xFFF0) == N.SC_KEYMENU:
                Log.Write("keymenu", new { win = _name, lparam = (long)m.LParam });
                break;
            case N.WM_ENTERMENULOOP:
                Log.Write("menuloop", new { win = _name });
                break;
            case N.WM_ACTIVATE:
                Log.Write("activate", new { win = _name, active = ((int)m.WParam & 0xFFFF) != 0 });
                break;
        }
        base.WndProc(ref m);
    }
}
