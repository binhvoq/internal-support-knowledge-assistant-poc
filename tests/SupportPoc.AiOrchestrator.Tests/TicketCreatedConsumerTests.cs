using MassTransit;
using MassTransit.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Consumers;
using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;
using SupportPoc.Shared.Testing;
using SupportPoc.TicketService.Consumers;
using SupportPoc.TicketService.Data;
using SupportPoc.TicketService.Services;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class TicketCreatedConsumerTests
{
    [Fact]
    public async Task Duplicate_message_on_terminal_job_is_ignored()
    {
        var jobId = Guid.NewGuid();
        await using var provider = BuildOrchestratorProvider();
        await SeedOrchestratorJobAsync(provider, jobId, "TCK-DUP", AutoSuggestionJobStatus.Completed);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        await harness.Bus.Publish<ITicketCreated>(Message(jobId, "TCK-DUP"));
        Assert.True(await harness.Consumed.Any<ITicketCreated>(x => x.Context.Message.JobId == jobId));

        await using var scope = provider.CreateAsyncScope();
        var job = await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>()
            .AutoSuggestionJobs.FindAsync(jobId);
        Assert.Equal(AutoSuggestionJobStatus.Completed, job!.Status);
        Assert.False(await harness.Published.Any<IAiSuggestionGenerated>());
    }

    [Fact]
    public async Task Ai_fail_marks_job_failed_and_publishes_event()
    {
        var jobId = Guid.NewGuid();
        await using var provider = BuildOrchestratorProvider();
        await EnsureOrchestratorDbAsync(provider);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish<ITicketCreated>(Message(jobId, "TCK-FAIL", FaultInjection.ForceAiFail));
        Assert.True(await harness.Consumed.Any<ITicketCreated>(x => x.Context.Message.JobId == jobId));

        await using var scope = provider.CreateAsyncScope();
        var job = await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>()
            .AutoSuggestionJobs.FindAsync(jobId);
        Assert.Equal(AutoSuggestionJobStatus.Failed, job!.Status);
        Assert.Contains("Simulated AI", job.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(await harness.Published.Any<IAutoSuggestionFailed>(x => x.Context.Message.JobId == jobId));
    }

    [Fact]
    public async Task Consider_timeout_marks_job_failed()
    {
        var jobId = Guid.NewGuid();
        await using var provider = BuildOrchestratorProvider(considerTimeoutSeconds: 5);
        await EnsureOrchestratorDbAsync(provider);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish<ITicketCreated>(Message(jobId, "TCK-TIMEOUT"));
        Assert.True(await harness.Consumed.Any<ITicketCreated>(x => x.Context.Message.JobId == jobId));

        await using var scope = provider.CreateAsyncScope();
        var job = await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>()
            .AutoSuggestionJobs.FindAsync(jobId);
        Assert.Equal(AutoSuggestionJobStatus.Failed, job!.Status);
        Assert.Contains("timed out", job.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejected_consider_marks_job_discarded()
    {
        var jobId = Guid.NewGuid();
        await using var provider = BuildFullProvider();
        await EnsureOrchestratorDbAsync(provider);
        await SeedTicketAsync(provider, "TCK-REJ", TicketStatus.Resolved, finalAnswer: "done");

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        await harness.Bus.Publish<ITicketCreated>(Message(jobId, "TCK-REJ"));
        Assert.True(await harness.Consumed.Any<ITicketCreated>(x => x.Context.Message.JobId == jobId));

        await using var scope = provider.CreateAsyncScope();
        var job = await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>()
            .AutoSuggestionJobs.FindAsync(jobId);
        Assert.Equal(AutoSuggestionJobStatus.Discarded, job!.Status);
        Assert.False(string.IsNullOrWhiteSpace(job.DiscardReason));
    }

    [Fact]
    public async Task Accepted_consider_marks_job_completed_and_publishes_generated()
    {
        var jobId = Guid.NewGuid();
        await using var provider = BuildFullProvider();
        await EnsureOrchestratorDbAsync(provider);
        await SeedTicketAsync(provider, "TCK-OK", TicketStatus.New);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        await harness.Bus.Publish<ITicketCreated>(Message(jobId, "TCK-OK"));
        Assert.True(await harness.Consumed.Any<ITicketCreated>(x => x.Context.Message.JobId == jobId));

        await using var scope = provider.CreateAsyncScope();
        var job = await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>()
            .AutoSuggestionJobs.FindAsync(jobId);
        if (job!.Status != AutoSuggestionJobStatus.Completed)
            Assert.Fail($"Expected Completed but got {job.Status}: {job.FailureReason}");
        Assert.True(await harness.Published.Any<IAiSuggestionGenerated>(x => x.Context.Message.JobId == jobId));

        var ticket = await scope.ServiceProvider.GetRequiredService<TicketDbContext>().Tickets.FindAsync("TCK-OK");
        Assert.Equal(TicketStatus.Suggested, ticket!.Status);
        Assert.False(string.IsNullOrWhiteSpace(ticket.AiSuggestedAnswer));
    }

    [Fact]
    public async Task Skip_consider_leaves_job_produced()
    {
        var jobId = Guid.NewGuid();
        await using var provider = BuildFullProvider();
        await EnsureOrchestratorDbAsync(provider);
        await SeedTicketAsync(provider, "TCK-SKIP", TicketStatus.New);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        await harness.Bus.Publish<ITicketCreated>(Message(jobId, "TCK-SKIP", FaultInjection.ForceSkipConsider));
        Assert.True(await harness.Consumed.Any<ITicketCreated>(x => x.Context.Message.JobId == jobId));

        await using var scope = provider.CreateAsyncScope();
        var job = await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>()
            .AutoSuggestionJobs.FindAsync(jobId);
        Assert.Equal(AutoSuggestionJobStatus.Produced, job!.Status);
    }

    private static ITicketCreated Message(Guid jobId, string ticketId, string? marker = null) =>
        new TicketCreated(jobId, ticketId, "EMP-1", $"VPN help {marker ?? ""}".Trim(), SupportCategory.IT);

    private static ServiceProvider BuildOrchestratorProvider(int considerTimeoutSeconds = 30) =>
        BuildProvider(includeTicketDb: false, considerTimeoutSeconds);

    private static ServiceProvider BuildFullProvider(int considerTimeoutSeconds = 30) =>
        BuildProvider(includeTicketDb: true, considerTimeoutSeconds);

    private static ServiceProvider BuildProvider(bool includeTicketDb, int considerTimeoutSeconds)
    {
        MapConsiderEndpoint();

        var dbKey = Guid.NewGuid().ToString("N");
        var orchDbPath = Path.Combine(Path.GetTempPath(), $"orch-test-{dbKey}.db");
        var ticketDbPath = Path.Combine(Path.GetTempPath(), $"ticket-test-{dbKey}.db");

        var services = new ServiceCollection()
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddDbContext<OrchestratorDbContext>((_, o) => o.UseSqlite($"Data Source={orchDbPath}"))
            .AddSingleton(Microsoft.Extensions.Options.Options.Create(new AutoSuggestionOptions { ConsiderRequestTimeoutSeconds = considerTimeoutSeconds }))
            .AddSingleton(Microsoft.Extensions.Options.Options.Create(new AzureOpenAIOptions { ChatEnabled = false }))
            .AddSingleton(Microsoft.Extensions.Options.Options.Create(new ServiceEndpointsOptions { KnowledgeService = "http://127.0.0.1:1" }))
            .AddHttpClient()
            .AddSingleton<KnowledgeSearchClient>()
            .AddScoped<IChatCompletionServiceAccessor>()
            .AddScoped<AiPipelineService>();

        services.AddScoped<IConsiderAutoSuggestionGateway, MassTransitConsiderAutoSuggestionGateway>();

        if (includeTicketDb)
        {
            services
                .AddDbContext<TicketDbContext>((_, o) => o.UseSqlite($"Data Source={ticketDbPath}"))
                .AddScoped<ConsiderAutoSuggestionApplier>();
        }

        services.AddMassTransitTestHarness(cfg =>
        {
            cfg.SetTestTimeouts(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(250));
            cfg.AddConsumer<TicketCreatedConsumer>();
            if (includeTicketDb)
            {
                cfg.AddConsumer<ConsiderAutoSuggestionConsumer>()
                    .Endpoint(e => e.Name = "consider-auto-suggestion");
            }
        });

        return services.BuildServiceProvider(true);
    }

    private static void MapConsiderEndpoint() =>
        EndpointConvention.Map<IConsiderAutoSuggestion>(new Uri("queue:consider-auto-suggestion"));

    private static async Task EnsureOrchestratorDbAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await db.Database.EnsureCreatedAsync();
        await OrchestratorDbSchema.EnsureSchemaAsync(db);
    }

    private static async Task SeedOrchestratorJobAsync(
        ServiceProvider provider,
        Guid jobId,
        string ticketId,
        string status)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.AutoSuggestionJobs.Add(new AutoSuggestionJob
        {
            JobId = jobId,
            TicketId = ticketId,
            EmployeeId = "EMP-1",
            Question = "VPN",
            Category = SupportCategory.IT,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedTicketAsync(
        ServiceProvider provider,
        string id,
        string status,
        string? finalAnswer = null)
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
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }
}
