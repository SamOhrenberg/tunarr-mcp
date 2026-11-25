using Microsoft.Extensions.Options;

namespace TunarrMcp.Server.Configuration;

internal static class ConfigurationServiceCollectionExtensions
{
    /// <summary>
    /// Binds every options section and validates it at startup, so bad config fails fast instead of on first request.
    /// ValidateOnStart also surfaces binder errors, such as an unknown Tools:Mode value.
    /// </summary>
    public static IServiceCollection AddTunarrMcpOptions(this IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<McpOptions>, McpOptionsValidator>();
        services.AddSingleton<IValidateOptions<TunarrOptions>, TunarrOptionsValidator>();
        services.AddSingleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>();
        services.AddSingleton<IValidateOptions<DataOptions>, DataOptionsValidator>();

        services.AddOptions<McpOptions>().BindConfiguration(McpOptions.Section).ValidateOnStart();
        services.AddOptions<ToolsOptions>().BindConfiguration(ToolsOptions.Section).ValidateOnStart();
        services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.Section).ValidateOnStart();
        services.AddOptions<DataOptions>().BindConfiguration(DataOptions.Section).ValidateOnStart();
        services.AddOptions<TunarrOptions>().BindConfiguration(TunarrOptions.Section).ValidateOnStart()
            .PostConfigure(o =>
            {
                // HttpClient drops the last path segment of a base address without a trailing slash.
                if (o.BaseUrl.IsAbsoluteUri && !o.BaseUrl.AbsolutePath.EndsWith('/'))
                    o.BaseUrl = new Uri(o.BaseUrl.AbsoluteUri + "/");
            });

        return services;
    }
}
