using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace SupportPoc.Shared.Auth;

/// <summary>Bat buoc Entra ID o Production; cho phep bypass ro rang cho Docker/local test.</summary>
public static class ProductionSecurityGuard
{
    public const string AllowInsecureEnvVar = "SUPPORTPOC_ALLOW_INSECURE_PRODUCTION";

    public static void Validate(IHostEnvironment environment, IConfiguration configuration)
    {
        if (IsSecure(environment, configuration))
            return;

        throw new InvalidOperationException(
            "Production requires Entra ID authentication (AzureAd:Enabled=true). " +
            "Debug and ops endpoints must not be public in Production. " +
            $"For local Docker testing only, set environment variable {AllowInsecureEnvVar}=true.");
    }

    public static bool IsSecure(IHostEnvironment environment, IConfiguration configuration) =>
        environment.IsDevelopment()
        || configuration.IsEntraEnabled()
        || IsInsecureBypassEnabled();

    public static bool IsInsecureBypassEnabled() =>
        string.Equals(
            Environment.GetEnvironmentVariable(AllowInsecureEnvVar),
            "true",
            StringComparison.OrdinalIgnoreCase);
}
