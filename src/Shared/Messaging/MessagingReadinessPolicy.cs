namespace SupportPoc.Shared.Messaging;

/// <summary>
/// Readiness checks for the reliable messaging pipeline (Azure Service Bus cloud or emulator).
/// </summary>
public static class MessagingReadinessPolicy
{
    public sealed record PipelineReadinessResult(bool Ready, string Transport, string? Detail);

    public static async Task<PipelineReadinessResult> EvaluatePipelineAsync(
        ServiceBusOptions bus,
        CancellationToken cancellationToken = default)
    {
        if (bus.Enabled)
        {
            var sb = await ServiceBusReadiness.CheckTcpAsync(bus, cancellationToken);
            var transport = bus.IsDevelopmentEmulator ? "servicebus-emulator" : sb.Transport;
            return new PipelineReadinessResult(sb.Ready, transport, sb.Detail);
        }

        return new PipelineReadinessResult(
            false,
            "none",
            "ServiceBus.ConnectionString trong — cau hinh Azure Service Bus hoac Emulator (UseDevelopmentEmulator=true).");
    }
}
