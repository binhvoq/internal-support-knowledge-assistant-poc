namespace SupportPoc.AiOrchestrator.Saga.Timeouts.Probes;

public interface ITicketProgressProbe
{
    Task<TicketProgressProbeResult> GetAsync(string ticketId, CancellationToken cancellationToken);
}
