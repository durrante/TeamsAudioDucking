using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TeamsAudioDucking.Tray;

/// <summary>
/// System tray icon + context menu. Icons are drawn at runtime (no assets):
/// green = enabled and idle, red with a slash = actively muting, grey = disabled.
/// Must be created and updated on the UI thread.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _iconIdle;
    private readonly Icon _iconDucking;
    private readonly Icon _iconDisabled;
    private readonly ToolStripMenuItem _statusCall;
    private readonly ToolStripMenuItem _statusMuted;
    private readonly ToolStripMenuItem _enableItem;
    private bool _suppressEvents;

    public event Action<bool>? EnableToggled;
    public event Action? MuteNowClicked;
    public event Action? RestoreNowClicked;
    public event Action? SettingsClicked;
    public event Action? ExitClicked;

    public TrayIcon()
    {
        _iconIdle = CreateIcon(Color.FromArgb(0x2E, 0xA8, 0x5C), drawSlash: false);
        _iconDucking = CreateIcon(Color.FromArgb(0xD9, 0x3B, 0x30), drawSlash: true);
        _iconDisabled = CreateIcon(Color.FromArgb(0x8A, 0x8A, 0x8A), drawSlash: false);

        _statusCall = new ToolStripMenuItem("In Teams call: no") { Enabled = false };
        _statusMuted = new ToolStripMenuItem("Muted 0 applications") { Enabled = false };
        _enableItem = new ToolStripMenuItem("Enabled") { CheckOnClick = true, Checked = true };
        _enableItem.CheckedChanged += (_, _) =>
        {
            if (!_suppressEvents) EnableToggled?.Invoke(_enableItem.Checked);
        };

        var muteNow = new ToolStripMenuItem("Mute other applications now");
        muteNow.Click += (_, _) => MuteNowClicked?.Invoke();
        var restoreNow = new ToolStripMenuItem("Restore audio now");
        restoreNow.Click += (_, _) => RestoreNowClicked?.Invoke();
        var settings = new ToolStripMenuItem("Settings...");
        settings.Click += (_, _) => SettingsClicked?.Invoke();
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitClicked?.Invoke();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusCall);
        menu.Items.Add(_statusMuted);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_enableItem);
        menu.Items.Add(muteNow);
        menu.Items.Add(restoreNow);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(settings);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        _notifyIcon = new NotifyIcon
        {
            Icon = _iconIdle,
            Text = "Teams Audio Ducking",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => SettingsClicked?.Invoke();
    }

    public void UpdateState(bool enabled, bool inCall, int mutedCount, bool ducking)
    {
        _suppressEvents = true;
        try
        {
            _enableItem.Checked = enabled;
        }
        finally
        {
            _suppressEvents = false;
        }

        _statusCall.Text = inCall ? "In Teams call: yes" : "In Teams call: no";
        _statusMuted.Text = mutedCount == 1 ? "Muted 1 application" : $"Muted {mutedCount} applications";
        _notifyIcon.Icon = !enabled ? _iconDisabled : ducking ? _iconDucking : _iconIdle;
        _notifyIcon.Text = Truncate(!enabled
            ? "Teams Audio Ducking - disabled"
            : ducking
                ? $"Teams Audio Ducking - muting {mutedCount} app(s)"
                : "Teams Audio Ducking - idle");
    }

    private static string Truncate(string text) => text.Length <= 63 ? text : text[..63];

    private static Icon CreateIcon(Color color, bool drawSlash)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, 2, 2, 28, 28);

            // Small white speaker glyph.
            using var white = new SolidBrush(Color.White);
            g.FillRectangle(white, 9, 13, 5, 6);
            g.FillPolygon(white, new[]
            {
                new Point(14, 13), new Point(20, 8), new Point(20, 24), new Point(14, 19),
            });

            if (drawSlash)
            {
                using var pen = new Pen(Color.White, 3.5f);
                g.DrawLine(pen, 7, 25, 25, 7);
            }
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _iconIdle.Dispose();
        _iconDucking.Dispose();
        _iconDisabled.Dispose();
    }
}
