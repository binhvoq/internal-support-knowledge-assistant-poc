namespace SupportPoc.KnowledgeService.Services;

public sealed class ReindexState
{
    private readonly object _lock = new();
    public string Status { get; private set; } = "Idle";
    public string? LastError { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public void Set(string status, string? error = null)
    {
        lock (_lock)
        {
            Status = status;
            LastError = error;
            if (status == "Completed")
                CompletedAt = DateTimeOffset.UtcNow;
        }
    }

    public object Snapshot()
    {
        lock (_lock)
        {
            return new { status = Status, lastError = LastError, completedAt = CompletedAt };
        }
    }
}
