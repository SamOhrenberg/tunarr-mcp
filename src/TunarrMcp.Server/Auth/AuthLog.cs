using System.Net;

namespace TunarrMcp.Server.Auth;

/// <summary>
/// Sign-in events, for spotting brute force. Usernames and codes are never logged: people sometimes
/// type a password into the username field.
/// </summary>
internal static partial class AuthLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Admin signed in from {RemoteIp}")]
    public static partial void SignedIn(ILogger logger, IPAddress? remoteIp);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed admin login from {RemoteIp}")]
    public static partial void FailedLogin(ILogger logger, IPAddress? remoteIp);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin login refused while locked out, from {RemoteIp}")]
    public static partial void LockedOutAttempt(ILogger logger, IPAddress? remoteIp);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin login locked for {Minutes} minutes after {Failures} failed attempts")]
    public static partial void LockedOut(ILogger logger, double minutes, int failures);
}
