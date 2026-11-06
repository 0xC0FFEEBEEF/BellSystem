using System.Drawing.Drawing2D;
using SchoolBell.Client.Core;
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
    private BellNotificationForm? _bellNotification;
    private AnnouncementNotificationForm? _announcementNotification;
    private readonly Dictionary<string, TranscriptForm> _transcriptForms = [];
    private readonly Dictionary<string, PipeAnnTranscript> _liveTranscripts = [];
    private string? _lastNotifiedBellId;
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
        _pipe.BellPlaying += bell => _ui.Post(_ => ShowBellNotification(bell), null);
        _pipe.AnnouncementStarted += announcement => _ui.Post(_ => ShowAnnouncementNotification(announcement), null);
        _pipe.AnnouncementEnded += announcement => _ui.Post(_ => EndAnnouncementNotification(announcement), null);
        _pipe.TranscriptUpdated += update => _ui.Post(_ => ApplyTranscriptUpdate(update), null);
        _pipe.Start();
        _refresh = new System.Windows.Forms.Timer { Interval = 15_000 };
        _refresh.Tick += (_, _) => Render();
        _refresh.Start();
        Render();
        if (showHistory) ShowHistory();
    }

    private void ShowBellNotification(PipeBellPlay bell)
    {
        // A passing playback failure can be retried briefly with the same occurrence ID. Keep
        // the notification calm and show it once even when audio needs another attempt.
        if (_lastNotifiedBellId == bell.OccurrenceId) return;
        _lastNotifiedBellId = bell.OccurrenceId;

        var name = string.IsNullOrWhiteSpace(bell.BellName) ? "School bell" : bell.BellName.Trim();
        if (name.Length > 180) name = name[..177] + "...";
        _bellNotification?.Close();
        int bottomOffset = _announcementNotification is { Visible: true } ? _announcementNotification.Height + 24 : 14;
        _bellNotification = new BellNotificationForm(name, bottomOffset);
        _bellNotification.FormClosed += (_, _) => _bellNotification = null;
        _bellNotification.Show();
    }

    private void ShowAnnouncementNotification(PipeAnnStart announcement)
    {
        _announcementNotification?.Close();
        _announcementNotification = new AnnouncementNotificationForm(announcement.AnnouncementId,
            announcement.SourceLabel, ViewTranscript);
        _announcementNotification.FormClosed += (_, _) =>
        {
            if (_announcementNotification?.AnnouncementId == announcement.AnnouncementId)
                _announcementNotification = null;
        };
        _announcementNotification.Show();
    }

    private void EndAnnouncementNotification(PipeAnnEnd announcement)
    {
        if (_announcementNotification?.AnnouncementId == announcement.AnnouncementId)
            _announcementNotification.MarkEnded();
        if (_transcriptForms.TryGetValue(announcement.AnnouncementId, out var form))
            form.MarkEnded();
    }

    private void ApplyTranscriptUpdate(PipeAnnTranscript update)
    {
        _liveTranscripts[update.AnnouncementId] = update;
        if (_transcriptForms.TryGetValue(update.AnnouncementId, out var form)) form.Apply(update);
    }

    private void ViewTranscript(string announcementId, string sourceLabel)
    {
        if (!_transcriptForms.TryGetValue(announcementId, out var form) || form.IsDisposed)
        {
            form = new TranscriptForm(announcementId, sourceLabel);
            _transcriptForms[announcementId] = form;
            form.FormClosed += (_, _) => _transcriptForms.Remove(announcementId);
        }
        if (_liveTranscripts.TryGetValue(announcementId, out var live)) form.Apply(live);
        var history = _pipe.History.FirstOrDefault(h => h.AnnouncementId == announcementId);
        if (history is not null) form.Apply(history);
        form.Show();
        form.WindowState = FormWindowState.Normal;
        form.Activate();
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
        else if (svc is null || !svc.Connected) { status = "Offline"; color = "amber"; }
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
        foreach (var item in _pipe.History)
        {
            if (_transcriptForms.TryGetValue(item.AnnouncementId, out var transcript)) transcript.Apply(item);
            if (item.TranscriptStatus == "done" && _announcementNotification?.AnnouncementId == item.AnnouncementId)
                _announcementNotification.MarkTranscriptReady();
            if (item.TranscriptStatus is "done" or "failed") _liveTranscripts.Remove(item.AnnouncementId);
        }
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
            _bellNotification?.Close();
            _bellNotification?.Dispose();
            _announcementNotification?.Close();
            _announcementNotification?.Dispose();
            foreach (var form in _transcriptForms.Values.ToList()) form.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _pipe.Dispose();
            _history.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// A compact, non-activating bell card. It is independent of Windows notification settings,
/// never steals keyboard focus, and stays inside the working area above the taskbar.
internal sealed class BellNotificationForm : Form
{
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private readonly System.Windows.Forms.Timer _dismiss;

    public BellNotificationForm(string bellName, int bottomOffset = 14)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(31, 35, 43);
        ForeColor = Color.White;
        ClientSize = new Size(330, 82);
        Font = new Font("Segoe UI", 9.5f);
        AccessibleName = $"Bell ringing: {bellName}";

        var accent = new Panel
        {
            BackColor = Color.FromArgb(69, 144, 255),
            Dock = DockStyle.Left,
            Width = 5,
        };
        var title = new Label
        {
            AutoSize = false,
            Text = "Bell ringing",
            Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(24, 14),
            Size = new Size(282, 24),
        };
        var detail = new Label
        {
            AutoEllipsis = true,
            Text = bellName,
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = Color.FromArgb(218, 223, 232),
            Location = new Point(24, 40),
            Size = new Size(282, 28),
        };

        Controls.Add(detail);
        Controls.Add(title);
        Controls.Add(accent);
        foreach (Control control in Controls) control.Click += (_, _) => Close();
        Click += (_, _) => Close();

        _dismiss = new System.Windows.Forms.Timer { Interval = 12_000 };
        _dismiss.Tick += (_, _) => Close();
        Shown += (_, _) =>
        {
            var work = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(work.Right - Width - 14, work.Bottom - Height - bottomOffset);
            using var shape = new GraphicsPath();
            const int radius = 16;
            shape.AddArc(0, 0, radius, radius, 180, 90);
            shape.AddArc(Width - radius, 0, radius, radius, 270, 90);
            shape.AddArc(Width - radius, Height - radius, radius, radius, 0, 90);
            shape.AddArc(0, Height - radius, radius, radius, 90, 90);
            shape.CloseFigure();
            Region = new Region(shape);
            _dismiss.Start();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExNoActivate | WsExToolWindow;
            return cp;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dismiss.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// A persistent announcement card. It remains available after the audio ends so the user can
/// open the completed transcript; View and Ignore are the only dismissing actions.
internal sealed class AnnouncementNotificationForm : Form
{
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private readonly Label _title;
    private readonly Action<string, string> _view;
    private readonly string _sourceLabel;

    public string AnnouncementId { get; }

    public AnnouncementNotificationForm(string announcementId, string sourceLabel,
        Action<string, string> view)
    {
        AnnouncementId = announcementId;
        _sourceLabel = string.IsNullOrWhiteSpace(sourceLabel) ? "Office" : sourceLabel.Trim();
        _view = view;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(31, 35, 43);
        ForeColor = Color.White;
        ClientSize = new Size(370, 132);
        Font = new Font("Segoe UI", 9.5f);
        AccessibleName = $"Announcement from {_sourceLabel}";

        var accent = new Panel { BackColor = Color.FromArgb(138, 92, 246), Dock = DockStyle.Left, Width = 5 };
        _title = new Label
        {
            Text = "Announcement in progress",
            Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(22, 13),
            Size = new Size(325, 23),
        };
        var detail = new Label
        {
            AutoEllipsis = true,
            Text = _sourceLabel,
            ForeColor = Color.FromArgb(218, 223, 232),
            Location = new Point(22, 38),
            Size = new Size(325, 24),
        };
        var ignore = CardButton("Ignore", 190, 82, Color.FromArgb(58, 64, 75));
        var open = CardButton("View transcript", 262, 82, Color.FromArgb(69, 108, 232));
        ignore.Size = new Size(64, 32);
        open.Size = new Size(90, 32);
        ignore.Click += (_, _) => Close();
        open.Click += (_, _) => OpenTranscript();
        _title.Click += (_, _) => OpenTranscript();
        detail.Click += (_, _) => OpenTranscript();
        accent.Click += (_, _) => OpenTranscript();
        Click += (_, _) => OpenTranscript();

        Controls.Add(open);
        Controls.Add(ignore);
        Controls.Add(detail);
        Controls.Add(_title);
        Controls.Add(accent);
        Shown += (_, _) => PositionAndRound();
    }

    private static Button CardButton(string text, int x, int y, Color color) => new()
    {
        Text = text,
        Location = new Point(x, y),
        FlatStyle = FlatStyle.Flat,
        BackColor = color,
        ForeColor = Color.White,
        Cursor = Cursors.Hand,
        TabStop = true,
        FlatAppearance = { BorderSize = 0 },
    };

    private void OpenTranscript()
    {
        _view(AnnouncementId, _sourceLabel);
        Close();
    }

    public void MarkEnded()
    {
        _title.Text = "Announcement ended — view transcript";
        AccessibleName = $"Announcement ended from {_sourceLabel}; view transcript";
    }

    public void MarkTranscriptReady()
    {
        _title.Text = "Transcript ready — click to view";
        AccessibleName = $"Transcript ready from {_sourceLabel}; click to view";
    }

    private void PositionAndRound()
    {
        var work = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(work.Right - Width - 14, work.Bottom - Height - 14);
        using var shape = new GraphicsPath();
        const int radius = 16;
        shape.AddArc(0, 0, radius, radius, 180, 90);
        shape.AddArc(Width - radius, 0, radius, radius, 270, 90);
        shape.AddArc(Width - radius, Height - radius, radius, radius, 0, 90);
        shape.AddArc(0, Height - radius, radius, radius, 90, 90);
        shape.CloseFigure();
        Region = new Region(shape);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExNoActivate | WsExToolWindow;
            return cp;
        }
    }
}

/// One window for the lifetime of an announcement. Live partials replace the body in place; the
/// final history item then changes the same window to the completed transcript.
internal sealed class TranscriptForm : Form
{
    private readonly Label _heading;
    private readonly Label _status;
    private readonly TextBox _text;
    private bool _ended;

    public string AnnouncementId { get; }

    public TranscriptForm(string announcementId, string sourceLabel)
    {
        AnnouncementId = announcementId;
        Text = "SchoolBell — announcement transcript";
        Width = 720;
        Height = 470;
        MinimumSize = new Size(480, 300);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10f);
        ShowInTaskbar = true;

        _heading = new Label
        {
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(12, 12, 12, 0),
            Font = new Font("Segoe UI Semibold", 12f, FontStyle.Bold),
            Text = string.IsNullOrWhiteSpace(sourceLabel) ? "Office announcement" : sourceLabel,
        };
        _status = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Padding = new Padding(12, 2, 12, 8),
            ForeColor = Color.FromArgb(74, 85, 104),
            Text = "Live transcription is starting…",
            AccessibleName = "Transcript status",
        };
        _text = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            Font = new Font("Segoe UI", 11f),
            Margin = new Padding(12),
            Text = "Listening for speech…",
            AccessibleName = "Announcement transcript",
        };
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 2, 12, 12) };
        body.Controls.Add(_text);
        Controls.Add(body);
        Controls.Add(_status);
        Controls.Add(_heading);
    }

    public void MarkEnded()
    {
        _ended = true;
        _status.Text = "Announcement ended · preparing final transcript…";
    }

    public void Apply(PipeAnnTranscript update)
    {
        if (update.AnnouncementId != AnnouncementId) return;
        if (!update.Schoolwide)
        {
            _status.Text = _ended ? "Announcement ended" : "Announcement in progress";
            _text.Text = "This was a targeted announcement. Its transcript is available only in the office.";
            return;
        }
        _status.Text = _ended ? "Announcement ended · preparing final transcript…" : "Live transcript · updates automatically";
        if (!string.IsNullOrWhiteSpace(update.Text))
        {
            _text.Text = update.Text;
            _text.SelectionStart = _text.TextLength;
            _text.ScrollToCaret();
        }
    }

    public void Apply(HistoryItemPayload item)
    {
        if (item.AnnouncementId != AnnouncementId) return;
        _ended = true;
        if (!item.Schoolwide)
        {
            _status.Text = "Announcement ended";
            _text.Text = "This was a targeted announcement. Its transcript is available only in the office.";
            return;
        }
        switch (item.TranscriptStatus)
        {
            case "done":
                _status.Text = item.Corrected ? "Completed transcript · corrected by the office" : "Completed transcript";
                _text.Text = item.Text ?? "[No speech detected]";
                break;
            case "failed":
                _status.Text = "Announcement ended";
                _text.Text = "The transcript could not be completed.";
                break;
            default:
                _status.Text = "Announcement ended · preparing final transcript…";
                if (string.IsNullOrWhiteSpace(_text.Text) || _text.Text == "Listening for speech…")
                    _text.Text = "The final transcript will appear here automatically.";
                break;
        }
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
