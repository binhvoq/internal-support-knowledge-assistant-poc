using Microsoft.Extensions.DependencyInjection;
using SupportPoc.Shared.Options;

namespace SupportPoc.Shared.Auth;

/// <summary>Kiem tra cau hinh/token outbound Entra cho HTTP client noi bo (MCP, Knowledge, debug).</summary>
public static class EntraOutboundReadinessPolicy
{
    public sealed record Result(bool Ready, string Detail);

    public static Result EvaluateConfig(AzureAdOptions options)
    {
        if (!options.Enabled)
            return new Result(true, "Entra disabled — outbound bearer khong bat buoc.");

        var clientId = options.McpClientId ?? options.ClientId;
        var secret = options.McpClientSecret ?? options.ClientSecret;
        if (string.IsNullOrWhiteSpace(options.TenantId))
            return new Result(false, "Thieu AzureAd.TenantId — HTTP outbound se bi 401.");
        if (string.IsNullOrWhiteSpace(clientId))
            return new Result(false, "Thieu AzureAd.McpClientId — HTTP outbound se bi 401.");
        if (string.IsNullOrWhiteSpace(secret))
            return new Result(false, "Thieu AzureAd.McpClientSecret — HTTP outbound se bi 401.");

        var audience = string.IsNullOrWhiteSpace(options.Audience) ? options.ClientId : options.Audience;
        if (string.IsNullOrWhiteSpace(audience))
            return new Result(false, "Thieu AzureAd.Audience — client credentials khong lay duoc token.");

        return new Result(true, "Entra outbound credentials configured.");
    }

    public static async Task<Result> EvaluateTokenAsync(
        IServiceProvider services,
        AzureAdOptions options,
        CancellationToken cancellationToken = default)
    {
        var config = EvaluateConfig(options);
        if (!options.Enabled || !config.Ready)
            return config;

        var provider = services.GetService<ClientCredentialsTokenProvider>();
        if (provider is null)
            return new Result(false, "ClientCredentialsTokenProvider chua dang ky — outbound bearer khong hoat dong.");

        try
        {
            var token = await provider.GetTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
                return new Result(false, "Client credentials tra ve token rong — HTTP outbound se bi 401.");

            return new Result(true, "Entra outbound token acquired.");
        }
        catch (Exception ex)
        {
            return new Result(false, $"Client credentials that bai: {ex.Message}");
        }
    }
}
