using MassTransit;
using SupportPoc.Shared.Contracts;
using SupportPoc.TicketService.Data;
using SupportPoc.TicketService.Services;

namespace SupportPoc.TicketService.Consumers;

/// <summary>DB-only: ap dung proposal neu ticket con suggestible.</summary>
public sealed class ConsiderAutoSuggestionConsumer : IConsumer<IConsiderAutoSuggestion>
{
    private readonly ConsiderAutoSuggestionApplier _applier;
    private readonly ILogger<ConsiderAutoSuggestionConsumer> _logger;

    public ConsiderAutoSuggestionConsumer(
        ConsiderAutoSuggestionApplier applier,
        ILogger<ConsiderAutoSuggestionConsumer> logger)
    {
        _applier = applier;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<IConsiderAutoSuggestion> context)
    {
        var msg = context.Message;
        var outcome = await _applier.ApplyAsync(msg, context.CancellationToken);

        if (outcome.Accepted)
        {
            await context.RespondAsync<IAutoSuggestionAccepted>(new AutoSuggestionAccepted(msg.JobId, msg.TicketId));
            return;
        }

        await context.RespondAsync<IAutoSuggestionRejected>(
            new AutoSuggestionRejected(msg.JobId, msg.TicketId, outcome.RejectReason ?? "Rejected"));
        await context.Publish<IAutoSuggestionDiscarded>(
            new AutoSuggestionDiscarded(msg.JobId, msg.TicketId, outcome.RejectReason ?? "Rejected"));
    }
}

public sealed class ConsiderAutoSuggestionConsumerDefinition : ConsumerDefinition<ConsiderAutoSuggestionConsumer>
{
    public ConsiderAutoSuggestionConsumerDefinition()
    {
        EndpointName = "consider-auto-suggestion";
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<ConsiderAutoSuggestionConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Intervals(100, 500, 1000, 2000));
        endpointConfigurator.UseEntityFrameworkOutbox<TicketDbContext>(context);
    }
}
