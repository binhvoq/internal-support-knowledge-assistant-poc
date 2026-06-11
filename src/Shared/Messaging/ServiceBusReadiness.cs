using System.Net;
using System.Net.Sockets;

namespace SupportPoc.Shared.Messaging;

public static class ServiceBusReadiness
{
    public sealed record Result(bool Ready, string Transport, string? Detail);

    public static Result Check(ServiceBusOptions options)
    {
        if (!options.Enabled)
            return new Result(
                false,
                "none",
                "ServiceBus.ConnectionString empty — reliable cross-service messaging requires Azure Service Bus or the local emulator.");

        if (!options.TryGetEndpoint(out var endpoint))
            return new Result(false, "servicebus", "ConnectionString khong parse duoc Endpoint (sb://host[:port]).");

        if (options.IsDevelopmentEmulator && IsLocalEmulatorHost(endpoint.Host))
            return new Result(true, "servicebus-emulator", $"Emulator endpoint {endpoint.Host}:{endpoint.Port?.ToString() ?? "default"}.");

        try
        {
            var addresses = Dns.GetHostAddresses(endpoint.Host);
            if (addresses.Length == 0)
                return new Result(false, "servicebus", $"DNS khong tra ve dia chi cho {endpoint.Host}.");

            return new Result(true, "servicebus", $"Host {endpoint.Host} resolvable.");
        }
        catch (Exception ex)
        {
            return new Result(false, "servicebus", $"Khong resolve duoc {endpoint.Host}: {ex.Message}");
        }
    }

    public static async Task<Result> CheckTcpAsync(ServiceBusOptions options, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
            return Check(options);

        if (!options.TryGetEndpoint(out var endpoint))
            return new Result(false, "servicebus", "ConnectionString khong parse duoc Endpoint (sb://host[:port]).");

        if (options.IsDevelopmentEmulator && IsLocalEmulatorHost(endpoint.Host))
            return await CheckEmulatorAsync(endpoint, cancellationToken);

        var dns = Check(options);
        if (!dns.Ready)
            return dns;

        return await ProbeTcpAsync(endpoint.Host, [443], "servicebus", cancellationToken);
    }

    private static async Task<Result> CheckEmulatorAsync(
        ServiceBusOptions.EndpointInfo endpoint,
        CancellationToken cancellationToken)
    {
        var healthPort = ServiceBusOptions.ResolveEmulatorHealthPort(endpoint);
        var healthUrl = $"http://{endpoint.Host}:{healthPort}/health";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await http.GetAsync(healthUrl, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new Result(
                    true,
                    "servicebus-emulator",
                    $"Emulator health OK ({healthUrl}).");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fall through to TCP probes.
        }

        var ports = new List<int> { ServiceBusOptions.EmulatorManagementPort, 5672, 5673 };
        if (endpoint.Port is > 0 && !ports.Contains(endpoint.Port.Value))
            ports.Insert(0, endpoint.Port.Value);

        var tcp = await ProbeTcpAsync(endpoint.Host, ports, "servicebus-emulator", cancellationToken);
        if (tcp.Ready)
            return tcp;

        return new Result(
            false,
            "servicebus-emulator",
            $"{tcp.Detail} Khoi dong Azure Service Bus Emulator (Docker) — health: {healthUrl}.");
    }

    private static async Task<Result> ProbeTcpAsync(
        string host,
        IEnumerable<int> ports,
        string transport,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (var port in ports.Distinct())
        {
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(host, port, cancellationToken).AsTask();
                if (await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken)) != connectTask)
                {
                    lastError = new TimeoutException($"Timeout ket noi TCP {port} toi {host}.");
                    continue;
                }

                await connectTask;
                return new Result(true, transport, $"TCP {host}:{port} OK.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }
        }

        return new Result(
            false,
            transport,
            $"Khong ket noi duoc {host} tren port [{string.Join(", ", ports)}] — {lastError?.Message}.");
    }

    private static bool IsLocalEmulatorHost(string host) =>
        ServiceBusOptions.IsLocalEmulatorHost(host);
}
