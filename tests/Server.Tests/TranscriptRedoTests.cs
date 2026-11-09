using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SchoolBell.Server;

namespace Server.Tests;

public class TranscriptRedoTests
{
    [Fact]
    public async Task Redo_requeues_the_recording_and_preserves_visible_text_until_success()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        await LiveTest.EnrollAsync(app, "RHS-REDO");
        var uploaded = await app.Upload("/api/announcements/recorded", "file", "redo.wav",
            LiveTest.BuildWav(16000, 16000), ("target", "{\"all\":true}"));
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        string id = (await app.Json(uploaded)).Str("announcement_id");
        Assert.True(app.Factory.Services.GetRequiredService<AnnouncementsService>().End(id, "operator", "test"));
        var db = LiveTest.DbOf(app);
        using (var conn = db.Open())
        {
            conn.Run("""
                UPDATE transcripts SET transcript_original = 'old automatic', transcript_current = 'corrected text',
                    status = 'completed', corrected_by = 'admin', corrected_at = 1
                WHERE announcement_id = $a
                """, ("$a", id));
            conn.Run("UPDATE transcript_jobs SET status = 'completed', attempts = 5, last_error = 'old' WHERE announcement_id = $a",
                ("$a", id));
        }

        var response = await app.Post($"/api/history/{id}/retranscribe", new { });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var check = db.Open();
        Assert.Equal("pending", check.Scalar("SELECT status FROM transcripts WHERE announcement_id = $a", ("$a", id)));
        Assert.Equal("corrected text", check.Scalar("SELECT transcript_current FROM transcripts WHERE announcement_id = $a", ("$a", id)));
        Assert.Equal("queued", check.Scalar("SELECT status FROM transcript_jobs WHERE announcement_id = $a", ("$a", id)));
        Assert.Equal(0, Convert.ToInt32(check.Scalar("SELECT attempts FROM transcript_jobs WHERE announcement_id = $a", ("$a", id))));
        Assert.Equal("", check.Scalar("SELECT COALESCE(last_error, '') FROM transcript_jobs WHERE announcement_id = $a", ("$a", id)));
    }

    [Fact]
    public async Task Redo_rejects_missing_recordings_and_duplicate_active_jobs()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        await LiveTest.EnrollAsync(app, "RHS-REDO");
        var uploaded = await app.Upload("/api/announcements/recorded", "file", "redo.wav",
            LiveTest.BuildWav(16000, 16000), ("target", "{\"all\":true}"));
        string id = (await app.Json(uploaded)).Str("announcement_id");
        Assert.True(app.Factory.Services.GetRequiredService<AnnouncementsService>().End(id, "operator", "test"));
        Assert.Equal(HttpStatusCode.Conflict, (await app.Post($"/api/history/{id}/retranscribe", new { })).StatusCode);

        using var conn = LiveTest.DbOf(app).Open();
        conn.Run("UPDATE transcript_jobs SET status = 'completed' WHERE announcement_id = $a", ("$a", id));
        conn.Run("UPDATE announcements SET recording_path = NULL WHERE announcement_id = $a", ("$a", id));
        Assert.Equal(HttpStatusCode.Conflict, (await app.Post($"/api/history/{id}/retranscribe", new { })).StatusCode);
    }

    [Fact]
    public async Task Redo_queues_a_previous_announcement_that_has_no_transcript()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        await LiveTest.EnrollAsync(app, "RHS-NO-TRANSCRIPT");
        var uploaded = await app.Upload("/api/announcements/recorded", "file", "previous.wav",
            LiveTest.BuildWav(16000, 16000), ("target", "{\"all\":true}"));
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        string id = (await app.Json(uploaded)).Str("announcement_id");
        Assert.True(app.Factory.Services.GetRequiredService<AnnouncementsService>().End(id, "operator", "test"));

        var db = LiveTest.DbOf(app);
        using (var conn = db.Open())
        {
            conn.Run("DELETE FROM transcript_jobs WHERE announcement_id = $a", ("$a", id));
            conn.Run("DELETE FROM transcripts WHERE announcement_id = $a", ("$a", id));
        }

        var response = await app.Post($"/api/history/{id}/retranscribe", new { });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var check = db.Open();
        Assert.Equal("pending", check.Scalar("SELECT status FROM transcripts WHERE announcement_id = $a", ("$a", id)));
        Assert.Equal("queued", check.Scalar("SELECT status FROM transcript_jobs WHERE announcement_id = $a", ("$a", id)));
        Assert.Equal(0, Convert.ToInt32(check.Scalar("SELECT attempts FROM transcript_jobs WHERE announcement_id = $a", ("$a", id))));
    }
}
