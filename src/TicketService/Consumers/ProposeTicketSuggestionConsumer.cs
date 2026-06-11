using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using SupportPoc.Shared.Contracts;
using SupportPoc.TicketService.Data;
using SupportPoc.TicketService.Services;

namespace SupportPoc.TicketService.Consumers;

/// <summary>Final gate: ap dung proposal neu ticket con suggestible. Chi respond — saga publish audit events.</summary>
public sealed class ProposeTicketSuggestionConsumer : IConsumer<IProposeTicketSuggestion>
{
    private readonly ProposeTicketSuggestionApplier _applier;

    public ProposeTicketSuggestionConsumer(ProposeTicketSuggestionApplier applier) => _applier = applier;

    public async Task Consume(ConsumeContext<IProposeTicketSuggestion> context)
    {
        var msg = context.Message;
        var outcome = await _applier.ApplyAsync(msg, context.CancellationToken);

        await context.RespondAsync<IProposeTicketSuggestionResult>(
            new ProposeTicketSuggestionResult(
                msg.CommandId,
                msg.SagaId,
                msg.AttemptId,
                msg.JobId,
                msg.TicketId,
                outcome.Accepted,
                outcome.RejectReason));
    }
}

public sealed class ProposeTicketSuggestionConsumerDefinition : ConsumerDefinition<ProposeTicketSuggestionConsumer>
{
    public ProposeTicketSuggestionConsumerDefinition()
    {
        EndpointName = "propose-ticket-suggestion";
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<ProposeTicketSuggestionConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Intervals(100, 500, 1000, 2000));
        endpointConfigurator.UseEntityFrameworkOutbox<TicketDbContext>(context);
    }
}
