using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TunarrMcp.Tunarr.Models;

namespace TunarrMcp.Server.Changes;

/// <summary>
/// What a snapshot keeps of a channel. Lineup is Tunarr's lineup items, as JSON, exactly as read. Changes that can
/// alter the schedule also capture it (ScheduleCaptured): Schedule is then the saved schedule without display
/// fields, or null when the channel had none. Lineup-only changes leave the schedule alone, so they don't capture it.
/// </summary>
public sealed record ChannelState(JsonElement Lineup)
{
    public bool ScheduleCaptured { get; init; }

    public JsonElement? Schedule { get; init; }
}

/// <summary>
/// A hash of a lineup's items (type, program and whole-ms duration, in order). Two lineups with the same
/// fingerprint play the same thing, so undo can tell whether a channel changed after the write it reverts.
/// </summary>
public static class LineupFingerprint
{
    public static string Of(IEnumerable<LineupItem> items)
    {
        var text = new StringBuilder();
        foreach (var item in items)
        {
            text.Append(item.Type).Append(':').Append(item.Id).Append(':')
                .Append(Math.Round(item.Duration).ToString(CultureInfo.InvariantCulture)).Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>The fingerprint of lineup items as JSON, the form snapshots and <see cref="Tunarr.TunarrClient.GetLineupItemsJsonAsync"/> use.</summary>
    public static string Of(string lineupItemsJson) =>
        Of(JsonSerializer.Deserialize<List<LineupItem>>(lineupItemsJson, JsonSerializerOptions.Web) ?? []);
}
