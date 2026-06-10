using SupportPoc.Shared.Models;

namespace SupportPoc.Shared.Contracts;

// =========================
// Cross-service Events (Topic)
// =========================

/// <summary>TicketService publish khi tao ticket moi — start saga.</summary>
public interface ITicketCreated
{
    Guid JobId { get; }
    string TicketId { get; }
    string EmployeeId { get; }
    string Question { get; }
    string Category { get; }
    long TicketVersion { get; }
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
// Saga orchestration messages
// =========================

/// <summary>Saga yeu cau AI worker generate suggestion — worker chi reply ve saga.</summary>
public interface IGenerateSuggestionRequested
{
    Guid SagaId { get; }
    Guid AttemptId { get; }
    Guid JobId { get; }
    string TicketId { get; }
    string Question { get; }
    string Category { get; }
}

/// <summary>AI worker tra ket qua thanh cong ve saga.</summary>
public interface ISuggestionGenerated
{
    Guid SagaId { get; }
    Guid AttemptId { get; }
    Guid JobId { get; }
    string TicketId { get; }
    string Category { get; }
    string Suggestion { get; }
    IReadOnlyList<RelatedDocument> RelatedDocuments { get; }
}

/// <summary>AI worker tra loi ve saga.</summary>
public interface ISuggestionGenerationFailed
{
    Guid SagaId { get; }
    Guid AttemptId { get; }
    Guid JobId { get; }
    string TicketId { get; }
    string Reason { get; }
}

/// <summary>Scheduled timeout — khong rollback, chuyen sang reconcile.</summary>
public interface IStepTimeout
{
    Guid SagaId { get; }
    Guid AttemptId { get; }
}

// =========================
// Intent-based final command (TicketService gate)
// =========================

/// <summary>Saga propose suggestion — TicketService quyet accept/reject bang invariant.</summary>
public interface IProposeTicketSuggestion
{
    Guid CommandId { get; }
    Guid SagaId { get; }
    Guid AttemptId { get; }
    Guid JobId { get; }
    string TicketId { get; }
    string Category { get; }
    string Suggestion { get; }
    IReadOnlyList<RelatedDocument> RelatedDocuments { get; }
    long? ExpectedTicketVersion { get; }
}

public interface ITicketSuggestionAccepted
{
    Guid CommandId { get; }
    Guid SagaId { get; }
    Guid AttemptId { get; }
    Guid JobId { get; }
    string TicketId { get; }
}

public interface ITicketSuggestionRejected
{
    Guid CommandId { get; }
    Guid SagaId { get; }
    Guid AttemptId { get; }
    Guid JobId { get; }
    string TicketId { get; }
    string Reason { get; }
}

/// <summary>Unified request/response payload cho MassTransit saga Request.</summary>
public interface IProposeTicketSuggestionResult
{
    Guid CommandId { get; }
    Guid SagaId { get; }
    Guid AttemptId { get; }
    Guid JobId { get; }
    string TicketId { get; }
    bool Accepted { get; }
    string? Reason { get; }
}

// =========================
// Concrete records
// =========================

public sealed record TicketCreated(
    Guid JobId,
    string TicketId,
    string EmployeeId,
    string Question,
    string Category,
    long TicketVersion) : ITicketCreated;

public sealed record AiSuggestionGenerated(Guid JobId, string TicketId) : IAiSuggestionGenerated;
public sealed record AutoSuggestionFailed(Guid JobId, string TicketId, string Reason) : IAutoSuggestionFailed;
public sealed record AutoSuggestionDiscarded(Guid JobId, string TicketId, string Reason) : IAutoSuggestionDiscarded;

public sealed record GenerateSuggestionRequested(
    Guid SagaId,
    Guid AttemptId,
    Guid JobId,
    string TicketId,
    string Question,
    string Category) : IGenerateSuggestionRequested;

public sealed record SuggestionGenerated(
    Guid SagaId,
    Guid AttemptId,
    Guid JobId,
    string TicketId,
    string Category,
    string Suggestion,
    IReadOnlyList<RelatedDocument> RelatedDocuments) : ISuggestionGenerated;

public sealed record SuggestionGenerationFailed(
    Guid SagaId,
    Guid AttemptId,
    Guid JobId,
    string TicketId,
    string Reason) : ISuggestionGenerationFailed;

public sealed record StepTimeout(Guid SagaId, Guid AttemptId) : IStepTimeout;

public sealed record ProposeTicketSuggestion(
    Guid CommandId,
    Guid SagaId,
    Guid AttemptId,
    Guid JobId,
    string TicketId,
    string Category,
    string Suggestion,
    IReadOnlyList<RelatedDocument> RelatedDocuments,
    long? ExpectedTicketVersion) : IProposeTicketSuggestion;

public sealed record TicketSuggestionAccepted(
    Guid CommandId,
    Guid SagaId,
    Guid AttemptId,
    Guid JobId,
    string TicketId) : ITicketSuggestionAccepted;

public sealed record TicketSuggestionRejected(
    Guid CommandId,
    Guid SagaId,
    Guid AttemptId,
    Guid JobId,
    string TicketId,
    string Reason) : ITicketSuggestionRejected;

public sealed record ProposeTicketSuggestionResult(
    Guid CommandId,
    Guid SagaId,
    Guid AttemptId,
    Guid JobId,
    string TicketId,
    bool Accepted,
    string? Reason) : IProposeTicketSuggestionResult;
