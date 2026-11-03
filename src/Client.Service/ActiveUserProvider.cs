using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using SchoolBell.Client.Core;

namespace SchoolBell.Client.Service;

/// Active console-session user via WTS APIs (plan §5: report the device-side Windows
/// identity; it is NEVER an authorization credential). Requires SeTcbPrivilege, which
/// the SYSTEM-run service has; every failure degrades honestly to "no signed-in user".
/// Windows-only glue — compile-verified on Linux, behavior-verified on Windows.
public sealed class ActiveUserProvider : IIdentityProvider
{
    private readonly ILogger _log;
    private long _lastWarnMs;

    public ActiveUserProvider(ILogger log) => _log = log;

    public (string? User, string? Sid) GetActiveUser()
    {
        if (!OperatingSystem.IsWindows()) return (null, null);
        try
        {
            var sidString = GetActiveUserSidString();
            if (sidString is null) return (null, null);
            using var identity = new WindowsIdentity(sidString);
            var name = identity.Name.Contains('\\')
                ? identity.Name[(identity.Name.LastIndexOf('\\') + 1)..]
                : identity.Name;
            return (name, sidString);
        }
        catch (Exception e)
        {
            WarnThrottled(e);
            return (null, null);
        }
    }

    /// SID string of the active console session user, or null when signed out.
    public string? GetActiveUserSidString()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFFu) return null; // no console session attached
        if (!WTSQueryUserToken(sessionId, out var token))
        {
            WarnThrottled(new Win32Exception(Marshal.GetLastWin32Error(), "WTSQueryUserToken failed"));
            return null;
        }
        try
        {
            return TokenUserSid(token);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static string? TokenUserSid(IntPtr token)
    {
        // TOKEN_USER SID via GetTokenInformation — done through WindowsIdentity's own
        // parsing by re-opening the token handle as a user identity.
        var dup = IntPtr.Zero;
        try
        {
            if (!DuplicateTokenEx(token, 0, IntPtr.Zero, 0 /*SecurityImpersonation*/, 1 /*TokenPrimary*/, out dup))
                return null;
            using var identity = new WindowsIdentity(dup);
            return identity.User.Value;
        }
        finally
        {
            if (dup != IntPtr.Zero) CloseHandle(dup);
        }
    }

    private void WarnThrottled(Exception e)
    {
        var now = Environment.TickCount64;
        if (now - _lastWarnMs < 60_000) return;
        _lastWarnMs = now;
        _log.LogWarning("active-user lookup failed: {Error} (reporting no signed-in user; identity is never a credential)",
            e.Message);
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existingToken, int desiredAccess, IntPtr tokenAttributes,
        int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
