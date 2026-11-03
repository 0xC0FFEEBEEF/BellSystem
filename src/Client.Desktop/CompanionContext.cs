using System.Drawing.Drawing2D;
using SchoolBell.Contracts;

namespace SchoolBell.Client.Desktop;

/// Notification-area application: status icon + menu + history window. Closing the window hides
/// it; "Exit" is not offered (the logon task restarts the companion and the service would fall
/// back to its own renderer anyway), so a student cannot silence the room from the tray.
internal sealed class CompanionContext : ApplicationContext
{
    private readonly CompanionLog _log = new();
    private readonly ServicePipeClient _pipe;
    private readonly NotifyIcon _tray;
    private readonly HistoryForm _history;
    private readonly ToolStripMenuItem _statusItem, _nextItem, _audioItem, _keepAwakeItem;
    private readonly SynchronizationContext _ui;
    private readonly System.Windows.Forms.Timer _refresh;
    private readonly Dictionary<string, Icon> _icons = [];
    public int ExitCode { get; private set; }

    public CompanionContext(bool showHistory)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _pipe = new ServicePipeClient(_log);
        _history = new HistoryForm(_pipe);

        _statusItem = new ToolStripMenuItem("SchoolBell — starting…") { Enabled = false };
        _nextItem = new ToolStripMenuItem("Next bell: —") { Enabled = false };
        _audioItem = new ToolStripMenuItem("Speaker: —") { Enabled = false };
        _keepAwakeItem = new ToolStripMenuItem("Keep awake: off") { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(_nextItem);
        menu.Items.Add(_audioItem);
        menu.Items.Add(_keepAwakeItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Announcement history…", null, (_, _) => ShowHistory()));

        _tray = new NotifyIcon
        {
            Icon = IconFor("gray"),
            Text = "SchoolBell",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowHistory();

        _pipe.Changed += () => _ui.Post(_ => Render(), null);
        _pipe.Start();
        _refresh = new System.Windows.Forms.Timer { Interval = 15_000 };
        _refresh.Tick += (_, _) => Render();
        _refresh.Start();
        Render();
        if (showHistory) ShowHistory();
    }

    private void ShowHistory()
    {
        _pipe.RequestHistory();
        _history.Show();
        _history.WindowState = FormWindowState.Normal;
        _history.Activate();
    }

    private void Render()
    {
        var svc = _pipe.ServiceState;
        string status, color;
        var audio = _pipe.Audio;
        if (!_pipe.PipeConnected) { status = "SchoolBell service not running"; color = "red"; }
        else if (svc is null || !svc.Connected) { status = "Offline — cached bells still ring"; color = "amber"; }
        else if (_pipe.Slot == "renderer" && audio is { Renderer: not "ready" }) { status = $"Connected — speaker problem ({audio.Reason})"; color = "amber"; }
        else if (_pipe.Slot == "renderer" && audio is { MasterMuted: true } or { SessionMuted: true }) { status = "Connected — muted (announcements unmute)"; color = "green"; }
        else { status = "Connected"; color = "green"; }
        _statusItem.Text = "SchoolBell — " + status;

        var next = _pipe.Manifest?.Next;
        _nextItem.Text = _pipe.Manifest is { Stale: true }
            ? "Bell schedule expired — waiting for the office"
            : next is null ? "Next bell: none scheduled"
            : $"Next bell: {next.BellName} at {DateTimeOffset.FromUnixTimeMilliseconds(next.StartsAt).ToLocalTime():ddd h:mm tt}";

        _audioItem.Text = _pipe.Slot != "renderer"
            ? (svc?.RendererOwner == "service" ? "Audio: played by the SchoolBell service" : "Audio: played in another session")
            : audio is null ? "Speaker: —"
            : $"Speaker: {audio.OutputDevice ?? "none"} — {(audio.MasterMuted ? "muted" : audio.MasterVolume + "%")}";

        var ka = _pipe.KeepAwake;
        _keepAwakeItem.Text = ka is { Desired: true }
            ? $"Kept awake by the office until {DateTimeOffset.FromUnixTimeMilliseconds(ka.ExpiresAt).ToLocalTime():h:mm tt}"
            : "Keep awake: off";

        var tip = $"SchoolBell — {status}";
        _tray.Text = tip.Length > 120 ? tip[..120] : tip;
        _tray.Icon = IconFor(color);
        _history.UpdateFrom(_pipe, status);
    }

    private Icon IconFor(string color)
    {
        if (_icons.TryGetValue(color, out var cached)) return cached;
        var c = color switch { "green" => Color.FromArgb(46, 160, 67), "amber" => Color.FromArgb(219, 154, 4), "red" => Color.FromArgb(207, 34, 46), _ => Color.Gray };
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bell = new GraphicsPath();
            bell.AddArc(7, 3, 18, 18, 180, 180);
            bell.AddLine(25, 12, 27, 24);
            bell.AddLine(27, 24, 5, 24);
            bell.AddLine(5, 24, 7, 12);
            bell.CloseFigure();
            using var body = new SolidBrush(Color.FromArgb(60, 64, 72));
            g.FillPath(body, bell);
            g.FillEllipse(body, 13, 23, 6, 6);
            using var dot = new SolidBrush(c);
            g.FillEllipse(dot, 19, 17, 12, 12);
            using var ring = new Pen(Color.White, 1.5f);
            g.DrawEllipse(ring, 19, 17, 12, 12);
        }
        var icon = Icon.FromHandle(bmp.GetHicon());
        _icons[color] = icon;
        return icon;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refresh.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _pipe.Dispose();
            _history.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// History window: previous announcements this computer received, with transcripts for
/// schoolwide announcements. Targeted announcements show no text (kept in the office).
internal sealed class HistoryForm : Form
{
    private readonly ListView _list;
    private readonly Label _state;
    private readonly TextBox _detail;

    public HistoryForm(ServicePipeClient pipe)
    {
        Text = "SchoolBell — announcement history";
        Width = 760; Height = 520;
        MinimumSize = new Size(480, 320);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10f);
        ShowInTaskbar = true;

        _state = new Label { Dock = DockStyle.Top, Height = 32, Padding = new Padding(8, 8, 8, 0), AccessibleName = "Connection state" };
        _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
            AccessibleName = "Announcements", HideSelection = false,
        };
        _list.Columns.Add("When", 150);
        _list.Columns.Add("From", 200);
        _list.Columns.Add("Transcript", 360);
        _detail = new TextBox
        {
            Dock = DockStyle.Bottom, Height = 110, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            AccessibleName = "Selected announcement text",
        };
        _list.SelectedIndexChanged += (_, _) =>
            _detail.Text = _list.SelectedItems.Count == 1 ? (string)_list.SelectedItems[0].Tag! : "";
        Controls.Add(_list);
        Controls.Add(_detail);
        Controls.Add(_state);
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
    }

    public void UpdateFrom(ServicePipeClient pipe, string status)
    {
        _state.Text = $"Status: {status}" + (pipe.LastEvent is { } ev ? $"   ·   Last: {ev}" : "");
        var items = pipe.History;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var h in items)
        {
            var when = DateTimeOffset.FromUnixTimeMilliseconds(h.At).ToLocalTime().ToString("ddd MMM d, h:mm tt");
            string text = !h.Schoolwide ? "(targeted announcement — transcript kept in the office)"
                : h.TranscriptStatus switch
                {
                    "done" => (h.Corrected ? "[corrected] " : "") + (h.Text ?? ""),
                    "failed" => "(transcript unavailable)",
                    _ => "(transcript pending)",
                };
            var item = new ListViewItem([when, h.SourceLabel, text]) { Tag = $"{when} — {h.SourceLabel}\r\n\r\n{text}" };
            _list.Items.Add(item);
        }
        if (items.Count == 0) _list.Items.Add(new ListViewItem(["—", "No announcements yet", ""]) { Tag = "" });
        _list.EndUpdate();
    }
}
