namespace TunarrMcp.Server.Shaping;

/// <summary>
/// Every time a tool returns is in the server's local zone (TZ in the container), so Claude can say "8 pm"
/// without converting. The offset is kept, so the value stays unambiguous.
/// </summary>
internal static class LocalTime
{
    public static DateTimeOffset ToLocal(this DateTimeOffset value, TimeProvider clock) =>
        TimeZoneInfo.ConvertTime(value, clock.LocalTimeZone);

    public static DateTimeOffset? ToLocal(this DateTimeOffset? value, TimeProvider clock) =>
        value?.ToLocal(clock);
}
