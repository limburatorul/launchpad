using System.Drawing;
using System.Windows.Forms;

namespace Launchpad;

/// <summary>Launchpad in the notification area: a dot on the icon when something is ready to update, and its menu.</summary>
public sealed class Tray : IDisposable
{
    private readonly NotifyIcon _icon = new();
    private readonly Hub _hub;
    private readonly Icon _plain, _badged;
    private readonly ToolStripMenuItem _updateAll;

    public Tray(Hub hub, Action show, Action quit)
    {
        _hub = hub;
        using (var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/app.ico")).Stream)
            _plain = new Icon(stream, SystemInformation.SmallIconSize);
        _badged = Badge(_plain);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Launchpad", null, (_, _) => show());
        menu.Items.Add("Check for updates", null, async (_, _) => await ((App)System.Windows.Application.Current).CheckNow());
        _updateAll = new ToolStripMenuItem("Update all", null, async (_, _) => await _hub.UpdateAll());
        menu.Items.Add(_updateAll);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => quit());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => show();
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) show(); };
        hub.Changed += Refresh;
        Refresh();
        _icon.Visible = true;
    }

    private void Refresh()
    {
        int updates = _hub.Updates.Count();
        int here = _hub.Updates.Count(e => e.UpdatesHere);
        _icon.Icon = updates > 0 ? _badged : _plain;
        _icon.Text = updates switch { 0 => "Protagonist Labs Launchpad", 1 => "Launchpad: 1 update ready", _ => $"Launchpad: {updates} updates ready" };
        _updateAll.Visible = here > 0;
        _updateAll.Text = here == 1 ? "Update 1 app" : $"Update {here} apps";
    }

    /// <summary>The icon with the pink dot the window uses for "has an update", bottom right.</summary>
    private static Icon Badge(Icon plain)
    {
        using var bitmap = plain.ToBitmap();
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float d = bitmap.Width * 0.46f;
            using var ring = new SolidBrush(Color.FromArgb(18, 19, 24));
            g.FillEllipse(ring, bitmap.Width - d - 1, bitmap.Height - d - 1, d + 1, d + 1);
            using var dot = new SolidBrush(Color.FromArgb(255, 43, 214));
            g.FillEllipse(dot, bitmap.Width - d + 1, bitmap.Height - d + 1, d - 3, d - 3);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
