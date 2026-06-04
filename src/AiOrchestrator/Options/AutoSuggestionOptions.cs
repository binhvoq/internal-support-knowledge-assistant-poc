namespace SupportPoc.AiOrchestrator.Options;

public sealed class AutoSuggestionOptions
{
    public const string SectionName = "AutoSuggestion";

    public int ConsiderRequestTimeoutSeconds { get; set; } = 30;
}
