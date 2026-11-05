namespace SchoolBell.Server;

internal sealed record VerboseRequest(bool On);

/// Admin > Server log: view the recent lines, switch verbose detail, download as .txt.
public static class LogEndpoints
{
    public static void Map(WebApplication app)
    {
        var logs = app.Services.GetRequiredService<LogBuffer>();
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SchoolBell.Server.Admin");

        static (CurrentUser? User, IResult? Error) Admin(HttpContext ctx)
        {
            if (Guard.User(ctx) is not CurrentUser u) return (null, Err.Detail(401, "authentication required"));
            if (u.Role != "admin") return (null, Err.Detail(403, "admin role required"));
            return (u, null);
        }

        app.MapGet("/api/admin/logs", (HttpContext ctx, int lines = 300, string? level = null, string? q = null) =>
        {
            var (u, e) = Admin(ctx);
            if (u is null) return e!;
            return Http.Json(new { verbose = logs.Verbose, lines = logs.Tail(Math.Clamp(lines, 1, 2000), level, q) });
        });

        app.MapPost("/api/admin/logs/verbose", async (HttpContext ctx) =>
        {
            var (u, e) = Admin(ctx);
            if (u is null) return e!;
            var body = await Http.ReadBody<VerboseRequest>(ctx);
            if (body is null) return Err.Detail(400, "{\"on\": true|false} is required");
            logs.SetVerbose(body.On);
            log.LogInformation("verbose logging turned {State} by {User}", body.On ? "on" : "off", u.Username);
            return Http.Json(new { verbose = logs.Verbose });
        });

        app.MapGet("/api/admin/logs.txt", (HttpContext ctx, int days = 7) =>
        {
            var (u, e) = Admin(ctx);
            if (u is null) return e!;
            var header = $"SchoolBell {Util.ServerVersion} server log, downloaded {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} " +
                         $"by {u.Username} (verbose {(logs.Verbose ? "on" : "off")}, last {Math.Clamp(days, 1, LogBuffer.KeepDays)} day(s))" +
                         Environment.NewLine + Environment.NewLine;
            var bytes = System.Text.Encoding.UTF8.GetBytes(header + logs.Export(days));
            return Results.File(bytes, "text/plain; charset=utf-8", $"schoolbell-log-{DateTime.Now:yyyy-MM-dd-HHmm}.txt");
        });
    }
}
