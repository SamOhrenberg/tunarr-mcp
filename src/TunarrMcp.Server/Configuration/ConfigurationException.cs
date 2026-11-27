using Microsoft.Extensions.Options;

namespace TunarrMcp.Server.Configuration;

/// <summary>A configuration mistake the operator has to fix. Shown at startup as a message, not a stack trace.</summary>
internal sealed class ConfigurationException(string message, Exception? innerException = null) : Exception(message, innerException);

internal static class StartupErrors
{
    /// <summary>
    /// The operator-facing message for a startup failure caused by configuration, or null for anything else
    /// (a real bug keeps its stack trace). ValidateOnStart reports several options types as an AggregateException.
    /// </summary>
    public static string? Describe(Exception exception)
    {
        var messages = Messages(exception)?.ToArray();
        return messages is null
            ? null
            : "Configuration error:" + string.Concat(messages.Select(m => "\n  - " + m));
    }

    private static IEnumerable<string>? Messages(Exception exception) => exception switch
    {
        ConfigurationException e => [e.Message],
        OptionsValidationException e => e.Failures,
        AggregateException e when e.InnerExceptions.Select(Messages).ToArray() is var inner && inner.All(m => m is not null) =>
            inner.SelectMany(m => m!),
        _ => null,
    };
}
