namespace SupportPoc.AiOrchestrator.Options;

public sealed class AutoSuggestionOptions
{
    public const string SectionName = "AutoSuggestion";

    /// <summary>Timeout saga cho moi buoc (Generating/Applying). Phai >= AiGenerationLeaseSeconds.</summary>
    public int StepTimeoutSeconds { get; set; } = 360;
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

    /// <summary>Saga Reconciling voi transient failure keo dai hon nguong nay thi leo thang ReconcileUnknown (phut).</summary>
    public int StuckReconcilingEscalateAfterMinutes { get; set; } = 120;

    /// <summary>So lan transient failure truoc khi leo thang ReconcileUnknown.</summary>
    public int MaxReconcileTransientFailuresBeforeEscalate { get; set; } = 20;

    /// <summary>Backoff co so cho retry transient reconcile (giay).</summary>
    public int ReconcileTransientBackoffBaseSeconds { get; set; } = 30;

    /// <summary>Backoff toi da cho retry transient reconcile (giay).</summary>
    public int ReconcileTransientBackoffMaxSeconds { get; set; } = 900;

    /// <summary>Saga ket o GeneratingSuggestion/ApplyingSuggestion qua nguong nay thi sweeper can thiep (phut).</summary>
    public int StuckStepSweepAfterMinutes { get; set; } = 15;

    /// <summary>Chu ky toi thieu giua cac lan auto-redrive saga ReconcileUnknown (phut).</summary>
    public int ReconcileUnknownRedriveAfterMinutes { get; set; } = 15;

    /// <summary>So lan auto-redrive toi da cho saga ReconcileUnknown truoc khi dung (khong retry vo han).</summary>
    public int MaxReconcileUnknownRedriveAttempts { get; set; } = 10;

    /// <summary>Backoff co so cho auto-redrive ReconcileUnknown (giay) — dai hon Reconciling.</summary>
    public int ReconcileUnknownBackoffBaseSeconds { get; set; } = 300;

    /// <summary>Backoff toi da cho auto-redrive ReconcileUnknown (giay).</summary>
    public int ReconcileUnknownBackoffMaxSeconds { get; set; } = 3600;

    /// <summary>Chu ky poll durable AI generation jobs (giay).</summary>
    public int AiGenerationWorkerPollIntervalSeconds { get; set; } = 2;

    /// <summary>So job AI generation chay dong thoi tren moi instance.</summary>
    public int AiGenerationWorkerConcurrency { get; set; } = 2;

    /// <summary>Lease khi worker claim attempt (giay) — phai du lon cho HTTP/LLM.</summary>
    public int AiGenerationLeaseSeconds { get; set; } = 300;

    /// <summary>Budget toi da cho mot attempt (giay) — ke ca khi worker renew lease lien tuc.</summary>
    public int AiGenerationHardTimeoutSeconds { get; set; } = 1800;

    /// <summary>Chu ky poll attempt khi saga cho generation active (giay).</summary>
    public int GenerationCheckIntervalSeconds { get; set; } = 30;

    /// <summary>Cho phep consumer tao row attempt sau khi saga gui generation request (giay).</summary>
    public int MissingAttemptGraceSeconds { get; set; } = 60;
}
