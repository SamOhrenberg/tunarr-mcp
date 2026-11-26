using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OtpNet;
using TunarrMcp.Server.Auth;
using TunarrMcp.Server.Configuration;

namespace TunarrMcp.Tests.Unit.Auth;

public sealed class AdminLoginTests
{
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.Zero));
    private readonly AdminLogin _login;

    public AdminLoginTests()
    {
        var options = Options.Create(new AuthOptions
        {
            AdminUser = TestAdmin.User,
            AdminPasswordHash = TestAdmin.PasswordHash,
            TotpSecret = TestAdmin.TotpSecret,
        });
        _login = new AdminLogin(options, _clock, NullLogger<AdminLogin>.Instance);
    }

    private string Code() => new Totp(Base32Encoding.ToBytes(TestAdmin.TotpSecret)).ComputeTotp(_clock.GetUtcNow().UtcDateTime);

    [Fact]
    public void Correct_credentials_succeed()
    {
        Assert.Equal(LoginResult.Success, _login.Verify(TestAdmin.User, TestAdmin.Password, Code()));
    }

    [Fact]
    public void Code_with_a_space_is_accepted()
    {
        var code = Code();

        Assert.Equal(LoginResult.Success, _login.Verify(TestAdmin.User, TestAdmin.Password, code[..3] + " " + code[3..]));
    }

    [Theory]
    [InlineData("Admin", TestAdmin.Password, true)]
    [InlineData(TestAdmin.User, "wrong", true)]
    [InlineData(TestAdmin.User, TestAdmin.Password, false)]
    public void Any_wrong_part_fails(string user, string password, bool validCode)
    {
        Assert.Equal(LoginResult.Invalid, _login.Verify(user, password, validCode ? Code() : "000000"));
    }

    [Fact]
    public void A_code_works_only_once()
    {
        var code = Code();
        Assert.Equal(LoginResult.Success, _login.Verify(TestAdmin.User, TestAdmin.Password, code));

        Assert.Equal(LoginResult.Invalid, _login.Verify(TestAdmin.User, TestAdmin.Password, code));

        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(LoginResult.Success, _login.Verify(TestAdmin.User, TestAdmin.Password, Code()));
    }

    [Fact]
    public void Codes_from_one_step_either_side_are_accepted()
    {
        var previous = Code();
        _clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(LoginResult.Success, _login.Verify(TestAdmin.User, TestAdmin.Password, previous));
    }

    [Fact]
    public void Locks_out_after_ten_failures_even_with_correct_credentials_then_recovers()
    {
        for (var i = 0; i < AdminLogin.MaxFailures; i++)
            Assert.Equal(LoginResult.Invalid, _login.Verify(TestAdmin.User, "wrong", Code()));

        Assert.Equal(LoginResult.LockedOut, _login.Verify(TestAdmin.User, TestAdmin.Password, Code()));

        _clock.Advance(AdminLogin.LockoutDuration);
        Assert.Equal(LoginResult.Success, _login.Verify(TestAdmin.User, TestAdmin.Password, Code()));
    }
}

internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
