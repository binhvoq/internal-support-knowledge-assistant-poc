using System.Net;
using System.Net.Sockets;

namespace SupportPoc.Shared.Messaging;

public static class ServiceBusReadiness
{
    public sealed record Result(bool Ready, string Transport, string? Detail);

    public static Result Check(ServiceBusOptions options)
    {
        if (!options.Enabled)
            return new Result(true, "inmemory", "ServiceBus.ConnectionString empty — MassTransit in-memory (single process only).");

        if (!TryGetHost(options.ConnectionString, out var host))
            return new Result(false, "servicebus", "ConnectionString khong parse duoc host.");

        try
        {
            var addresses = Dns.GetHostAddresses(host);
            if (addresses.Length == 0)
                return new Result(false, "servicebus", $"DNS khong tra ve dia chi cho {host}.");

            return new Result(true, "servicebus", $"Host {host} resolvable.");
        }
        catch (Exception ex)
        {
            return new Result(false, "servicebus", $"Khong resolve duoc {host}: {ex.Message}");
        }
    }

    public static async Task<Result> CheckTcpAsync(ServiceBusOptions options, CancellationToken cancellationToken = default)
    {
        var dns = Check(options);
        if (!dns.Ready || !options.Enabled)
            return dns;

        if (!TryGetHost(options.ConnectionString, out var host))
            return new Result(false, "servicebus", "ConnectionString khong parse duoc host.");

        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, 443, cancellationToken).AsTask();
            if (await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken)) != connectTask)
                return new Result(false, "servicebus", $"Timeout ket noi TCP 443 toi {host}.");

            await connectTask;
            return new Result(true, "servicebus", $"TCP 443 toi {host} OK.");
        }
        catch (Exception ex)
        {
            return new Result(false, "servicebus", $"Khong ket noi duoc {host}:443 — {ex.Message}");
        }
    }

    private static bool TryGetHost(string? connectionString, out string host)
    {
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(connectionString))
            return false;

        const string marker = "sb://";
        var start = connectionString.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return false;

        start += marker.Length;
        var end = connectionString.IndexOf('/', start);
        if (end < 0)
            end = connectionString.IndexOf(';', start);
        if (end < 0)
            end = connectionString.Length;

        host = connectionString[start..end].Trim();
        return !string.IsNullOrWhiteSpace(host);
    }
}
