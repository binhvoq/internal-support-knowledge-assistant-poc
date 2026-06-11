namespace SupportPoc.AiOrchestrator.Options;

public sealed class AutoSuggestionOptions
{
    public const string SectionName = "AutoSuggestion";

    public int StepTimeoutSeconds { get; set; } = 120;
    public int ProposeRequestTimeoutSeconds { get; set; } = 30;
    public int MaxGenerationRetries { get; set; } = 2;
}
