using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SchoolBell.Server;

namespace Server.Tests;

public class TranscriptionPipelineTests
{
    sealed class Engine : ITranscriptionEngine
    {
        public string Name => "test-speech";
        public bool Available => true;
        public string? Text = "The school announcement is ready.";
        public Exception? Failure;
        public Task<string?> TranscribeAsync(ReadOnlyMemory<byte> wav, CancellationToken ct) =>
            Failure is null ? Task.FromResult(Text) : Task.FromException<string?>(Failure);
    }

    static async Task<string> Upload(TestApp app)
    {
        await app.LoginAsAdmin();
        await LiveTest.EnrollAsync(app, "TRANSCRIPT-ROOM");
        var result = await app.Upload("/api/announcements/recorded", "file", "speech.wav",
            LiveTest.BuildWav(16000, 32000, value: 1000), ("target", "{\"all\":true}"));
        Assert.Equal(HttpStatusCode.Created, result.StatusCode);
        return (await app.Json(result)).Str("announcement_id");
    }

    static TranscriptionWorkerHost Worker(TestApp app, Engine engine)
    {
        var sp = app.Factory.Services;
        var db = sp.GetRequiredService<Db>();
        db.SetSetting("transcription_enabled", "true");
        return new(db, new LiveSettings(db), sp.GetRequiredService<AnnouncementsService>(), engine,
            TimeProvider.System, sp.GetRequiredService<ILogger<TranscriptionWorkerHost>>());
    }

    [Fact]
    public async Task Recorded_speech_transcribes_while_offer_is_still_active()
    {
        using var app = new TestApp();
        var id = await Upload(app);
        var announcements = app.Factory.Services.GetRequiredService<AnnouncementsService>();
        Assert.Equal("active", announcements.Read(id)!.Status);
        Assert.Equal("pending", announcements.Transcript(id)?.Status);
        Assert.True(await Worker(app, new()).RunOneAsync(default));
        Assert.Equal("completed", announcements.Transcript(id)?.Status);
        Assert.Equal("The school announcement is ready.", announcements.Transcript(id)?.Current);
        Assert.Equal("active", announcements.Read(id)!.Status); // offline delivery window remains open
    }

    [Fact]
    public async Task Upgrade_repairs_missing_recorded_transcripts_and_reclaims_interrupted_jobs()
    {
        using var app = new TestApp();
        var id = await Upload(app);
        var db = LiveTest.DbOf(app);
        using (var c = db.Open())
        {
            c.Run("DELETE FROM transcript_jobs WHERE announcement_id = $a", ("$a", id));
            c.Run("DELETE FROM transcripts WHERE announcement_id = $a", ("$a", id));
        }
        var worker = Worker(app, new());
        worker.RecoverInterruptedJobs();
        Assert.True(await worker.RunOneAsync(default));
        using (var c = db.Open())
        {
            c.Run("UPDATE transcript_jobs SET status = 'running', attempts = max_attempts WHERE announcement_id = $a", ("$a", id));
            c.Run("UPDATE transcripts SET status = 'pending' WHERE announcement_id = $a", ("$a", id));
        }
        worker.RecoverInterruptedJobs();
        Assert.True(await worker.RunOneAsync(default));
        Assert.Equal("completed", app.Factory.Services.GetRequiredService<AnnouncementsService>().Transcript(id)?.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_output_and_request_timeout_retry_without_completing_or_wedging(bool timeout)
    {
        using var app = new TestApp();
        var id = await Upload(app);
        var engine = new Engine { Text = "", Failure = timeout ? new TaskCanceledException("inference timed out") : null };
        var worker = Worker(app, engine);
        Assert.True(await worker.RunOneAsync(default));
        using var c = LiveTest.DbOf(app).Open();
        Assert.Equal("queued", c.Scalar("SELECT status FROM transcript_jobs WHERE announcement_id = $a", ("$a", id)));
        Assert.Equal("pending", c.Scalar("SELECT status FROM transcripts WHERE announcement_id = $a", ("$a", id)));
        Assert.Null(c.Scalar("SELECT transcript_current FROM transcripts WHERE announcement_id = $a", ("$a", id)) as string);
    }

    [Fact]
    public async Task Retried_recorded_offer_can_be_transcribed_before_its_delivery_window_closes()
    {
        using var app = new TestApp();
        var id = await Upload(app);
        using (var c = LiveTest.DbOf(app).Open())
        {
            c.Run("UPDATE transcript_jobs SET status = 'failed' WHERE announcement_id = $a", ("$a", id));
            c.Run("UPDATE transcripts SET status = 'failed' WHERE announcement_id = $a", ("$a", id));
        }
        Assert.Equal(HttpStatusCode.Accepted, (await app.Post($"/api/history/{id}/retranscribe", new { })).StatusCode);
        Assert.True(await Worker(app, new()).RunOneAsync(default));
        var detail = await app.Json(await app.Get($"/api/announcements/{id}"));
        Assert.Equal("completed", detail.GetProperty("transcript").GetProperty("status").GetString());
        Assert.Equal("completed", detail.GetProperty("transcription_job").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Worker_shutdown_releases_its_claim_and_does_not_consume_an_attempt()
    {
        using var app = new TestApp();
        var id = await Upload(app);
        using var stop = new CancellationTokenSource();
        var engine = new CancelEngine(stop);
        var sp = app.Factory.Services;
        var db = LiveTest.DbOf(app);
        db.SetSetting("transcription_enabled", "true");
        var worker = new TranscriptionWorkerHost(db, new LiveSettings(db), sp.GetRequiredService<AnnouncementsService>(), engine,
            TimeProvider.System, sp.GetRequiredService<ILogger<TranscriptionWorkerHost>>());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunOneAsync(stop.Token));
        using var c = db.Open();
        Assert.Equal("queued", c.Scalar("SELECT status FROM transcript_jobs WHERE announcement_id = $a", ("$a", id)));
        Assert.Equal(0, Convert.ToInt32(c.Scalar("SELECT attempts FROM transcript_jobs WHERE announcement_id = $a", ("$a", id))));
    }

    sealed class CancelEngine(CancellationTokenSource stop) : ITranscriptionEngine
    {
        public string Name => "cancel-test";
        public bool Available => true;
        public Task<string?> TranscribeAsync(ReadOnlyMemory<byte> wav, CancellationToken ct)
        {
            stop.Cancel();
            return Task.FromCanceled<string?>(ct);
        }
    }
}
