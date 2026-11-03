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

        // Honest S2 stubs — live media relay and announcements are deferred to milestone S2.
        IResult Stub() => Err.Detail(501, "announcements and live media relay land in milestone S2");
        app.MapGet("/api/announcements", (HttpContext ctx) => Stub());
        app.MapPost("/api/announcements", (HttpContext ctx) => Stub());
        app.MapPost("/api/announcements/lease", (HttpContext ctx) => Stub());
        app.Map("/ws/media", async (HttpContext ctx) =>
        {
            ctx.Response.StatusCode = 501;
            await ctx.Response.WriteAsJsonAsync(new
            {
                detail = "media relay lands in milestone S2",
            }, Util.ApiJson);
        });
    }
}
