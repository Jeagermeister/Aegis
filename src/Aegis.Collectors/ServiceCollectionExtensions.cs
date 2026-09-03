using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aegis.Collectors;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the collector host. Sources come from the <c>Collectors</c> section and the store
    /// from <c>ConnectionStrings:Aegis</c> (or <c>Collectors:StoreConnectionString</c>). With no
    /// sources configured the host logs a warning and idles, so the API can run without any.
    /// </summary>
    public static IServiceCollection AddAegisCollectors(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CollectorOptions>()
            .Bind(configuration.GetSection(CollectorOptions.SectionName))
            .PostConfigure(options =>
            {
                if (string.IsNullOrWhiteSpace(options.StoreConnectionString))
                {
                    options.StoreConnectionString = configuration.GetConnectionString("Aegis") ?? string.Empty;
                }
            });

        services.AddHttpClient();
        services.AddHostedService<CollectorHostedService>();
        services.AddHostedService<StaleCollectorSweepService>();

        return services;
    }
}
