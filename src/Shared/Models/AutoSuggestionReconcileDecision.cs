namespace SupportPoc.Shared.Models;

public static class AutoSuggestionReconcileDecision
{
    public const string AlreadyAppliedBySameJob = "AlreadyAppliedBySameJob";
    public const string StillSuggestible = "StillSuggestible";
    public const string Resolved = "Resolved";
    public const string AlreadySuggestedByOtherJob = "AlreadySuggestedByOtherJob";
    public const string VersionChanged = "VersionChanged";
    public const string NotFound = "NotFound";
}
