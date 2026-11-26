using Microsoft.AspNetCore.Authentication.Cookies;

namespace TunarrMcp.Server.Auth;

/// <summary>
/// The admin's browser session. It only bridges login → consent, so it's short and never slides.
/// SameSite=Lax so it survives the top-level redirect from the client (claude.ai) to /connect/authorize.
/// </summary>
internal static class AdminCookie
{
    public const string Scheme = "admin";

    public static void Configure(CookieAuthenticationOptions options)
    {
        options.Cookie.Name = "tunarr_mcp_admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // https behind the proxy via forwarded headers
        options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
        options.SlidingExpiration = false;
        options.LoginPath = "/login";
    }
}
