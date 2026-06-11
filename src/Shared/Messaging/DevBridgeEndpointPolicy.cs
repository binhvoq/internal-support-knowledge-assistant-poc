using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using System.Net;

namespace SupportPoc.Shared.Messaging;

/// <summary>
/// HTTP dev bridge — Development only, best-effort debug shortcut. Not the Outbox/reliable path.
/// </summary>
public static class DevBridgeEndpointPolicy
{
    public static bool ShouldUseHttpBridge(
        IHostEnvironment env,
        LocalMessagingOptions local,
        ServiceBusOptions bus) =>
        env.IsDevelopment() && local.HttpBridgeEnabled && !bus.Enabled;

    public static bool IsEnabled(IHostEnvironment env, LocalMessagingOptions local, ServiceBusOptions bus) =>
        ShouldUseHttpBridge(env, local, bus);

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
    /// Reliable path: Service Bus (cloud or emulator). HTTP bridge is optional debug only.
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
            var transport = bus.IsDevelopmentEmulator ? "servicebus-emulator" : sb.Transport;
            return new PipelineReadinessResult(sb.Ready, transport, sb.Detail, false);
        }

        if (IsEnabled(env, local, bus))
        {
            return new PipelineReadinessResult(
                true,
                "http-bridge",
                "DEBUG ONLY: Development + USE_HTTP_BRIDGE/HttpBridgeEnabled + ServiceBus off — best-effort HTTP bridge, khong co Outbox guarantee.",
                true);
        }

        var detail = local.HttpBridgeEnabled && !env.IsDevelopment()
            ? "HttpBridgeEnabled/USE_HTTP_BRIDGE bat nhung khong phai Development — endpoint bridge khong duoc map."
            : "ServiceBus.ConnectionString trong — cau hinh Azure Service Bus hoac Emulator (UseDevelopmentEmulator=true). HTTP bridge chi dung khi co y bat USE_HTTP_BRIDGE=true.";

        return new PipelineReadinessResult(false, "none", detail, local.HttpBridgeEnabled);
    }
}
