using SchoolBell.Contracts;

namespace SchoolBell.Client.Core;

/// Shared transcript lifecycle, independent of Windows: an audio-end event cannot turn a
/// completed transcript back into an endless "generating" message.
public sealed class TranscriptPresentation(string announcementId)
{
    private bool _ended, _terminal, _hasText;
    private bool _schoolwide = true;
    private const string OfficeOnly = "This was a targeted announcement. Its transcript is available only in the office.";
    public string Status { get; private set; } = "Generating transcription…";
    public string Text { get; private set; } = "Generating transcription…";

    public void MarkEnded()
    {
        _ended = true;
        if (!_schoolwide) { Status = "Announcement ended"; Text = OfficeOnly; return; }
        if (_terminal) return;
        Status = "Announcement ended · generating transcription…";
        if (!_hasText) Text = "Generating transcription…";
    }

    public void Apply(PipeAnnTranscript update)
    {
        if (update.AnnouncementId != announcementId || _terminal) return;
        _schoolwide = update.Schoolwide;
        if (!_schoolwide)
        {
            Status = _ended ? "Announcement ended" : "Announcement in progress";
            Text = OfficeOnly;
            return;
        }
        Status = _ended ? "Announcement ended · generating transcription…" : "Live transcript · updates automatically";
        if (!string.IsNullOrWhiteSpace(update.Text)) { _hasText = true; Text = update.Text; }
    }

    public void Apply(HistoryItemPayload item)
    {
        if (item.AnnouncementId != announcementId) return;
        _ended = true;
        _schoolwide = item.Schoolwide;
        _terminal = !_schoolwide || item.TranscriptStatus is "done" or "failed";
        if (!_schoolwide) { Status = "Announcement ended"; Text = OfficeOnly; return; }
        switch (item.TranscriptStatus)
        {
            case "done":
                _hasText = true;
                Status = item.Corrected ? "Completed transcript · corrected by the office" : "Completed transcript";
                Text = item.Text ?? "[No speech detected]";
                break;
            case "failed":
                _hasText = false;
                Status = "Announcement ended";
                Text = "The transcript could not be completed.";
                break;
            default:
                Status = "Announcement ended · generating transcription…";
                if (!_hasText) Text = "Generating transcription…";
                break;
        }
    }
}
