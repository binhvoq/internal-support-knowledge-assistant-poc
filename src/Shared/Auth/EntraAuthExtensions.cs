using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Web;
using SupportPoc.Shared.Options;

namespace SupportPoc.Shared.Auth;

public static class EntraAuthExtensions
{
    public static bool IsEntraEnabled(this IConfiguration configuration) =>
        configuration.GetSection(AzureAdOptions.SectionName).GetValue<bool>(nameof(AzureAdOptions.Enabled));

    public static IServiceCollection AddSupportPocEntraAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(AzureAdOptions.SectionName);
        services.Configure<AzureAdOptions>(section);

        var azureAd = section.Get<AzureAdOptions>() ?? new AzureAdOptions();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApi(section);

        services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwtOptions =>
        {
            var clientId = azureAd.ClientId;
            var audience = string.IsNullOrWhiteSpace(azureAd.Audience) ? clientId : azureAd.Audience;
            jwtOptions.TokenValidationParameters.ValidAudiences =
            [
                audience,
                clientId,
                $"api://{clientId}",
            ];
        });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(PolicyNames.EmployeeOrAbove, policy =>
                policy.RequireRole(AppRoleNames.UserRoles));

            options.AddPolicy(PolicyNames.Agent, policy =>
                policy.RequireRole(AppRoleNames.Agent));

            options.AddPolicy(PolicyNames.KnowledgeAdmin, policy =>
                policy.RequireRole(AppRoleNames.KnowledgeAdmin));

            options.AddPolicy(PolicyNames.Service, policy =>
                policy.RequireRole(AppRoleNames.Service));

            options.AddPolicy(PolicyNames.AgentOrService, policy =>
                policy.RequireRole(AppRoleNames.Agent, AppRoleNames.Service));

            options.AddPolicy(PolicyNames.UserOrService, policy =>
                policy.RequireAssertion(ctx =>
                {
                    if (ctx.User.Identity?.IsAuthenticated != true)
                        return false;
                    if (ctx.User.IsInRole(AppRoleNames.Service))
                        return true;
                    return AppRoleNames.UserRoles.Any(ctx.User.IsInRole);
                }));
        });

        return services;
    }

    public static IServiceCollection AddSupportPocClientCredentials(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AzureAdOptions>(configuration.GetSection(AzureAdOptions.SectionName));
        services.AddHttpClient();
        services.AddSingleton<ClientCredentialsTokenProvider>();
        services.AddTransient<EntraBearerTokenHandler>();
        services.AddHttpClient("entra-outbound").AddHttpMessageHandler<EntraBearerTokenHandler>();
        return services;
    }

    public static WebApplication UseSupportPocEntraAuth(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }

    public static RouteHandlerBuilder WithEntraPolicy(this RouteHandlerBuilder builder, bool enabled, string policy) =>
        enabled ? builder.RequireAuthorization(policy) : builder;
}
