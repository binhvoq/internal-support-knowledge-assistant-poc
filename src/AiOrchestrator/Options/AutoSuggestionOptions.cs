namespace SupportPoc.AiOrchestrator.Options;

public sealed class AutoSuggestionOptions
{
    public const string SectionName = "AutoSuggestion";

    public int StepTimeoutSeconds { get; set; } = 120;
    public int ProposeRequestTimeoutSeconds { get; set; } = 30;
    public int MaxGenerationRetries { get; set; } = 2;

    /// <summary>
    /// MassTransit EF consumer outbox/inbox on the saga receive endpoint.
    /// Default false: SQLite PoC contends on orchestrator.db (saga repository + inbox/outbox).
    /// Set true for PostgreSQL/SQL Server in production.
    /// </summary>
    public bool UseSagaConsumerOutbox { get; set; }
}
