using System.Windows;
using Forms = System.Windows.Forms;

namespace WinExt4.UI;

/// <summary>Notification-area icon used when WinExt4 runs in the background.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayIcon(Action show, Action exit)
    {
        var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/winext4.ico"))!;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Apri WinExt4", null, (_, _) => show());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Esci", null, (_, _) => exit());
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(res.Stream),
            Text = "WinExt4 — in attesa di dischi ext",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) show();
        };
    }

    public void Notify(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
