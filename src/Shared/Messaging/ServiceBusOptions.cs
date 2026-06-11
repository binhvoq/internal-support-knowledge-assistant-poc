namespace SupportPoc.Shared.Messaging;

public sealed class ServiceBusOptions
{
    public const string SectionName = "ServiceBus";
    public const string DevelopmentEmulatorFlag = "UseDevelopmentEmulator=true";
    public const int EmulatorManagementPort = 5300;

    public string? ConnectionString { get; set; }
    public string TopicName { get; set; } = "support-events";
    public bool Enabled => !string.IsNullOrWhiteSpace(ConnectionString);

    public bool IsDevelopmentEmulator =>
        Enabled
        && ConnectionString!.Contains(DevelopmentEmulatorFlag, StringComparison.OrdinalIgnoreCase);

    public sealed record EndpointInfo(string Host, int? Port);

    public bool TryGetEndpoint(out EndpointInfo endpoint) =>
        TryParseEndpoint(ConnectionString, out endpoint);

    /// <summary>
    /// MassTransit AMQP runtime. Port 5300 on the emulator is HTTP management only — use sb://localhost for messaging.
    /// </summary>
    public string? GetMessagingConnectionString()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
            return ConnectionString;

        if (!IsDevelopmentEmulator || !TryParseEndpoint(ConnectionString, out var endpoint))
            return ConnectionString;

        if (!IsLocalEmulatorHost(endpoint.Host))
            return ConnectionString;

        if (endpoint.Port is null or EmulatorManagementPort)
            return ReplaceEndpointHost(ConnectionString, endpoint.Host);

        return ConnectionString;
    }

    /// <summary>
    /// Administration Client (debug/DLQ) uses management port 5300 on the local emulator.
    /// </summary>
    public string? GetAdministrationConnectionString()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString) || !IsDevelopmentEmulator)
            return ConnectionString;

        if (!TryParseEndpoint(ConnectionString, out var endpoint))
            return ConnectionString;

        if (!IsLocalEmulatorHost(endpoint.Host))
            return ConnectionString;

        if (endpoint.Port == EmulatorManagementPort)
            return ConnectionString;

        return ReplaceEndpointHostPort(ConnectionString, endpoint.Host, EmulatorManagementPort);
    }

    private static string ReplaceEndpointHost(string connectionString, string host)
    {
        var segments = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            if (!segments[i].StartsWith("Endpoint=", StringComparison.OrdinalIgnoreCase))
                continue;

            segments[i] = $"Endpoint=sb://{host}";
            return string.Join(';', segments) + ";";
        }

        return connectionString;
    }

    public static bool TryParseEndpoint(string? connectionString, out EndpointInfo endpoint)
    {
        endpoint = new EndpointInfo(string.Empty, null);
        if (string.IsNullOrWhiteSpace(connectionString))
            return false;

        var endpointValue = ExtractEndpointValue(connectionString);
        if (string.IsNullOrWhiteSpace(endpointValue))
            return false;

        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            return false;

        int? port = uri.Port > 0 ? uri.Port : null;
        endpoint = new EndpointInfo(uri.Host, port);
        return true;
    }

    internal static bool TryGetHost(string connectionString, out string host, out bool hasExplicitPort)
    {
        host = string.Empty;
        hasExplicitPort = false;
        if (!TryParseEndpoint(connectionString, out var endpoint))
            return false;

        host = endpoint.Host;
        hasExplicitPort = endpoint.Port.HasValue;
        return !string.IsNullOrWhiteSpace(host);
    }

    internal static bool IsLocalEmulatorHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase);

    internal static int ResolveEmulatorHealthPort(EndpointInfo endpoint) =>
        endpoint.Port == EmulatorManagementPort ? EmulatorManagementPort : EmulatorManagementPort;

    private static string? ExtractEndpointValue(string connectionString)
    {
        foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (segment.StartsWith("Endpoint=", StringComparison.OrdinalIgnoreCase))
                return segment["Endpoint=".Length..];
        }

        return null;
    }

    private static string ReplaceEndpointHostPort(string connectionString, string host, int port)
    {
        const string prefix = "Endpoint=";
        var segments = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            if (!segments[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            segments[i] = $"{prefix}sb://{host}:{port}";
            return string.Join(';', segments) + ";";
        }

        return connectionString;
    }
}
