namespace SupportPoc.AiOrchestrator.Options;

// Cau hinh timeout / verify recovery cho saga TicketSuggestion — rieng tung buoc.
public sealed class SagaTimeoutOptions
{
    public const string SectionName = "Saga";

    // Delay ngan giua cac lan VerifyDue (dung chung).
    public int VerifyRetrySeconds { get; set; } = 15;

    public SagaStepTimeoutOptions Analyzing { get; set; } = new()
    {
        TimeoutSeconds = 60,
        MaxVerifyAttempts = 3,
        PostResendVerifyAttempts = 2
    };

    public SagaStepTimeoutOptions RunningAi { get; set; } = new()
    {
        TimeoutSeconds = 600,
        MaxVerifyAttempts = 4,
        PostResendVerifyAttempts = 2,
        MaxResendAttempts = 2
    };

    public SagaStepTimeoutOptions Saving { get; set; } = new()
    {
        TimeoutSeconds = 120,
        MaxVerifyAttempts = 3,
        PostResendVerifyAttempts = 2
    };

    public SagaStepTimeoutOptions Compensating { get; set; } = new()
    {
        TimeoutSeconds = 60,
        MaxVerifyAttempts = 3,
        PostResendVerifyAttempts = 2,
        MaxResendAttempts = 1
    };
}

public sealed class SagaStepTimeoutOptions
{
    public int TimeoutSeconds { get; set; } = 300;

    // So lan verify truoc khi resend command (pre-resend).
    public int MaxVerifyAttempts { get; set; } = 3;

    // So lan verify sau moi lan resend truoc khi bước tiep / compensate / fail.
    public int PostResendVerifyAttempts { get; set; } = 2;

    // Chi dung o RunningAi: toi da bao nhieu lan gui lai RunAiPipeline.
    public int MaxResendAttempts { get; set; } = 1;
}
