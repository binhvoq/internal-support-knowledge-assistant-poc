namespace SupportPoc.Shared.Models;

public static class AutoSuggestionJobStatus
{
    public const string Running = "Running";
    public const string Produced = "Produced";
    public const string Completed = "Completed";
    public const string Discarded = "Discarded";
    public const string Failed = "Failed";
    public const string Unknown = "Unknown";

    public static readonly IReadOnlyList<string> All =
        [Running, Produced, Completed, Discarded, Failed, Unknown];
}
