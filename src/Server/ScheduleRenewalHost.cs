namespace SchoolBell.Server;

/// Startup recovery plus daily horizon extension; failures retry in a minute.
public sealed class ScheduleRenewalHost(PublishService publish, Db db, ILogger<ScheduleRenewalHost> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        string? lastError = null;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                var result = publish.RenewIfNeeded();
                var error = result is { Status: not 200 } ? result.Detail : null;
                if (error is not null && error != lastError) log.LogError("schedule renewal: {Reason}", error);
                SetError(error);
                lastError = error;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (e.Message != lastError) log.LogError(e, "automatic schedule renewal failed; retrying in one minute");
                SetError(e.Message);
                lastError = e.Message;
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private void SetError(string? error)
    {
        try
        {
            if (db.GetSetting("schedule_renewal_error") != (error ?? "")) db.SetSetting("schedule_renewal_error", error ?? "");
        }
        catch (Exception e) { log.LogDebug(e, "could not persist schedule renewal status; the next check will retry"); }
    }
}
