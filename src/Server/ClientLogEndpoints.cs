using System.Text.Json;
using SchoolBell.Contracts;

namespace SchoolBell.Server;

public static class ClientLogEndpoints
{
    public static void Map(WebApplication app)
    {
        var logs = app.Services.GetRequiredService<ClientLogsService>();
        app.MapPost(ClientLogAuth.Path, async (HttpContext ctx) =>
        {
            // Bound the actual stream, including chunked requests with no Content-Length.
            using var ms = new MemoryStream();
            var buffer = new byte[4096];
            int n;
            while ((n = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
            {
                if (ms.Length + n > ClientLogAuth.MaxBodyBytes) return Err.Detail(413, "log upload too large");
                ms.Write(buffer, 0, n);
            }
            var bytes = ms.ToArray();
            var device = ctx.Request.Headers["X-SchoolBell-Device"].ToString();
            if (!logs.Authenticate(device, ctx.Request.Headers["X-SchoolBell-Time"].ToString(),
                ctx.Request.Headers["X-SchoolBell-Proof"].ToString(), bytes)) return Err.Detail(401, "device authentication required");
            ClientLogChunk? chunk;
            try { chunk = JsonSerializer.Deserialize<ClientLogChunk>(bytes, ControlEnvelope.Json); }
            catch (JsonException) { return Err.Detail(400, "invalid log chunk"); }
            if (chunk is null || chunk.Data is null || chunk.FileId is null || !logs.Ingest(device, chunk))
                return Err.Detail(400, "invalid log chunk");
            return Http.Json(new { ok = true });
        });

        static IResult? CheckAdmin(HttpContext ctx) => Guard.User(ctx) is not CurrentUser u
            ? Err.Detail(401, "authentication required") : u.Role != "admin" ? Err.Detail(403, "admin role required") : null;

        app.MapGet("/api/admin/client-logs/endpoints", (HttpContext ctx) =>
        {
            var error = CheckAdmin(ctx);
            return error ?? Http.Json(new { items = logs.Endpoints() });
        });

        app.MapGet("/api/admin/client-logs", (HttpContext ctx, string? device_id = null, string? q = null, int count = 100, long? before = null) =>
        {
            var error = CheckAdmin(ctx);
            if (error is not null) return error;
            var items = logs.Read(device_id, q, count, before);
            return Http.Json(new { items, next_before = items.Count > 0 ? (long?)items[^1].Id : null, keep_days = ClientLogsService.KeepDays });
        });

        app.MapGet("/api/admin/client-logs.txt", async (HttpContext ctx, string? device_id = null, string? q = null, int days = ClientLogsService.KeepDays) =>
        {
            var error = CheckAdmin(ctx);
            if (error is not null) return error;
            // Stream the complete retained result in pages; downloads have no viewer limit.
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.Headers.ContentDisposition = "attachment; filename=schoolbell-client-logs.txt";
            long? before = null;
            while (!ctx.RequestAborted.IsCancellationRequested)
            {
                var page = logs.Read(device_id, q, 500, before, days);
                if (page.Count == 0) break;
                foreach (var row in page)
                    await ctx.Response.WriteAsync($"\n--- {row.Label} | {row.Source} | file {row.FileId} offset {row.Offset} | received {DateTimeOffset.FromUnixTimeMilliseconds(row.ReceivedAt):O} ---\n{row.Text}", ctx.RequestAborted);
                before = page[^1].Id;
            }
            return Results.Empty;
        });
    }
}
