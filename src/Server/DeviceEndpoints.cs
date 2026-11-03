namespace SchoolBell.Server;

public static class DeviceEndpoints
{
    public static void Map(WebApplication app)
    {
        var devices = app.Services.GetRequiredService<DevicesService>();
        var receipts = app.Services.GetRequiredService<ReceiptsService>();
        var time = app.Services.GetRequiredService<TimeProvider>();

        app.MapGet("/api/devices", (HttpContext ctx, string? q, int page = 1, int page_size = 50) =>
        {
            if (Guard.User(ctx) is null) return Err.Detail(401, "authentication required");
            return Http.Json(devices.List(q, page, page_size));
        });

        app.MapGet("/api/devices/{deviceId}", (HttpContext ctx, string deviceId) =>
        {
            if (Guard.User(ctx) is null) return Err.Detail(401, "authentication required");
            var row = devices.Get(deviceId);
            return row is null ? Err.Detail(404, "device not found") : Http.Json(row);
        });

        app.MapGet("/api/recent-bells", (HttpContext ctx, int limit = 50) =>
        {
            if (Guard.User(ctx) is null) return Err.Detail(401, "authentication required");
            return Http.Json(receipts.RecentBells(limit, time.GetUtcNow().ToUnixTimeMilliseconds()));
        });

        // Milestone S2 built the real announcement API on its own paths
        // (api/announcements/lease, /live, /live/end, /recorded, /active, api/history) and the real
        // media relay (/ws/mic, /ws/media). These bare paths are not resources; they answer honestly
        // instead of pretending to be one. The POST stub keeps the shape the S1 build published.
        app.MapGet("/api/announcements", (HttpContext ctx) =>
            Err.Detail(404, "no such endpoint; use GET api/history or GET api/announcements/active (milestone S2 live API)"));
        app.MapPost("/api/announcements", (HttpContext ctx) =>
            Err.Detail(501, "POST /api/announcements is not an endpoint (milestone S2 uses api/announcements/live and api/announcements/recorded)"));
    }
}
