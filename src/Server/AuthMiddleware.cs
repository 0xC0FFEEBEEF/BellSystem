using SchoolBell.Contracts;
using Microsoft.AspNetCore.Http;

namespace SchoolBell.Server;

public static class CookieNames
{
    public const string Session = "sb_session";
    public const string Csrf = "sb_csrf";
    public const string CsrfHeader = "X-CSRF";
}

/// Loads the cookie session (server-side session store) and ensures the CSRF double-submit cookie.
/// Cookies are HttpOnly (session) / SameSite=Lax; Secure is written on HTTPS writes.
public sealed class SessionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AuthService _auth;

    public SessionMiddleware(RequestDelegate next, AuthService auth)
    {
        _next = next; _auth = auth;
    }

    public Task Invoke(HttpContext ctx)
    {
        var csrf = ctx.Request.Cookies[CookieNames.Csrf];
        if (string.IsNullOrEmpty(csrf))
        {
            csrf = Util.RandomHex(16);
            ctx.Response.Cookies.Append(CookieNames.Csrf, csrf, new CookieOptions
            {
                HttpOnly = false,           // client must echo it in X-CSRF
                SameSite = SameSiteMode.Lax,
                Secure = ctx.Request.IsHttps,
                Path = "/",
            });
        }
        ctx.Items["csrf"] = csrf;

        if (_auth.ResolveSession(ctx.Request.Cookies[CookieNames.Session]) is CurrentUser user)
            ctx.Items["user"] = user;

        return _next(ctx);
    }

    public static void SetSessionCookie(HttpContext ctx, string token) =>
        ctx.Response.Cookies.Append(CookieNames.Session, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            Path = "/",
            MaxAge = TimeSpan.FromMilliseconds(AuthService.SessionLifetimeMs),
        });

    public static void ClearSessionCookie(HttpContext ctx) =>
        ctx.Response.Cookies.Append(CookieNames.Session, "", new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = ctx.Request.IsHttps,
            Path = "/", MaxAge = TimeSpan.Zero,
        });
}

/// CSRF double-submit: every non-GET /api write must send X-CSRF matching the sb_csrf cookie.
public sealed class CsrfMiddleware
{
    private readonly RequestDelegate _next;

    public CsrfMiddleware(RequestDelegate next) => _next = next;

    public Task Invoke(HttpContext ctx)
    {
        var method = ctx.Request.Method;
        var isWrite = !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method);
        // /api/enroll is a cookie-less machine endpoint (PROTOCOL.md §2): CSRF protects
        // cookie-session office clients only.
        bool isMachineEndpoint = ctx.Request.Path == "/api/enroll" || ctx.Request.Path == SchoolBell.Contracts.ClientLogAuth.Path;
        if (isWrite && !isMachineEndpoint && ctx.Request.Path.StartsWithSegments("/api"))
        {
            var cookie = ctx.Request.Cookies[CookieNames.Csrf];
            var header = ctx.Request.Headers[CookieNames.CsrfHeader];
            if (string.IsNullOrEmpty(cookie) || header.Count != 1 || header[0] != cookie)
            {
                ctx.Response.StatusCode = 401;
                return ctx.Response.WriteAsJsonAsync(
                    new { detail = "csrf token missing or invalid" }, Util.ApiJson);
            }
        }
        return _next(ctx);
    }
}

public static class Guard
{
    public static CurrentUser? User(HttpContext ctx) => ctx.Items["user"] as CurrentUser;

    public static bool HasRole(CurrentUser u, params string[] roles) => roles.Contains(u.Role);

    /// Operator scope rules (PROTOCOL.md §10 / plan §6): operators act within their group_scope;
    /// empty group_scope grants no groups; target.all requires the explicit schoolwide_broadcast flag.
    public static bool CanTarget(CurrentUser u, TargetScope target, Func<string, IEnumerable<long>> deviceGroups)
    {
        if (u.Role == "admin") return true;
        if (u.Role != "operator") return false;
        if (target.All == true) return u.SchoolwideBroadcast;
        if (target.Groups is { Count: > 0 })
            return target.Groups.All(g => u.GroupScope.Contains(g));
        if (target.Devices is { Count: > 0 })
            return target.Devices.All(d => deviceGroups(d).Any(g => u.GroupScope.Contains(g)));
        return false;
    }
}
