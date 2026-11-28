# Tunarr MCP

An [MCP](https://modelcontextprotocol.io) server for [Tunarr](https://github.com/chrisbenincasa/tunarr), so you can ask Claude
what's on, check on your server, and (soon) build channels and schedules in plain language, from claude.ai on the web,
desktop or mobile, or from Claude Code.

It runs as one small container next to Tunarr. Tunarr stays private on your Docker network; only this server is exposed,
behind its own OAuth 2.1 login.

> **Status: early.** Authentication and deployment are done; library and channel-schedule tools are being added.

## Tools

| Tool | Mode | What it does |
| --- | --- | --- |
| `system_status` | read | Tunarr version, failing health checks, scheduled tasks, how far ahead the guide is built |
| `list_channels` | read | Every channel's number, name and id, and whether it's programmed by hand or by a slot schedule |

## How it works

- **Embedded OAuth server.** One admin account: password plus a TOTP code from your authenticator app. Clients register
  themselves through [client ID metadata documents](https://datatracker.ietf.org/doc/draft-ietf-oauth-client-id-metadata-document/)
  (what Claude uses) or dynamic client registration, but only with allowed redirect URIs (Claude's, by default), and
  every connection still needs your login and consent.
- **Read-only by default.** `Tools__Mode=read` registers read tools only; write and destructive tools have to be turned on.
- **Hardened container.** Non-root, no shell, read-only root filesystem, ~80 MB, multi-arch (amd64, arm64).

## Quick start

You need Tunarr and a reverse proxy that terminates TLS (Nginx Proxy Manager, Traefik, Caddy, ...) in Docker, and a
public hostname for this server.

1. **Create the admin credentials.** This asks for a password, then shows a QR code for your authenticator app:

   ```sh
   docker run --rm -it -v ./secrets:/secrets ghcr.io/samohrenberg/tunarr-mcp setup --out /secrets
   ```

2. **Add the service** to your compose file. [`compose.example.yaml`](compose.example.yaml) has a complete, commented
   example. Set `Mcp__PublicUrl` to your public URL, for example `https://mcp.example.com/mcp`.

3. **Point your reverse proxy** at `tunarr-mcp:8080` with TLS. For Nginx Proxy Manager, add this under *Advanced*,
   since MCP streams responses:

   ```nginx
   proxy_buffering off;
   proxy_read_timeout 300s;
   ```

4. **Connect Claude.** In claude.ai: *Customize → Connectors → Add custom connector*, enter your `/mcp` URL and leave the
   OAuth fields empty. In Claude Code: `claude mcp add --transport http tunarr https://mcp.example.com/mcp`.
   Sign in with your password and code when the browser opens.

## Configuration

Environment variables (or `appsettings.json` keys, with `:` instead of `__`). Every secret also accepts a `*File`
variant for Docker secrets.

| Key | Default | Purpose |
| --- | --- | --- |
| `Mcp__PublicUrl` | required | Public URL of the MCP endpoint. The OAuth issuer and metadata URLs derive from it |
| `Tunarr__BaseUrl` | `http://tunarr:8000/api/` | Tunarr's API |
| `Tunarr__BasicAuthUser`, `Tunarr__BasicAuthPassword(File)` | unset | Only if Tunarr has basic auth turned on |
| `Tools__Mode` | `read` | `read`, `write` or `all` |
| `Auth__Mode` | `Embedded` | `Embedded`, or `None` for LAN/local testing (also needs `Auth__AllowInsecureNone=true`) |
| `Auth__AdminUser` | required | Admin username |
| `Auth__AdminPasswordHash(File)`, `Auth__TotpSecret(File)` | required | Written by `setup` |
| `Auth__AllowedRedirectUris` | Claude's callbacks | Comma-separated; http loopback entries match any port |
| `Auth__AllowedIpRanges` | unset | Optional comma-separated CIDR allowlist for `/mcp` |
| `Data__Path` | `/data` | SQLite database and keys; mount a volume here |
| `TZ` | UTC | Time zone for times in tool output |

## Development

Requires the .NET 10 SDK.

```sh
dotnet test --solution TunarrMcp.slnx

# Local credentials, then run with auth on (http://localhost:5080/mcp).
# dotnet run starts in the project folder, hence the ../../ path.
dotnet run --project src/TunarrMcp.Server -- setup --out ../../.data/secrets
dotnet run --project src/TunarrMcp.Server

# Or without auth, for working on tools:
dotnet run --project src/TunarrMcp.Server --launch-profile http-noauth
```

Point it at your Tunarr with `dotnet user-secrets --project src/TunarrMcp.Server set "Tunarr:BaseUrl" "http://<host>:8000/api/"`.

## License

[MIT](LICENSE)
