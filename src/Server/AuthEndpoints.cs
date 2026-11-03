using System.Text.Json;
using Microsoft.AspNetCore.Http.Extensions;

namespace SchoolBell.Server;

internal sealed record SetupRequest(string? Code, string? Username, string? Password);
internal sealed record LoginRequest(string? Username, string? Password);
internal sealed record EnrollRequest(string? Token, string? DeviceName);

internal static class Http
{
    public static async Task<T?> ReadBody<T>(HttpContext ctx)
    {
        try { return await JsonSerializer.DeserializeAsync<T>(ctx.Request.Body, Util.ApiRead); }
        catch (JsonException) { return default; }
    }

    public static IResult Json(object value) => Results.Json(value, Util.ApiJson);
    public static IResult Json(object value, int status) => Results.Json(value, Util.ApiJson, statusCode: status);
}

public static class AuthEndpoints
{
    public static void Map(WebApplication app)
    {
        var auth = app.Services.GetRequiredService<AuthService>();

        app.MapGet("/api/auth/csrf", (HttpContext ctx) =>
            Http.Json(new { csrf = ctx.Items["csrf"] as string ?? "" }));

        app.MapPost("/api/auth/setup", async (HttpContext ctx) =>
        {
            var body = await Http.ReadBody<SetupRequest>(ctx);
            if (body?.Code is null || body.Username is null || body.Password is null)
                return Err.Detail(400, "code, username and password are required");
            var res = auth.Setup(body.Code, body.Username, body.Password);
            if (res.Status != 201) return Err.Detail(res.Status, res.Detail!);
            return Http.Json(new { username = body.Username, role = "admin" }, 201);
        });

        app.MapPost("/api/auth/login", async (HttpContext ctx) =>
        {
            var body = await Http.ReadBody<LoginRequest>(ctx);
            if (body?.Username is null || body.Password is null)
                return Err.Detail(400, "username and password are required");
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "local";
            var res = auth.Login(body.Username, body.Password, ip);
            if (res.User is null) return Err.Detail(res.Status, res.Detail!);
            SessionMiddleware.SetSessionCookie(ctx, res.SessionToken!);
            return Http.Json(new { username = res.User.Username, role = res.User.Role });
        });

        app.MapPost("/api/auth/logout", (HttpContext ctx) =>
        {
            if (Guard.User(ctx) is not CurrentUser u) return Err.Detail(401, "authentication required");
            auth.Logout(u);
            SessionMiddleware.ClearSessionCookie(ctx);
            return Http.Json(new { ok = true });
        });

        app.MapGet("/api/auth/me", (HttpContext ctx) =>
        {
            if (Guard.User(ctx) is not CurrentUser u) return Err.Detail(401, "authentication required");
            return Http.Json(new
            {
                username = u.Username,
                role = u.Role,
                group_scope = u.GroupScope,
                schoolwide_broadcast = u.SchoolwideBroadcast,
            });
        });
    }
}

public static class EnrollmentEndpoints
{
    public static void Map(WebApplication app)
    {
        var enroll = app.Services.GetRequiredService<EnrollmentService>();

        // Device-side endpoint, like /api/enroll: cookie-less machine client, so CSRF (a
        // cookie-session protection) does not apply here.
        app.MapPost("/api/enroll", async (HttpContext ctx) =>
        {
            var body = await Http.ReadBody<EnrollRequest>(ctx);
            var res = enroll.Enroll(body?.Token ?? "", body?.DeviceName ?? "");
            if (res.Result is null) return Err.Detail(res.Status, res.Detail!);
            // Exact PROTOCOL.md §2 201 shape; secrets must never be logged server-side.
            return Http.Json(new
            {
                device_id = res.Result.DeviceId,
                device_secret = res.Result.DeviceSecret,
                server_trust_pin = res.Result.ServerTrustPin,
            }, 201);
        });

        app.MapGet("/api/admin/enrollment-tokens", (HttpContext ctx) =>
        {
            if (Guard.User(ctx) is not CurrentUser u || !Guard.HasRole(u, "admin"))
                return Err.Detail(403, "admin role required");
            return Http.Json(enroll.ListTokens());
        });

        app.MapPost("/api/admin/enrollment-tokens", async (HttpContext ctx) =>
        {
            if (Guard.User(ctx) is not CurrentUser u || !Guard.HasRole(u, "admin"))
                return Err.Detail(403, "admin role required");
            var body = await Http.ReadBody<CreateTokenRequest>(ctx);
            if (body?.Label is null) return Err.Detail(400, "label is required");
            var res = enroll.CreateToken(u.Username, body.Label, body.TtlHours ?? 24,
                body.MaxUses ?? 1, body.GroupScope ?? []);
            if (res.Info is null) return Err.Detail(res.Status, res.Detail!);
            // The full token appears exactly once, in this response only.
            return Http.Json(new
            {
                id = res.Info.Id,
                label = res.Info.Label,
                token = res.Token,
                expires_at = res.Info.ExpiresAt,
                max_uses = res.Info.MaxUses,
                group_scope = res.Info.GroupScope,
                created_at = res.Info.CreatedAt,
            }, 201);
        });

        app.MapDelete("/api/admin/enrollment-tokens/{id:int}", (HttpContext ctx, int id) =>
        {
            if (Guard.User(ctx) is not CurrentUser u || !Guard.HasRole(u, "admin"))
                return Err.Detail(403, "admin role required");
            var (status, detail) = enroll.RevokeToken(u.Username, id);
            return status == 200 ? Http.Json(new { ok = true }) : Err.Detail(status, detail!);
        });
    }

    internal sealed record CreateTokenRequest(string? Label, int? TtlHours, int? MaxUses, List<long>? GroupScope);
}
