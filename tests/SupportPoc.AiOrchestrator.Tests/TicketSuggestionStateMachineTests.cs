using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class TicketSuggestionStateMachineTests
{
    [Fact]
    public async Task Failed_is_reached_only_after_ticket_revert_is_confirmed()
    {
        MapEndpointConvention<IMarkTicketAnalyzing>("queue:mark-ticket-analyzing");
        MapEndpointConvention<ICompensateMarkAnalyzing>("queue:compensate-mark-analyzing");

        var sagaId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        const string ticketId = "TCK-SAGA-STATE";

        await using var provider = new ServiceCollection()
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddSingleton(Microsoft.Extensions.Options.Options.Create(new SagaTimeoutOptions
            {
                Analyzing = new SagaStepTimeoutOptions { TimeoutSeconds = 30 },
                RunningAi = new SagaStepTimeoutOptions { TimeoutSeconds = 30 },
                Saving = new SagaStepTimeoutOptions { TimeoutSeconds = 30 },
                Compensating = new SagaStepTimeoutOptions { TimeoutSeconds = 30 }
            }))
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.SetTestTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(200));
                cfg.AddSagaStateMachine<TicketSuggestionStateMachine, TicketSuggestionState>()
                    .InMemoryRepository();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            var saga = harness.GetSagaStateMachineHarness<TicketSuggestionStateMachine, TicketSuggestionState>();
            var machine = provider.GetRequiredService<TicketSuggestionStateMachine>();

            await harness.Bus.Publish<ITicketCreated>(
                new TicketCreated(sagaId, ticketId, "EMP-1", "VPN issue", SupportCategory.IT, 1));

            Assert.NotNull(await saga.Exists(sagaId, machine.Analyzing));

            await harness.Bus.Publish<ITicketAnalyzingMarkFailed>(
                new TicketAnalyzingMarkFailed(sagaId, ticketId, "TicketService rejected mark"));

            Assert.NotNull(await saga.Exists(sagaId, machine.RevertingBeforeFailed));
            Assert.Null(await saga.Exists(sagaId, machine.Failed));
            Assert.True(await harness.Sent.Any<ICompensateMarkAnalyzing>(x =>
                x.Context.Message.CorrelationId == sagaId &&
                x.Context.Message.TicketId == ticketId &&
                x.Context.Message.OriginalStatus == TicketStatus.New &&
                !string.IsNullOrWhiteSpace(x.Context.Message.SagaStopNote)));

            await harness.Bus.Publish<IMarkAnalyzingReverted>(
                new MarkAnalyzingReverted(sagaId, ticketId));

            Assert.NotNull(await saga.Exists(sagaId, machine.Failed));
        }
        finally
        {
            await harness.Stop();
        }
    }

    private static void MapEndpointConvention<T>(string address)
        where T : class
    {
        try
        {
            EndpointConvention.Map<T>(new Uri(address));
        }
        catch (ArgumentException)
        {
            // EndpointConvention is process-wide; another test may have mapped it already.
        }
    }
}
