namespace SupportPoc.AiOrchestrator.Tests.TestSupport;

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        _respond = respond;

    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_respond(request));
    }
}

internal sealed class NamedHttpClientFactory : IHttpClientFactory
{
    private readonly Dictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);

    public void Register(string name, HttpClient client) => _clients[name] = client;

    public HttpClient CreateClient(string name) =>
        _clients.TryGetValue(name, out var client)
            ? client
            : throw new InvalidOperationException($"HttpClient '{name}' chua duoc dang ky trong test.");
}
