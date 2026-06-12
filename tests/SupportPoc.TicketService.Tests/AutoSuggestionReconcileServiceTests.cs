using Microsoft.EntityFrameworkCore;
using SupportPoc.Shared.Models;
using SupportPoc.TicketService.Data;
using SupportPoc.TicketService.Services;

namespace SupportPoc.TicketService.Tests;

public sealed class AutoSuggestionReconcileServiceTests : IDisposable
{
    private readonly TicketDbContext _db;
    private readonly AutoSuggestionReconcileService _service;
    private readonly Guid _jobId = Guid.NewGuid();

    public AutoSuggestionReconcileServiceTests()
    {
        var options = new DbContextOptionsBuilder<TicketDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new TicketDbContext(options);
        _service = new AutoSuggestionReconcileService(_db);
    }

    [Fact]
    public async Task Reconcile_returns_not_found_when_ticket_missing()
    {
        var result = await _service.ReconcileAsync("TCK-MISSING", _jobId, 1);

        Assert.Equal(AutoSuggestionReconcileDecision.NotFound, result.Decision);
    }

    [Fact]
    public async Task Reconcile_returns_already_applied_when_same_job_accepted_even_if_ticket_missing()
    {
        _db.ProcessedCommands.Add(new ProcessedCommandEntity
        {
            CommandId = Guid.NewGuid(),
            TicketId = "TCK-GONE",
            JobId = _jobId,
            Accepted = true,
            ProcessedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        var result = await _service.ReconcileAsync("TCK-GONE", _jobId, 1);

        Assert.Equal(AutoSuggestionReconcileDecision.AlreadyAppliedBySameJob, result.Decision);
        Assert.NotEqual(AutoSuggestionReconcileDecision.NotFound, result.Decision);
    }

    [Fact]
    public async Task Reconcile_returns_already_applied_when_same_job_accepted()
    {
        var ticket = await SeedTicketAsync(TicketStatus.Suggested, aiSuggestion: "done");
        _db.ProcessedCommands.Add(new ProcessedCommandEntity
        {
            CommandId = Guid.NewGuid(),
            TicketId = ticket.Id,
            JobId = _jobId,
            Accepted = true,
            ProcessedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        var result = await _service.ReconcileAsync(ticket.Id, _jobId, 1);

        Assert.Equal(AutoSuggestionReconcileDecision.AlreadyAppliedBySameJob, result.Decision);
    }

    [Fact]
    public async Task Reconcile_returns_resolved_when_ticket_has_final_answer()
    {
        var ticket = await SeedTicketAsync(TicketStatus.Resolved, finalAnswer: "closed");

        var result = await _service.ReconcileAsync(ticket.Id, _jobId, 1);

        Assert.Equal(AutoSuggestionReconcileDecision.Resolved, result.Decision);
        Assert.True(result.HasFinalAnswer);
    }

    [Fact]
    public async Task Reconcile_returns_still_suggestible_for_new_ticket()
    {
        var ticket = await SeedTicketAsync(TicketStatus.New);

        var result = await _service.ReconcileAsync(ticket.Id, _jobId, ticket.Version);

        Assert.Equal(AutoSuggestionReconcileDecision.StillSuggestible, result.Decision);
    }

    [Fact]
    public async Task Reconcile_returns_already_suggested_by_other_job()
    {
        var ticket = await SeedTicketAsync(TicketStatus.Suggested, aiSuggestion: "other");
        _db.ProcessedCommands.Add(new ProcessedCommandEntity
        {
            CommandId = Guid.NewGuid(),
            TicketId = ticket.Id,
            JobId = Guid.NewGuid(),
            Accepted = true,
            ProcessedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        var result = await _service.ReconcileAsync(ticket.Id, _jobId, 1);

        Assert.Equal(AutoSuggestionReconcileDecision.AlreadySuggestedByOtherJob, result.Decision);
    }

    [Fact]
    public async Task Reconcile_returns_version_changed_when_ticket_no_longer_initial()
    {
        var ticket = await SeedTicketAsync(TicketStatus.Suggested, version: 3);

        var result = await _service.ReconcileAsync(ticket.Id, _jobId, expectedVersion: 1);

        Assert.Equal(AutoSuggestionReconcileDecision.VersionChanged, result.Decision);
    }

    public void Dispose() => _db.Dispose();

    private async Task<TicketEntity> SeedTicketAsync(
        string status,
        string? aiSuggestion = null,
        string? finalAnswer = null,
        long version = 1)
    {
        var ticket = new TicketEntity
        {
            Id = "TCK-1",
            EmployeeId = "EMP-1",
            Category = SupportCategory.IT,
            Question = "Q",
            Status = status,
            AiSuggestedAnswer = aiSuggestion,
            FinalAnswer = finalAnswer,
            Version = version,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _db.Tickets.Add(ticket);
        await _db.SaveChangesAsync();
        return ticket;
    }
}
