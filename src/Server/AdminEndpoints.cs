namespace SchoolBell.Server;

internal sealed record UserCreateRequest(string? Username, string? Password, string? Role,
    List<long>? GroupScope, bool? SchoolwideBroadcast);
internal sealed record UserUpdateRequest(string? Role, List<long>? GroupScope, bool? SchoolwideBroadcast, string? Password);
internal sealed record GroupRequest(string? Name);
internal sealed record MemberRequest(string? DeviceId);
internal sealed record SettingsRequest(string? SchoolTz, string? ServerName);
internal sealed record KeepAwakeRequest(string? Scope, string? Id, int? DurationMinutes, long? UntilUtc, bool? DisplayOff);
internal sealed record DeviceNameRequest(string? DeviceId);

public static class AdminEndpoints
{
    public static void Map(WebApplication app)
    {
        var auth = app.Services.GetRequiredService<AuthService>();
        var directory = app.Services.GetRequiredService<DirectoryService>();
        var keepAwake = app.Services.GetRequiredService<KeepAwakeService>();
        var enrollment = app.Services.GetRequiredService<EnrollmentService>();
        var diagnostics = app.Services.GetRequiredService<DiagnosticsService>();

        static (CurrentUser? User, IResult? Error) Admin(HttpContext ctx)
        {
            if (Guard.User(ctx) is not CurrentUser u)
                return (null, Err.Detail(401, "authentication required"));
            if (u.Role != "admin")
                return (null, Err.Detail(403, "admin role required"));
            return (u, null);
        }

        app.MapGet("/api/admin/users", (HttpContext ctx) =>
        {
            var (user, err) = Admin(ctx);
            if (user is null) return err!;
            return Http.Json(auth.ListUsers());
        });

        app.MapPost("/api/admin/users", async (HttpContext ctx) =>
        {
            var (user, err) = Admin(ctx);
            if (user is null) return err!;
            var body = await Http.ReadBody<UserCreateRequest>(ctx);
            if (body?.Username is null || body.Password is null || body.Role is null)
                return Err.Detail(400, "username, password and role are required");
            var res = auth.CreateUser(user.Username, body.Username, body.Password, body.Role,
                body.GroupScope ?? [], body.SchoolwideBroadcast == true);
            if (res.User is null) return Err.Detail(res.Status, res.Detail!);
            return Http.Json(new
            {
                id = res.User.Id, username = res.User.Username, role = res.User.Role,
                group_scope = res.User.GroupScope, schoolwide_broadcast = res.User.SchoolwideBroadcast,
            }, 201);
        });

        app.MapPut("/api/admin/users/{id:int}", async (HttpContext ctx, int id) =>
        {
            var (user, err) = Admin(ctx);
            if (user is null) return err!;
            var body = await Http.ReadBody<UserUpdateRequest>(ctx);
            var (status, detail) = auth.UpdateUser(user.Username, id, body?.Role, body?.GroupScope,
                body?.SchoolwideBroadcast, body?.Password);
            return status == 200 ? Http.Json(new { ok = true }) : Err.Detail(status, detail!);
        });

        app.MapDelete("/api/admin/users/{id:int}", (HttpContext ctx, int id) =>
        {
            var (user, err) = Admin(ctx);
            if (user is null) return err!;
            if (id == user.Id) return Err.Detail(409, "cannot delete your own account");
            var (status, detail) = auth.DeleteUser(user.Username, id);
            return status == 200 ? Http.Json(new { ok = true }) : Err.Detail(status, detail!);
        });

        app.MapGet("/api/admin/groups", (HttpContext ctx) =>
        {
            var (user, err) = Admin(ctx);
            if (user is null) return err!;
            return Http.Json(directory.ListGroups());
        });

        app.MapPost("/api/admin/groups", async (HttpContext ctx) =>
        {
            var (user, err) = Admin(ctx);
            if (user is null) return err!;
            var body = await Http.ReadBody<GroupRequest>(ctx);
            var (status, detail, group) = directory.CreateGroup(user.Username, body?.Name ?? "");
            return group is null ? Err.Detail(status, detail!) : Http.Json(group, 201);
        });

        app.MapDelete("/api/admin/groups/{id:int}", (HttpContext ctx, int id) =>
        {
            var (user, err) = Admin(ctx);
            if (user is null) return err!;
            var (status, detail) = directory.DeleteGroup(user.Username, id);
            return status == 200 ? Http.Json(new { ok = true }) : Err.Detail(status, detail!);
        });

        app.MapGet("/api/admin/groups/{id:int}/members", (HttpContext ctx, int id) =>
        {
            var (user, err) = Admin(ctx);
            if (user is null) return err!;
            return Http.Json(directory.Members(id));
        });

        app.MapPost("/api/admin/groups/{id:int}/members", async (HttpContext ctx, int id) =>
        {
            var (user, err) = Admin(ctx);
            if (user is null) return err!;
            var body = await Http.ReadBody<MemberRequest>(ctx);
            var (status, detail) = directory.AddMember(user.Username, id, body?.DeviceId ?? "");
            if (status == 200) ctx.RequestServices.GetRequiredService<DeviceSettingsService>().Touch(body!.DeviceId!);
            return status == 200 ? Http.Json(new { ok = true }) : Err.Detail(status, detail!);
        });

        app.MapDelete("/api/admin/groups/{id:int}/members/{deviceId}", (HttpContext ctx, int id, string deviceId) =>
        {
            var (user, err) = Admin(ctx);
            if (user is null) return err!;
            var (status, detail) = directory.RemoveMember(user.Username, id, deviceId);
            if (status == 200) ctx.RequestServices.GetRequiredService<DeviceSettingsService>().Touch(deviceId);
            return status == 200 ? Http.Json(new { ok = true }) : Err.Detail(status, detail!);
        });

        app.MapGet("/api/admin/settings", (HttpContext ctx) =>
        {
            var (user, err) = Admin(ctx);
            if (user is null) return err!;
            return Http.Json(directory.GetSettings());
        });

        app.MapPut("/api/admin/settings", async (HttpContext ctx) =>
        {
            var (user, err) = Admin(ctx);
            if (user is null) return err!;
            var body = await Http.ReadBody<SettingsRequest>(ctx);
            var (status, detail) = directory.UpdateSettings(user.Username, body?.SchoolTz, body?.ServerName);
            return status == 200 ? Http.Json(directory.GetSettings()) : Err.Detail(status, detail!);
        });

        app.MapPost("/api/admin/devices/revoke", async (HttpContext ctx) =>
        {
            var (user, err) = Admin(ctx);
            if (user is null) return err!;
            var body = await Http.ReadBody<DeviceNameRequest>(ctx);
            var (status, detail) = enrollment.RevokeDevice(user.Username, body?.DeviceId ?? "");
            if (status != 200) return Err.Detail(status, detail!);
            app.Services.GetRequiredService<ControlRegistry>().CloseDevice(body!.DeviceId!, "revoked by administrator");
            return Http.Json(new { ok = true });
        });

        app.MapGet("/api/admin/keepawake", (HttpContext ctx) =>
        {
            var (user, err) = Admin(ctx);
            if (user is null) return err!;
            return Http.Json(keepAwake.List());
        });

        app.MapPost("/api/admin/keepawake", async (HttpContext ctx) =>
        {
            var (user, err) = Admin(ctx);
            if (user is null) return err!;
            var body = await Http.ReadBody<KeepAwakeRequest>(ctx);
            var res = keepAwake.Create(user.Username, body?.Scope ?? "", body?.Id,
                body?.DurationMinutes, body?.UntilUtc, body?.DisplayOff == true);
            return res.Status == 201 ? Http.Json(new { ok = true }, 201) : Err.Detail(res.Status, res.Detail!);
        });

        app.MapDelete("/api/admin/keepawake/{policyId}", (HttpContext ctx, string policyId) =>
        {
            var (user, err) = Admin(ctx);
            if (user is null) return err!;
            var res = keepAwake.Release(user.Username, policyId);
            return res.Status == 200 ? Http.Json(new { ok = true }) : Err.Detail(res.Status, res.Detail!);
        });

        app.MapGet("/api/diagnostics", (HttpContext ctx) =>
        {
            if (Guard.User(ctx) is null) return Err.Detail(401, "authentication required");
            return Http.Json(diagnostics.Snapshot());
        });
    }
}
