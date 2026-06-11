using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Consumers;
using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;
using SupportPoc.Shared.Testing;
using SupportPoc.TicketService.Consumers;
using SupportPoc.TicketService.Data;
using SupportPoc.TicketService.Services;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class TicketSuggestionSagaTests
{
    [Fact]
    public async Task Happy_path_completes_and_applies_suggestion()
    {
        var jobId = Guid.NewGuid();
        await using var provider = BuildFullProvider();
        await EnsureDatabasesAsync(provider);
        await SeedTicketAsync(provider, "TCK-OK", TicketStatus.New, version: 1);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        await harness.Bus.Publish<ITicketCreated>(Message(jobId, "TCK-OK", version: 1));

        Assert.True(await WaitForPublishedAsync<IAiSuggestionGenerated>(harness, m => m.JobId == jobId));

        await using var scope = provider.CreateAsyncScope();
        var ticket = await scope.ServiceProvider.GetRequiredService<TicketDbContext>().Tickets.FindAsync("TCK-OK");
        Assert.Equal(TicketStatus.Suggested, ticket!.Status);
        Assert.False(string.IsNullOrWhiteSpace(ticket.AiSuggestedAnswer));
        Assert.True(await harness.Published.Any<IAiSuggestionGenerated>(x => x.Context.Message.JobId == jobId));
    }

    [Fact]
    public async Task Rejects_when_agent_already_resolved()
    {
        var jobId = Guid.NewGuid();
        await using var provider = BuildFullProvider();
        await EnsureDatabasesAsync(provider);
        await SeedTicketAsync(provider, "TCK-REJ", TicketStatus.Resolved, finalAnswer: "done", version: 2);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        await harness.Bus.Publish<ITicketCreated>(Message(jobId, "TCK-REJ", version: 2));

        Assert.True(await WaitForPublishedAsync<IAutoSuggestionDiscarded>(harness, m => m.JobId == jobId));
    }

    [Fact]
    public async Task Ai_fail_eventually_fails_after_retries()
    {
        var jobId = Guid.NewGuid();
        await using var provider = BuildFullProvider(maxRetries: 0);
        await EnsureDatabasesAsync(provider);
        await SeedTicketAsync(provider, "TCK-FAIL", TicketStatus.New);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        await harness.Bus.Publish<ITicketCreated>(Message(jobId, "TCK-FAIL", FaultInjection.ForceAiFail));

        Assert.True(await WaitForPublishedAsync<IAutoSuggestionFailed>(harness, m => m.JobId == jobId));
    }

    [Fact]
    public async Task Late_attempt_result_is_ignored()
    {
        var jobId = Guid.NewGuid();
        await using var provider = BuildFullProvider();
        await EnsureDatabasesAsync(provider);
        await SeedTicketAsync(provider, "TCK-LATE", TicketStatus.New);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        await harness.Bus.Publish<ITicketCreated>(Message(jobId, "TCK-LATE"));

        Assert.True(await WaitForPublishedAsync<IAiSuggestionGenerated>(harness, m => m.JobId == jobId));

        var oldAttempt = Guid.NewGuid();
        await harness.Bus.Publish<ISuggestionGenerated>(new SuggestionGenerated(
            jobId,
            oldAttempt,
            jobId,
            "TCK-LATE",
            SupportCategory.IT,
            "late suggestion",
            []));

        await Task.Delay(500);
        Assert.True(await harness.Consumed.Any<ISuggestionGenerated>(
            x => x.Context.Message.SagaId == jobId && x.Context.Message.AttemptId == oldAttempt));

        var sagaHarness = harness.GetSagaStateMachineHarness<TicketSuggestionStateMachine, TicketSuggestionSaga>();
        Assert.NotNull(await sagaHarness.Exists(jobId, sagaHarness.StateMachine.Completed, TimeSpan.FromSeconds(5)));
        var audited = await sagaHarness.Match(
            s => s.CorrelationId == jobId && (s.LateMessageAudit ?? string.Empty).Contains("LateMessageIgnored"),
            TimeSpan.FromSeconds(5));
        Assert.NotEmpty(audited);
    }

    [Fact]
    public async Task Timeout_triggers_reconcile_and_retries_generation()
    {
        var jobId = Guid.NewGuid();
        await using var provider = BuildFullProvider(maxRetries: 1, stepTimeoutSeconds: 1);
        await EnsureDatabasesAsync(provider);
        await SeedTicketAsync(provider, "TCK-TMO", TicketStatus.New);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        await harness.Bus.Publish<ITicketCreated>(Message(jobId, "TCK-TMO", FaultInjection.ForceSkipGenerate));

        Assert.True(await WaitForPublishedAsync<IAutoSuggestionFailed>(harness, m => m.JobId == jobId, attempts: 200));

        var generateCount = 0;
        foreach (var ctx in harness.Sent.Select<IGenerateSuggestionRequested>())
        {
            if (ctx.Context.Message.JobId == jobId)
                generateCount++;
        }

        Assert.True(generateCount >= 2);
    }

    private static ITicketCreated Message(Guid jobId, string ticketId, string? marker = null, long version = 1) =>
        new TicketCreated(jobId, ticketId, "EMP-1", $"VPN help {marker ?? ""}".Trim(), SupportCategory.IT, version);

    private static ServiceProvider BuildFullProvider(int maxRetries = 2, int stepTimeoutSeconds = 2)
    {
        MapProposeEndpoint();
        var dbKey = Guid.NewGuid().ToString("N");
        var orchDbPath = Path.Combine(Path.GetTempPath(), $"orch-saga-{dbKey}.db");
        var ticketDbPath = Path.Combine(Path.GetTempPath(), $"ticket-saga-{dbKey}.db");

        var services = new ServiceCollection()
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddDbContext<OrchestratorDbContext>((_, o) => o.UseSqlite($"Data Source={orchDbPath}"))
            .AddDbContext<TicketDbContext>((_, o) => o.UseSqlite($"Data Source={ticketDbPath}"))
            .AddSingleton(Microsoft.Extensions.Options.Options.Create(new AzureOpenAIOptions { ChatEnabled = false }))
            .AddSingleton(Microsoft.Extensions.Options.Options.Create(new ServiceEndpointsOptions { KnowledgeService = "http://127.0.0.1:1" }));

        services.AddHttpClient(KnowledgeSearchClient.HttpClientName);
        services
            .AddSingleton<KnowledgeSearchClient>()
            .AddSingleton<IKnowledgeSearchClient>(sp => sp.GetRequiredService<KnowledgeSearchClient>())
            .AddScoped<IChatCompletionServiceAccessor>()
            .AddScoped<AiPipelineService>()
            .AddScoped<IAiPipelineService>(sp => sp.GetRequiredService<AiPipelineService>())
            .AddScoped<ProposeTicketSuggestionApplier>()
            .AddScoped<ITicketSnapshotClient, DbTicketSnapshotClient>();

        services.AddOptions<AutoSuggestionOptions>()
            .Configure(o =>
            {
                o.StepTimeoutSeconds = stepTimeoutSeconds;
                o.ProposeRequestTimeoutSeconds = 10;
                o.MaxGenerationRetries = maxRetries;
            });

        services.AddMassTransitTestHarness(cfg =>
        {
            cfg.SetTestTimeouts(TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(250));
            cfg.AddDelayedMessageScheduler();
            cfg.AddSagaStateMachine<TicketSuggestionStateMachine, TicketSuggestionSaga>()
                .InMemoryRepository();
            cfg.AddConsumer<GenerateSuggestionRequestedConsumer>()
                .Endpoint(e => e.Name = "generate-suggestion-requested");
            cfg.AddConsumer<ProposeTicketSuggestionConsumer>()
                .Endpoint(e => e.Name = "propose-ticket-suggestion");
            cfg.UsingInMemory((context, bus) =>
            {
                bus.UseDelayedMessageScheduler();
                bus.ConfigureEndpoints(context);
            });
        });

        return services.BuildServiceProvider(true);
    }

    private static void MapProposeEndpoint() =>
        EndpointConvention.Map<IProposeTicketSuggestion>(new Uri("queue:propose-ticket-suggestion"));

    private static async Task EnsureDatabasesAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var orch = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await orch.Database.EnsureCreatedAsync();
        await OrchestratorDbSchema.EnsureSchemaAsync(orch);
        var ticket = scope.ServiceProvider.GetRequiredService<TicketDbContext>();
        await ticket.Database.EnsureCreatedAsync();
    }

    private static async Task SeedTicketAsync(
        ServiceProvider provider,
        string id,
        string status,
        string? finalAnswer = null,
        long version = 1)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Tickets.Add(new TicketEntity
        {
            Id = id,
            EmployeeId = "EMP-1",
            Category = SupportCategory.IT,
            Question = "VPN",
            Status = status,
            FinalAnswer = finalAnswer,
            Version = version,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static async Task<bool> WaitForPublishedAsync<T>(
        ITestHarness harness,
        Func<T, bool> filter,
        int attempts = 120)
        where T : class
    {
        for (var i = 0; i < attempts; i++)
        {
            if (await harness.Published.Any<T>(x => filter(x.Context.Message)))
                return true;
            await Task.Delay(250);
        }

        return false;
    }

    private sealed class DbTicketSnapshotClient(TicketDbContext db) : ITicketSnapshotClient
    {
        public async Task<TicketSnapshot?> GetTicketAsync(string ticketId, CancellationToken cancellationToken = default)
        {
            var ticket = await db.Tickets.FindAsync([ticketId], cancellationToken);
            if (ticket is null)
                return null;
            return new TicketSnapshot(
                ticket.Id,
                ticket.Status,
                ticket.Version,
                !string.IsNullOrWhiteSpace(ticket.AiSuggestedAnswer),
                !string.IsNullOrWhiteSpace(ticket.FinalAnswer));
        }
    }
}
