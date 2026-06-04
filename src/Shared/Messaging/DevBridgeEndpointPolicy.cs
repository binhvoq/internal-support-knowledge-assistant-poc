using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using System.Net;

namespace SupportPoc.Shared.Messaging;

/// <summary>Chinh sach endpoint HTTP dev bridge — chi Development + bridge bat + khong dung Service Bus.</summary>
public static class DevBridgeEndpointPolicy
{
    public static bool IsEnabled(IHostEnvironment env, LocalMessagingOptions local, ServiceBusOptions bus) =>
        env.IsDevelopment() && local.HttpBridgeEnabled && !bus.Enabled;

    public static bool IsLocalCaller(HttpContext httpContext)
    {
        var remote = httpContext.Connection.RemoteIpAddress;
        if (remote is null)
            return false;

        if (IPAddress.IsLoopback(remote))
            return true;

        // IPv4-mapped loopback (::ffff:127.0.0.1)
        return remote.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remote.MapToIPv4());
    }

    public static IResult RejectDisabled() => Results.NotFound();

    public sealed record PipelineReadinessResult(bool Ready, string Transport, string? Detail, bool HttpBridge);

    /// <summary>
    /// Cung dieu kien map endpoint dev bridge. In-memory (ServiceBus off, bridge off) = not ready cho cross-service.
    /// </summary>
    public static async Task<PipelineReadinessResult> EvaluatePipelineAsync(
        IHostEnvironment env,
        ServiceBusOptions bus,
        LocalMessagingOptions local,
        CancellationToken cancellationToken = default)
    {
        if (bus.Enabled)
        {
            var sb = await ServiceBusReadiness.CheckTcpAsync(bus, cancellationToken);
            return new PipelineReadinessResult(sb.Ready, sb.Transport, sb.Detail, false);
        }

        if (IsEnabled(env, local, bus))
        {
            return new PipelineReadinessResult(
                true,
                "http-bridge",
                "Development + HttpBridgeEnabled + ServiceBus off — dev bridge endpoints available.",
                true);
        }

        var detail = local.HttpBridgeEnabled && !env.IsDevelopment()
            ? "HttpBridgeEnabled=true nhung khong phai Development — endpoint bridge khong duoc map."
            : "ServiceBus off va HTTP bridge khong bat — in-memory chi single-process, pipeline cross-service khong chay.";

        return new PipelineReadinessResult(false, "none", detail, local.HttpBridgeEnabled);
    }
}
