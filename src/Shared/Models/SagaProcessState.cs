namespace SupportPoc.Shared.Models;

/// <summary>Saga process state — khong mirror len ticket lifecycle.</summary>
public static class SagaProcessState
{
    public const string GeneratingSuggestion = "GeneratingSuggestion";
    public const string ApplyingSuggestion = "ApplyingSuggestion";
    public const string Reconciling = "Reconciling";
    public const string ReconcileUnknown = "ReconcileUnknown";
    public const string Completed = "Completed";
    public const string Discarded = "Discarded";
    public const string Failed = "Failed";

    /// <summary>Terminal states — ReconcileUnknown is a recoverable parking lot, not included here.</summary>
    public static readonly IReadOnlyList<string> Terminal =
        [Completed, Discarded, Failed];

    public static bool IsTerminal(string? state) =>
        state is not null && Terminal.Contains(state);
}
