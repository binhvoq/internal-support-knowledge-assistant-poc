namespace SupportPoc.AiOrchestrator.Options;

// Cau hinh timeout cho saga TicketSuggestion.
public sealed class SagaTimeoutOptions
{
    public const string SectionName = "Saga";

    // Step timeout: cho event tiep theo trong flow (Analyzing, RunningAi, Saving...).
    public int TimeoutSeconds { get; set; } = 300;

    // VerifyDue: delay ngan khi Saving timeout recovery nghi ngo (khong dung TimeoutSeconds).
    public int VerifyRetrySeconds { get; set; } = 15;

    // So lan verify truoc khi ResendSave (Saving).
    public int MaxVerifyAttempts { get; set; } = 3;

    // So lan verify sau ResendSave truoc khi Compensate (Saving).
    public int PostResendVerifyAttempts { get; set; } = 2;
}
