using SchoolBell.Client.Core;
using SchoolBell.Contracts;

namespace Client.Tests;

public class TranscriptPresentationTests
{
    static HistoryItemPayload Item(string state, bool schoolwide = true, string? text = null) =>
        new("a", 0, "recorded", "Office", schoolwide, state, text, false);

    [Fact]
    public void Transcript_finishing_before_audio_stays_completed_after_end_and_late_preview()
    {
        var display = new TranscriptPresentation("a");
        display.Apply(Item("done", text: "The final transcript."));
        display.MarkEnded();
        display.Apply(new PipeAnnTranscript("a", "live", true, "old partial", 0));
        Assert.Equal("Completed transcript", display.Status);
        Assert.Equal("The final transcript.", display.Text);
    }

    [Fact]
    public void Failed_and_private_transcripts_do_not_show_generating_forever()
    {
        var failed = new TranscriptPresentation("a");
        failed.Apply(Item("failed"));
        failed.MarkEnded();
        Assert.DoesNotContain("generating", failed.Status);
        var privateDisplay = new TranscriptPresentation("a");
        privateDisplay.Apply(new PipeAnnTranscript("a", "starting", false, null, 0));
        privateDisplay.MarkEnded();
        Assert.Equal("Announcement ended", privateDisplay.Status);
        Assert.Contains("only in the office", privateDisplay.Text);
    }

    [Fact]
    public void Explicit_redo_keeps_old_text_while_pending_then_updates_the_same_view()
    {
        var display = new TranscriptPresentation("a");
        display.Apply(Item("done", text: "Old text"));
        display.Apply(Item("pending"));
        Assert.Contains("generating", display.Status);
        Assert.Equal("Old text", display.Text);
        display.Apply(Item("done", text: "New text"));
        Assert.Equal("New text", display.Text);
    }
}
