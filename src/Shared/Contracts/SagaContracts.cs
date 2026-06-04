using SupportPoc.Shared.Models;

namespace SupportPoc.Shared.Contracts;

// =========================
// Cross-service Events (Topic)
// =========================

/// <summary>TicketService publish khi tao ticket moi — wake auto-suggestion job.</summary>
public interface ITicketCreated
{
    Guid JobId { get; }
    string TicketId { get; }
    string EmployeeId { get; }
    string Question { get; }
    string Category { get; }
}

/// <summary>Broadcast khi proposal duoc ticket accept.</summary>
public interface IAiSuggestionGenerated
{
    Guid JobId { get; }
    string TicketId { get; }
}

/// <summary>Audit: AI job that bai — ticket khong bi mutate.</summary>
public interface IAutoSuggestionFailed
{
    Guid JobId { get; }
    string TicketId { get; }
    string Reason { get; }
}

/// <summary>Audit: proposal bi ticket reject (stale / khong con suggestible).</summary>
public interface IAutoSuggestionDiscarded
{
    Guid JobId { get; }
    string TicketId { get; }
    string Reason { get; }
}

// =========================
// Cross-service Command (Queue) — DB-only tren TicketService
// =========================

/// <summary>Orchestrator gui sau khi AI pipeline xong — TicketService quyet accept/reject bang invariant.</summary>
public interface IConsiderAutoSuggestion
{
    Guid JobId { get; }
    string TicketId { get; }
    string Category { get; }
    string Suggestion { get; }
    IReadOnlyList<RelatedDocument> RelatedDocuments { get; }
}

// =========================
// Request/response (ConsiderAutoSuggestion consumer)
// =========================

public interface IAutoSuggestionAccepted
{
    Guid JobId { get; }
    string TicketId { get; }
}

public interface IAutoSuggestionRejected
{
    Guid JobId { get; }
    string TicketId { get; }
    string Reason { get; }
}

// =========================
// Concrete records
// =========================

public sealed record TicketCreated(Guid JobId, string TicketId, string EmployeeId, string Question, string Category) : ITicketCreated;
public sealed record AiSuggestionGenerated(Guid JobId, string TicketId) : IAiSuggestionGenerated;
public sealed record AutoSuggestionFailed(Guid JobId, string TicketId, string Reason) : IAutoSuggestionFailed;
public sealed record AutoSuggestionDiscarded(Guid JobId, string TicketId, string Reason) : IAutoSuggestionDiscarded;

public sealed record ConsiderAutoSuggestion(
    Guid JobId,
    string TicketId,
    string Category,
    string Suggestion,
    IReadOnlyList<RelatedDocument> RelatedDocuments) : IConsiderAutoSuggestion;

public sealed record AutoSuggestionAccepted(Guid JobId, string TicketId) : IAutoSuggestionAccepted;
public sealed record AutoSuggestionRejected(Guid JobId, string TicketId, string Reason) : IAutoSuggestionRejected;
