using SupportPoc.AiOrchestrator.Data;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class OrchestratorSchemaPatcherTests
{
    [Fact]
    public void BuildAddColumnIfMissingSql_is_idempotent_for_ProposeRetryCount()
    {
        var sql = OrchestratorSchemaPatcher.BuildAddColumnIfMissingSql(
            "dbo",
            "TicketSuggestionSagas",
            "ProposeRetryCount",
            "int NOT NULL DEFAULT 0");

        Assert.Contains("OBJECT_ID(N'dbo.TicketSuggestionSagas', N'U') IS NOT NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.TicketSuggestionSagas', N'ProposeRetryCount') IS NULL", sql);
        Assert.Contains("ALTER TABLE [dbo].[TicketSuggestionSagas] ADD [ProposeRetryCount] int NOT NULL DEFAULT 0", sql);
    }

    [Fact]
    public void BuildAddColumnIfMissingSql_is_idempotent_for_ReconcileTransientFailureCount()
    {
        var sql = OrchestratorSchemaPatcher.BuildAddColumnIfMissingSql(
            "dbo",
            "TicketSuggestionSagas",
            "ReconcileTransientFailureCount",
            "int NOT NULL DEFAULT 0");

        Assert.Contains("ReconcileTransientFailureCount", sql);
        Assert.Contains("int NOT NULL DEFAULT 0", sql);
    }

    [Fact]
    public void BuildAddColumnIfMissingSql_is_idempotent_for_LastReconcileAttemptAt()
    {
        var sql = OrchestratorSchemaPatcher.BuildAddColumnIfMissingSql(
            "dbo",
            "TicketSuggestionSagas",
            "LastReconcileAttemptAt",
            "datetimeoffset NULL");

        Assert.Contains("LastReconcileAttemptAt", sql);
        Assert.Contains("datetimeoffset NULL", sql);
    }

    [Fact]
    public void BuildAddColumnIfMissingSql_is_idempotent_for_ReconcilingSinceAt()
    {
        var sql = OrchestratorSchemaPatcher.BuildAddColumnIfMissingSql(
            "dbo",
            "TicketSuggestionSagas",
            "ReconcilingSinceAt",
            "datetimeoffset NULL");

        Assert.Contains("ReconcilingSinceAt", sql);
        Assert.Contains("datetimeoffset NULL", sql);
    }
}
