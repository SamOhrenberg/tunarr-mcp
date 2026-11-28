using System.Globalization;
using ModelContextProtocol;
using TunarrMcp.Tunarr;
using TunarrMcp.Tunarr.Models;

namespace TunarrMcp.Server.Tools;

/// <summary>
/// Channel tools take a channel as its id, number or exact name, since people say "channel 5". Unknown channels fail with
/// the list of real ones, so Claude can correct itself without another call.
/// </summary>
internal static class ChannelLookup
{
    public static async Task<Channel> FindAsync(TunarrClient tunarr, string channel, CancellationToken ct)
    {
        var channels = await tunarr.GetChannelsAsync(ct).ConfigureAwait(false);
        var trimmed = channel.Trim();

        return channels.FirstOrDefault(c => string.Equals(c.Id, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? channels.FirstOrDefault(c => c.Number == number)
                : null)
            ?? channels.FirstOrDefault(c => string.Equals(c.Name.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException($"No channel '{channel}'. Channels: " +
                string.Join(", ", channels.OrderBy(c => c.Number).Select(c => $"{c.Number} {c.Name}")) + ".");
    }
}
