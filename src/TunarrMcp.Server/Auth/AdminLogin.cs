using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using OtpNet;
using TunarrMcp.Server.Configuration;

namespace TunarrMcp.Server.Auth;

internal enum LoginResult
{
    Success,
    Invalid,
    LockedOut,
}

/// <summary>
/// Checks the single admin's username, password and TOTP code. All three are always evaluated, and the
/// caller only learns "invalid", so a failure doesn't reveal which part was wrong.
/// State (failure count, lockout, last TOTP step) is in memory: a restart clears it, which is acceptable for
/// one instance and one user. Per-IP rate limiting on POST /login is the first line of defence; this lockout
/// is the backstop when attempts come from many IPs.
/// </summary>
internal sealed class AdminLogin(IOptions<AuthOptions> options, TimeProvider clock, ILogger<AdminLogin> logger)
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly PasswordHasher<AdminLogin> _hasher = new();
    private readonly Lock _gate = new();
    private int _failures;
    private DateTimeOffset _lockedUntil;
    private long _lastUsedStep = -1;

    public LoginResult Verify(string? username, string? password, string? code)
    {
        var now = clock.GetUtcNow();
        var admin = options.Value;

        // One check at a time. PBKDF2 makes each one slow, so this also throttles parallel guessing.
        lock (_gate)
        {
            if (now < _lockedUntil)
                return LoginResult.LockedOut;

            var userOk = FixedTimeEquals(username ?? "", admin.AdminUser!);
            var passwordOk = _hasher.VerifyHashedPassword(this, admin.AdminPasswordHash!, password ?? "") != PasswordVerificationResult.Failed;
            var codeOk = VerifyCode(admin.TotpSecret!, code, now, out var step) && step > _lastUsedStep; // each code works once

            if (userOk && passwordOk && codeOk)
            {
                _failures = 0;
                _lastUsedStep = step;
                return LoginResult.Success;
            }

            if (++_failures >= MaxFailures)
            {
                _failures = 0;
                _lockedUntil = now + LockoutDuration;
                AuthLog.LockedOut(logger, LockoutDuration.TotalMinutes, MaxFailures);
            }

            return LoginResult.Invalid;
        }
    }

    private static bool VerifyCode(string secret, string? code, DateTimeOffset now, out long step)
    {
        step = -1;
        var digits = code?.Replace(" ", "", StringComparison.Ordinal) ?? "";
        if (digits.Length != 6 || !digits.All(char.IsAsciiDigit))
            return false;

        // One step either side tolerates clock drift between the server and the phone.
        return new Totp(Base32Encoding.ToBytes(secret))
            .VerifyTotp(now.UtcDateTime, digits, out step, VerificationWindow.RfcSpecifiedNetworkDelay);
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
