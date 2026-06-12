using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace SupportPoc.Shared.Auth;

public static class EndpointSecurityExtensions
{
    /// <summary>
    /// Debug/ops endpoints: anonymous chi trong Development hoac khi bypass Production ro rang.
    /// Non-Development khac: bat buoc Entra Service role.
    /// </summary>
    public static RouteHandlerBuilder WithDebugOrServicePolicy(
        this RouteHandlerBuilder builder,
        bool entraEnabled,
        IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
            return builder.AllowAnonymous();

        if (entraEnabled)
            return builder.RequireAuthorization(PolicyNames.Service);

        if (ProductionSecurityGuard.IsInsecureBypassEnabled())
            return builder.AllowAnonymous();

        return builder.RequireAuthorization(PolicyNames.Service);
    }

    public static RouteHandlerBuilder WithUserFacingPolicy(
        this RouteHandlerBuilder builder,
        bool entraEnabled,
        IHostEnvironment environment,
        string policy)
    {
        if (environment.IsDevelopment())
            return builder.AllowAnonymous();

        if (entraEnabled)
            return builder.RequireAuthorization(policy);

        if (ProductionSecurityGuard.IsInsecureBypassEnabled())
            return builder.AllowAnonymous();

        return builder.RequireAuthorization(policy);
    }
}
