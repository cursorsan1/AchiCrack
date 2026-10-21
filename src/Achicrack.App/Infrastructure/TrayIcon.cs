using System.Windows;
using Forms = System.Windows.Forms;

namespace Achicrack.App.Infrastructure;

/// <summary>System tray icon with context menu (WinForms NotifyIcon; WPF has no tray API).</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayIcon(Action open, Action testNotification, Action simulateUnlock, Action exit)
    {
        _icon = new Forms.NotifyIcon
        {
            Text = "Achicrack — achievement tracker",
            Icon = LoadIcon(),
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };
        _icon.ContextMenuStrip.Items.Add("Open Achicrack", null, (_, _) => open());
        _icon.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _icon.ContextMenuStrip.Items.Add("Show test notification", null, (_, _) => testNotification());
        _icon.ContextMenuStrip.Items.Add("Simulate achievement unlock", null, (_, _) => simulateUnlock());
        _icon.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _icon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => exit());
        _icon.DoubleClick += (_, _) => open();
    }

    public void SetStatus(string text) =>
        _icon.Text = text.Length > 63 ? text[..63] : text; // NotifyIcon tooltip limit

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/achicrack.ico"));
        if (info?.Stream is { } stream)
        {
            using (stream)
            {
                return new System.Drawing.Icon(stream);
            }
        }

        return System.Drawing.SystemIcons.Application;
    }
}
