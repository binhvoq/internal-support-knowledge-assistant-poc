using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupportPoc.Shared.Options;

namespace SupportPoc.Shared.Telemetry;

public static class TelemetryExtensions
{
    public static IServiceCollection AddSupportPocApplicationInsights(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(ApplicationInsightsOptions.SectionName);
        services.Configure<ApplicationInsightsOptions>(section);

        var options = section.Get<ApplicationInsightsOptions>() ?? new ApplicationInsightsOptions();
        if (!options.Enabled)
            return services;

        services.AddApplicationInsightsTelemetry(o =>
        {
            o.ConnectionString = options.ConnectionString;
        });

        return services;
    }

    public static TelemetryClient? TryGetTelemetryClient(this IServiceProvider services) =>
        services.GetService<TelemetryClient>();
}
