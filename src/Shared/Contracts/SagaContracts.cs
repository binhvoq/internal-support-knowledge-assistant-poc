using SupportPoc.Shared.Models;

namespace SupportPoc.Shared.Contracts;

// =========================
// Cross-service Events (Topic)
// Phát rộng cho nhiều subscriber; MassTransit map -> Service Bus Topic
// =========================

// Event nay duoc TicketService publish khi tao ticket moi.
// AiOrchestrator saga state machine se "Initially" nhan event nay
// va correlate by CorrelationId (Guid).
public interface ITicketCreated
{
    Guid CorrelationId { get; }
    string TicketId { get; }
    string EmployeeId { get; }
    string Question { get; }
    string Category { get; }
    int SagaEpoch { get; }
}

// TicketService publish sau khi xu ly Cmd.MarkTicketAnalyzing thanh cong.
public interface ITicketAnalyzingMarked
{
    Guid CorrelationId { get; }
    string TicketId { get; }
    int SagaEpoch { get; }
}

// TicketService publish khi Cmd.MarkTicketAnalyzing fail (ticket khong ton tai, sai state...).
public interface ITicketAnalyzingMarkFailed
{
    Guid CorrelationId { get; }
    string TicketId { get; }
    string Reason { get; }
}

// TicketService publish sau khi xu ly Cmd.SaveTicketSuggestion thanh cong.
public interface ITicketSuggestionSaved
{
    Guid CorrelationId { get; }
    string TicketId { get; }
}

// TicketService publish khi save suggestion fail.
public interface ITicketSuggestionSaveFailed
{
    Guid CorrelationId { get; }
    string TicketId { get; }
    string Reason { get; }
}

// TicketService publish sau khi compensation hoan tat (revert status ve trang thai cu).
public interface IMarkAnalyzingReverted
{
    Guid CorrelationId { get; }
    string TicketId { get; }
}

// Event broadcast khi saga ket thuc thanh cong - cho downstream (notification, analytics) subscribe.
public interface IAiSuggestionGenerated
{
    Guid CorrelationId { get; }
    string TicketId { get; }
}

// =========================
// Cross-service Commands (Queue, point-to-point)
// AiOrchestrator saga gui den TicketService consumer
// =========================

public interface IMarkTicketAnalyzing
{
    Guid CorrelationId { get; }
    string TicketId { get; }
    int ExpectedEpoch { get; }
}

public interface ISaveTicketSuggestion
{
    Guid CorrelationId { get; }
    string TicketId { get; }
    int ExpectedEpoch { get; }
    string Category { get; }
    string Suggestion { get; }
    IReadOnlyList<RelatedDocument> RelatedDocuments { get; }
}

public interface ICompensateMarkAnalyzing
{
    Guid CorrelationId { get; }
    string TicketId { get; }
    // Trang thai goc truoc khi PATCH "Analyzing" - de revert chinh xac.
    string OriginalStatus { get; }
}

// =========================
// Internal (AiOrchestrator only) - chay AI pipeline async
// Saga gui Cmd.RunAiPipeline -> consumer trong cung process xu ly -> publish Evt.AiPipelineCompleted/Failed
// =========================

public interface IRunAiPipeline
{
    Guid CorrelationId { get; }
    string TicketId { get; }
    string Question { get; }
    string Category { get; }
}

public interface IAiPipelineCompleted
{
    Guid CorrelationId { get; }
    string TicketId { get; }
    string Category { get; }
    string Suggestion { get; }
    IReadOnlyList<RelatedDocument> RelatedDocuments { get; }
}

public interface IAiPipelineFailed
{
    Guid CorrelationId { get; }
    string TicketId { get; }
    string Reason { get; }
}

// =========================
// Saga Timeout (Schedule message)
// =========================

public interface ISagaTimeoutExpired
{
    Guid CorrelationId { get; }
    string TicketId { get; }
}

// Short-delay schedule for Saving timeout recovery (verify/reconcile), separate from step timeout.
public interface ISagaVerifyDue
{
    Guid CorrelationId { get; }
    string TicketId { get; }
}

// =========================
// Concrete record types - dung de publish/send.
// Tach interface va record giup test va versioning de hon.
// =========================

public sealed record TicketCreated(Guid CorrelationId, string TicketId, string EmployeeId, string Question, string Category, int SagaEpoch) : ITicketCreated;
public sealed record TicketAnalyzingMarked(Guid CorrelationId, string TicketId, int SagaEpoch) : ITicketAnalyzingMarked;
public sealed record TicketAnalyzingMarkFailed(Guid CorrelationId, string TicketId, string Reason) : ITicketAnalyzingMarkFailed;
public sealed record TicketSuggestionSaved(Guid CorrelationId, string TicketId) : ITicketSuggestionSaved;
public sealed record TicketSuggestionSaveFailed(Guid CorrelationId, string TicketId, string Reason) : ITicketSuggestionSaveFailed;
public sealed record MarkAnalyzingReverted(Guid CorrelationId, string TicketId) : IMarkAnalyzingReverted;
public sealed record AiSuggestionGenerated(Guid CorrelationId, string TicketId) : IAiSuggestionGenerated;

public sealed record MarkTicketAnalyzing(Guid CorrelationId, string TicketId, int ExpectedEpoch) : IMarkTicketAnalyzing;
public sealed record SaveTicketSuggestion(Guid CorrelationId, string TicketId, int ExpectedEpoch, string Category, string Suggestion, IReadOnlyList<RelatedDocument> RelatedDocuments) : ISaveTicketSuggestion;
public sealed record CompensateMarkAnalyzing(Guid CorrelationId, string TicketId, string OriginalStatus) : ICompensateMarkAnalyzing;

public sealed record RunAiPipeline(Guid CorrelationId, string TicketId, string Question, string Category) : IRunAiPipeline;
public sealed record AiPipelineCompleted(Guid CorrelationId, string TicketId, string Category, string Suggestion, IReadOnlyList<RelatedDocument> RelatedDocuments) : IAiPipelineCompleted;
public sealed record AiPipelineFailed(Guid CorrelationId, string TicketId, string Reason) : IAiPipelineFailed;

public sealed record SagaTimeoutExpired(Guid CorrelationId, string TicketId) : ISagaTimeoutExpired;
public sealed record SagaVerifyDue(Guid CorrelationId, string TicketId) : ISagaVerifyDue;
