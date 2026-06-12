using MassTransit;
using SupportPoc.TicketService.Consumers;

namespace SupportPoc.TicketService.Tests;

/// <summary>Test harness consumer config — no retry delay, same queue name as production.</summary>
internal sealed class TestProposeTicketSuggestionConsumerDefinition
    : ConsumerDefinition<ProposeTicketSuggestionConsumer>
{
    public TestProposeTicketSuggestionConsumerDefinition() => EndpointName = "propose-ticket-suggestion";
}
