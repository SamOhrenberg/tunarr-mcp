using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;

namespace TunarrMcp.Server.Auth;

/// <summary>
/// The login and consent pages: plain HTML, no scripts, every value HTML-encoded.
/// Served with headers that forbid framing (clickjacking on the consent button) and caching.
/// </summary>
internal static class AuthPages
{
    public static IResult Login(HttpContext context, AntiforgeryTokenSet tokens, string returnUrl, string? error) => Render(context, "Sign in",
        $"""
        <h1>Sign in to Tunarr MCP</h1>
        {(error is null ? "" : $"<p class=error role=alert>{E(error)}</p>")}
        <form method=post action="/login">
          {Antiforgery(tokens)}
          <input type=hidden name=returnUrl value="{E(returnUrl)}">
          <label>Username <input name=username autocomplete=username required autofocus></label>
          <label>Password <input name=password type=password autocomplete=current-password required></label>
          <label>Authenticator code <input name=code inputmode=numeric autocomplete=one-time-code pattern="[0-9 ]{"{6,7}"}" required></label>
          <button type=submit>Sign in</button>
        </form>
        """);

    public static IResult Consent(HttpContext context, AntiforgeryTokenSet tokens, string clientName, Uri redirectUri,
        IEnumerable<KeyValuePair<string, string>> requestParameters)
    {
        var hidden = new StringBuilder();
        foreach (var (name, value) in requestParameters)
            hidden.Append("<input type=hidden name=\"").Append(E(name)).Append("\" value=\"").Append(E(value)).Append("\">\n");

        return Render(context, "Allow access",
            $"""
            <h1>Allow access to Tunarr?</h1>
            <p>An app that calls itself <strong>{E(clientName)}</strong> wants to use your Tunarr server through MCP.</p>
            <p class=host>After you approve, you'll be sent to<br><strong>{E(redirectUri.Authority)}</strong></p>
            <p class=hint>Only approve if you just started connecting this app yourself.</p>
            <form method=post action="/connect/authorize">
              {Antiforgery(tokens)}
              {hidden}
              <button type=submit name=decision value=approve>Approve</button>
              <button type=submit name=decision value=deny class=secondary>Deny</button>
            </form>
            """);
    }

    public static IResult Message(HttpContext context, string title, string text) =>
        Render(context, title, $"<h1>{E(title)}</h1><p>{E(text)}</p>");

    private static IResult Render(HttpContext context, string title, string body)
    {
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'";
        headers.XFrameOptions = "DENY";
        headers.CacheControl = "no-store";
        headers["Referrer-Policy"] = "no-referrer";
        headers.XContentTypeOptions = "nosniff";

        return Results.Content(
            $$"""
            <!doctype html>
            <html lang=en>
            <head>
            <meta charset=utf-8>
            <meta name=viewport content="width=device-width, initial-scale=1">
            <title>{{E(title)}} · Tunarr MCP</title>
            <style>
              :root { color-scheme: light dark; font-family: system-ui, sans-serif; }
              body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: Canvas; }
              main { width: min(24rem, 100% - 2rem); padding: 1.5rem; border: 1px solid #8884; border-radius: .75rem; }
              h1 { font-size: 1.25rem; margin-top: 0; }
              label { display: block; margin: .75rem 0; }
              input { display: block; width: 100%; box-sizing: border-box; margin-top: .25rem; padding: .5rem; font: inherit; }
              button { font: inherit; padding: .5rem 1rem; margin: .75rem .5rem 0 0; border-radius: .375rem; border: 1px solid #8888; cursor: pointer; }
              button[type=submit]:not(.secondary) { background: #2563eb; color: white; border-color: #2563eb; }
              .error { color: #dc2626; }
              .host strong { font-size: 1.25rem; }
              .hint { opacity: .75; font-size: .9rem; }
            </style>
            </head>
            <body><main>
            {{body}}
            </main></body>
            </html>
            """,
            "text/html; charset=utf-8");
    }

    private static string Antiforgery(AntiforgeryTokenSet tokens) =>
        $"<input type=hidden name=\"{E(tokens.FormFieldName)}\" value=\"{E(tokens.RequestToken ?? "")}\">";

    private static string E(string value) => HtmlEncoder.Default.Encode(value);
}
