namespace SupportPoc.AiOrchestrator.Options;

public sealed class AutoSuggestionOptions
{
    public const string SectionName = "AutoSuggestion";

    public int StepTimeoutSeconds { get; set; } = 120;
    public int ProposeRequestTimeoutSeconds { get; set; } = 30;
    public int MaxGenerationRetries { get; set; } = 3;
    public int MaxProposeRetries { get; set; } = 3;

    /// <summary>Timeout moi HTTP call reconcile toi TicketService (giay).</summary>
    public int ReconcileHttpTimeoutSeconds { get; set; } = 10;

    /// <summary>So lan retry transient khi goi HTTP reconcile.</summary>
    public int ReconcileHttpMaxRetries { get; set; } = 2;

    /// <summary>Chu ky sweeper quet saga Reconciling (giay).</summary>
    public int StuckReconcilingSweepIntervalSeconds { get; set; } = 60;

    /// <summary>Saga Reconciling khong doi hon nguong nay thi sweeper gui lai reconcile (phut).</summary>
    public int StuckReconcilingRetryAfterMinutes { get; set; } = 2;

    /// <summary>Saga Reconciling qua nguong nay thi sweeper xem xet abandon (phut).</summary>
    public int StuckReconcilingFailAfterMinutes { get; set; } = 30;

    /// <summary>Saga ket o GeneratingSuggestion/ApplyingSuggestion qua nguong nay thi sweeper can thiep (phut).</summary>
    public int StuckStepSweepAfterMinutes { get; set; } = 15;
}
