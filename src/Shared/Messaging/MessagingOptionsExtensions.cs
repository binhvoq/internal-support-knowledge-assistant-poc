using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SupportPoc.Shared.Messaging;

public static class MessagingOptionsExtensions
{
    public static IServiceCollection AddSupportPocMessagingOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ServiceBusOptions>(configuration.GetSection(ServiceBusOptions.SectionName));
        services.Configure<LocalMessagingOptions>(configuration.GetSection(LocalMessagingOptions.SectionName));
        services.PostConfigure<LocalMessagingOptions>(options => options.ApplyEnvironmentOverrides());
        return services;
    }

    public static LocalMessagingOptions ResolveLocalMessaging(IConfiguration configuration)
    {
        var options = configuration.GetSection(LocalMessagingOptions.SectionName).Get<LocalMessagingOptions>()
            ?? new LocalMessagingOptions();
        options.ApplyEnvironmentOverrides();
        return options;
    }
}
