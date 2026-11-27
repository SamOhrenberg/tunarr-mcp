using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TunarrMcp.Server.Auth.Registration;

internal interface IClientMetadataFetcher
{
    Task<string> FetchAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>
/// Fetches a client metadata document. The URL comes from whoever calls /connect/authorize, so this is
/// hardened against being used to probe the private network (SSRF): it only connects to public addresses,
/// checked at connect time so DNS rebinding can't slip past; it doesn't follow redirects or use a proxy;
/// and it gives up after 5 seconds or 16 KB.
/// </summary>
internal sealed class ClientMetadataFetcher(HttpClient http) : IClientMetadataFetcher
{
    public const int MaxDocumentBytes = 16 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        ConnectTimeout = Timeout,
        ConnectCallback = ConnectToPublicAddressAsync,
    };

    public async Task<string> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK)
                throw new ClientMetadataException($"Fetching the client metadata document returned {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength > MaxDocumentBytes)
                throw new ClientMetadataException("The client metadata document is too large.");

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[MaxDocumentBytes + 1];
            var length = 0;
            int read;
            while (length < buffer.Length && (read = await body.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false)) > 0)
                length += read;
            if (length > MaxDocumentBytes)
                throw new ClientMetadataException("The client metadata document is too large.");

            return Encoding.UTF8.GetString(buffer, 0, length);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ClientMetadataException($"The client metadata document could not be fetched: {e.Message}");
        }
    }

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        var publicAddresses = addresses.Where(IsPublic).ToArray();
        if (publicAddresses.Length == 0)
            throw new HttpRequestException($"{host} does not resolve to a public address.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(publicAddresses, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static readonly IPNetwork[] NonPublic =
    [
        IPNetwork.Parse("0.0.0.0/8"), IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("169.254.0.0/16"), IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"), IPNetwork.Parse("192.168.0.0/16"), IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("224.0.0.0/4"), IPNetwork.Parse("240.0.0.0/4"),
        IPNetwork.Parse("::/128"), IPNetwork.Parse("::1/128"), IPNetwork.Parse("64:ff9b::/96"),
        IPNetwork.Parse("fc00::/7"), IPNetwork.Parse("fe80::/10"), IPNetwork.Parse("ff00::/8"),
    ];

    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return !NonPublic.Any(n => n.Contains(address));
    }
}
