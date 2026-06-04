using System.Net.Http.Json;
using MassTransit;
using Microsoft.Extensions.Options;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Messaging;

namespace SupportPoc.AiOrchestrator.Services;

public interface IConsiderAutoSuggestionGateway
{
    Task<ConsiderAutoSuggestionGatewayResult> SendAsync(
        IConsiderAutoSuggestion request,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed record ConsiderAutoSuggestionGatewayResult(bool Accepted, string? RejectReason);

public sealed class MassTransitConsiderAutoSuggestionGateway(IBus bus) : IConsiderAutoSuggestionGateway
{
    private static readonly Uri ConsiderAddress = new("queue:consider-auto-suggestion");

    public async Task<ConsiderAutoSuggestionGatewayResult> SendAsync(
        IConsiderAutoSuggestion request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var client = bus.CreateRequestClient<IConsiderAutoSuggestion>(ConsiderAddress, timeout);
        var response = await client.GetResponse<IAutoSuggestionAccepted, IAutoSuggestionRejected>(
            request,
            cancellationToken);

        if (response.Is(out Response<IAutoSuggestionRejected>? rejected))
            return new ConsiderAutoSuggestionGatewayResult(false, rejected!.Message.Reason);

        return new ConsiderAutoSuggestionGatewayResult(true, null);
    }
}

public sealed class HttpConsiderAutoSuggestionGateway(
    IHttpClientFactory httpClientFactory,
    IOptions<LocalMessagingOptions> localOptions) : IConsiderAutoSuggestionGateway
{
    public const string HttpClientName = "ticket-service-consider-bridge";

    public async Task<ConsiderAutoSuggestionGatewayResult> SendAsync(
        IConsiderAutoSuggestion request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var baseUrl = localOptions.Value.TicketServiceBaseUrl.TrimEnd('/');
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var client = httpClientFactory.CreateClient(HttpClientName);
        var response = await client.PostAsJsonAsync(
            $"{baseUrl}/internal/dev/consider-auto-suggestion",
            request,
            cts.Token);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ConsiderBridgeResponse>(cancellationToken: cts.Token)
            ?? throw new InvalidOperationException("Consider bridge returned empty body.");

        return new ConsiderAutoSuggestionGatewayResult(body.Accepted, body.RejectReason);
    }

    private sealed record ConsiderBridgeResponse(bool Accepted, string? RejectReason);
}
