using Microsoft.Extensions.Options;
using ModelContextProtocol;
using TunarrMcp.Server.Configuration;
using TunarrMcp.Tunarr.Models;

namespace TunarrMcp.Server.Tools;

/// <summary>Enforces Tools:WritableChannels: write tools may only change the channels it lists, when it lists any.</summary>
public sealed class WritableChannels(IOptions<ToolsOptions> options)
{
    private readonly IReadOnlyList<string> _allowed = options.Value.ParseWritableChannels();

    public void EnsureWritable(Channel channel)
    {
        if (_allowed.Count == 0)
            return;

        var number = channel.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_allowed.Any(a => a == number || string.Equals(a, channel.Id, StringComparison.OrdinalIgnoreCase)))
            return;

        throw new McpException(
            $"Channel {channel.Number} {channel.Name} is read-only here: Tools:WritableChannels only allows {string.Join(", ", _allowed)}.");
    }
}
