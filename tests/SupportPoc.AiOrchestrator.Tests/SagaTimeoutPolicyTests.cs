using Microsoft.Extensions.Options;
using AiOptions = SupportPoc.AiOrchestrator.Options.SagaTimeoutOptions;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga;
using SupportPoc.AiOrchestrator.Saga.Timeouts.Core;
using SupportPoc.AiOrchestrator.Saga.Timeouts.Policies;
using SupportPoc.AiOrchestrator.Saga.Timeouts.Probes;
using SupportPoc.Shared.Models;
using SupportPoc.Shared.Testing;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class SagaTimeoutPolicyTests
{
    private static readonly Guid SagaId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static IOptions<AiOptions> WrapOpts() =>
        Microsoft.Extensions.Options.Options.Create(TestOptions());

    private static AiOptions TestOptions() => new()
    {
        VerifyRetrySeconds = 5,
        Analyzing = new SagaStepTimeoutOptions { MaxVerifyAttempts = 2, PostResendVerifyAttempts = 1 },
        RunningAi = new SagaStepTimeoutOptions { MaxVerifyAttempts = 2, PostResendVerifyAttempts = 1, MaxResendAttempts = 2 },
        Saving = new SagaStepTimeoutOptions { MaxVerifyAttempts = 2, PostResendVerifyAttempts = 1 },
        Compensating = new SagaStepTimeoutOptions { MaxVerifyAttempts = 2, PostResendVerifyAttempts = 1, MaxResendAttempts = 1 }
    };

    private static TicketSuggestionState Saga(
        int epoch = 1,
        string? category = null,
        string? suggestion = null,
        string originalStatus = TicketStatus.New) => new()
    {
        CorrelationId = SagaId,
        TicketId = "TCK-TEST",
        TicketSagaEpoch = epoch,
        OriginalStatus = originalStatus,
        Category = category,
        Suggestion = suggestion,
        RelatedDocumentsJson = "[]"
    };

    private static StepTimeoutContext Ctx(
        TicketSuggestionState saga,
        SagaStepTimeoutOptions step,
        int verifyAttempt = 0,
        int postResendVerifyAttempt = 0,
        int resendCount = 0) =>
        new(saga, step, verifyAttempt, postResendVerifyAttempt, resendCount);

    private static TicketProgressProbeResult Found(TicketProgressSnapshot snapshot) =>
        new(TicketProgressProbeStatus.Found, snapshot, null);

    private static TicketProgressSnapshot Snapshot(
        string status,
        int epoch,
        Guid? activeSaga = null,
        bool hasSuggestion = false) =>
        new("TCK-TEST", status, epoch, activeSaga ?? SagaId, hasSuggestion);

    // --- Analyzing ---

    [Fact]
    public void Analyzing_mark_already_applied_proceeds()
    {
        var saga = Saga(epoch: 1);
        var policy = new AnalyzingTimeoutPolicy(WrapOpts());
        var probe = Found(Snapshot(TicketStatus.Analyzing, epoch: 1, activeSaga: SagaId));
        var decision = policy.Decide(probe, Ctx(saga, TestOptions().Analyzing));

        Assert.Equal(SagaTimeoutOutcome.Proceed, decision.Outcome);
        Assert.Equal(1, decision.ResolvedTicketSagaEpoch);
    }

    [Fact]
    public void Analyzing_probe_unavailable_retries_then_fails()
    {
        var saga = Saga();
        var policy = new AnalyzingTimeoutPolicy(WrapOpts());
        var probe = new TicketProgressProbeResult(TicketProgressProbeStatus.Unavailable, null, "503");

        var retry = policy.Decide(probe, Ctx(saga, TestOptions().Analyzing, verifyAttempt: 0));
        Assert.Equal(SagaTimeoutOutcome.RetryVerify, retry.Outcome);

        var fail = policy.Decide(probe, Ctx(saga, TestOptions().Analyzing, verifyAttempt: 2));
        Assert.Equal(SagaTimeoutOutcome.Fail, fail.Outcome);
    }

    [Fact]
    public void Analyzing_wrong_saga_owner_fails()
    {
        var saga = Saga();
        var other = Guid.NewGuid();
        var policy = new AnalyzingTimeoutPolicy(WrapOpts());
        var probe = Found(Snapshot(TicketStatus.New, epoch: 1, activeSaga: other));

        var decision = policy.Decide(
            probe,
            Ctx(saga, TestOptions().Analyzing, verifyAttempt: 2));

        Assert.Equal(SagaTimeoutOutcome.Fail, decision.Outcome);
        Assert.Contains("owned by another saga", decision.Reason);
    }

    [Fact]
    public void Analyzing_exhausted_verify_resends_mark()
    {
        var saga = Saga();
        var policy = new AnalyzingTimeoutPolicy(WrapOpts());
        var probe = Found(Snapshot(TicketStatus.New, epoch: 1, activeSaga: null));

        var decision = policy.Decide(
            probe,
            Ctx(saga, TestOptions().Analyzing, verifyAttempt: 2));

        Assert.Equal(SagaTimeoutOutcome.ResendMark, decision.Outcome);
    }

    // --- RunningAi ---

    [Fact]
    public void RunningAi_payload_on_saga_proceeds_without_probe()
    {
        var saga = Saga(category: "IT", suggestion: "Reset VPN password");
        var policy = new RunningAiTimeoutPolicy(WrapOpts());
        var probe = new TicketProgressProbeResult(TicketProgressProbeStatus.Unavailable, null, "ignored");

        var decision = policy.Decide(probe, Ctx(saga, TestOptions().RunningAi));

        Assert.Equal(SagaTimeoutOutcome.Proceed, decision.Outcome);
    }

    [Fact]
    public void RunningAi_exhausted_resend_compensates()
    {
        var saga = Saga();
        var policy = new RunningAiTimeoutPolicy(WrapOpts());
        var probe = Found(Snapshot(TicketStatus.Analyzing, epoch: 1));

        var decision = policy.Decide(
            probe,
            Ctx(saga, TestOptions().RunningAi, verifyAttempt: 0, postResendVerifyAttempt: 1, resendCount: 2));

        Assert.Equal(SagaTimeoutOutcome.Compensate, decision.Outcome);
    }

    [Fact]
    public void RunningAi_pre_resend_exhausted_resends_run()
    {
        var saga = Saga();
        var policy = new RunningAiTimeoutPolicy(WrapOpts());
        var probe = Found(Snapshot(TicketStatus.Analyzing, epoch: 1));

        var decision = policy.Decide(
            probe,
            Ctx(saga, TestOptions().RunningAi, verifyAttempt: 2));

        Assert.Equal(SagaTimeoutOutcome.ResendRun, decision.Outcome);
    }

    // --- Saving ---

    [Fact]
    public void Saving_already_saved_completes()
    {
        var saga = Saga(epoch: 1);
        var policy = new SavingTimeoutPolicy(WrapOpts());
        var probe = Found(Snapshot(TicketStatus.Suggested, epoch: 1, activeSaga: null, hasSuggestion: true));

        var decision = policy.Decide(probe, Ctx(saga, TestOptions().Saving));

        Assert.Equal(SagaTimeoutOutcome.Complete, decision.Outcome);
        Assert.Contains("Save already applied", decision.Reason);
    }

    [Fact]
    public void Saving_post_resend_grace_compensates()
    {
        var saga = Saga(category: "IT", suggestion: "Help", epoch: 1);
        var policy = new SavingTimeoutPolicy(WrapOpts());
        var probe = Found(Snapshot(TicketStatus.Analyzing, epoch: 1));

        var decision = policy.Decide(
            probe,
            Ctx(saga, TestOptions().Saving, resendCount: 1, postResendVerifyAttempt: 1));

        Assert.Equal(SagaTimeoutOutcome.Compensate, decision.Outcome);
    }

    [Fact]
    public void Saving_missing_payload_fails_resend()
    {
        var saga = Saga(epoch: 1);
        var policy = new SavingTimeoutPolicy(WrapOpts());
        var probe = Found(Snapshot(TicketStatus.Analyzing, epoch: 1));

        var decision = policy.Decide(
            probe,
            Ctx(saga, TestOptions().Saving, verifyAttempt: 2));

        Assert.Equal(SagaTimeoutOutcome.Fail, decision.Outcome);
        Assert.Contains("Category or Suggestion", decision.Reason);
    }

    [Fact]
    public void Saving_valid_payload_resends_save()
    {
        var saga = Saga(category: "IT", suggestion: "Help", epoch: 1);
        var policy = new SavingTimeoutPolicy(WrapOpts());
        var probe = Found(Snapshot(TicketStatus.Analyzing, epoch: 1));

        var decision = policy.Decide(
            probe,
            Ctx(saga, TestOptions().Saving, verifyAttempt: 2));

        Assert.Equal(SagaTimeoutOutcome.ResendSave, decision.Outcome);
    }

    // --- Compensating ---

    [Fact]
    public void Compensating_already_reverted_completes()
    {
        var saga = Saga(originalStatus: TicketStatus.New);
        var policy = new CompensatingTimeoutPolicy(WrapOpts());
        var probe = Found(Snapshot(TicketStatus.New, epoch: 2, activeSaga: Guid.Empty, hasSuggestion: false));

        var decision = policy.Decide(probe, Ctx(saga, TestOptions().Compensating));

        Assert.Equal(SagaTimeoutOutcome.Complete, decision.Outcome);
    }

    [Fact]
    public void Compensating_still_owned_resends_compensate()
    {
        var saga = Saga();
        var policy = new CompensatingTimeoutPolicy(WrapOpts());
        var probe = Found(Snapshot(TicketStatus.Analyzing, epoch: 1, activeSaga: SagaId));

        var decision = policy.Decide(
            probe,
            Ctx(saga, TestOptions().Compensating, verifyAttempt: 2));

        Assert.Equal(SagaTimeoutOutcome.ResendCompensate, decision.Outcome);
    }

    [Fact]
    public void Compensating_resend_limit_fails()
    {
        var saga = Saga();
        var policy = new CompensatingTimeoutPolicy(WrapOpts());
        var probe = Found(Snapshot(TicketStatus.Analyzing, epoch: 1, activeSaga: SagaId));

        var decision = policy.Decide(
            probe,
            Ctx(saga, TestOptions().Compensating, resendCount: 1, postResendVerifyAttempt: 1));

        Assert.Equal(SagaTimeoutOutcome.Fail, decision.Outcome);
    }

    [Fact]
    public void Compensating_probe_unavailable_retries_before_fail()
    {
        var saga = Saga();
        var policy = new CompensatingTimeoutPolicy(WrapOpts());
        var probe503 = new TicketProgressProbeResult(
            TicketProgressProbeStatus.Unavailable,
            null,
            "HTTP 503 Service Unavailable");

        var retry = policy.Decide(probe503, Ctx(saga, TestOptions().Compensating, verifyAttempt: 0));
        Assert.Equal(SagaTimeoutOutcome.RetryVerify, retry.Outcome);
        Assert.Contains("unavailable", retry.Reason, StringComparison.OrdinalIgnoreCase);

        var stillRetry = policy.Decide(probe503, Ctx(saga, TestOptions().Compensating, verifyAttempt: 1));
        Assert.Equal(SagaTimeoutOutcome.RetryVerify, stillRetry.Outcome);

        var fail = policy.Decide(probe503, Ctx(saga, TestOptions().Compensating, verifyAttempt: 2));
        Assert.Equal(SagaTimeoutOutcome.Fail, fail.Outcome);
        Assert.Contains("unable to verify", fail.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("compensation failed", fail.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Compensation resend limit", fail.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compensating_resend_exhausted_fail_is_not_probe_unavailable()
    {
        var saga = Saga();
        var policy = new CompensatingTimeoutPolicy(WrapOpts());
        var probe = Found(Snapshot(TicketStatus.Analyzing, epoch: 1, activeSaga: SagaId));

        var decision = policy.Decide(
            probe,
            Ctx(saga, TestOptions().Compensating, resendCount: 1, postResendVerifyAttempt: 1));

        Assert.Equal(SagaTimeoutOutcome.Fail, decision.Outcome);
        Assert.Contains("Compensation resend limit", decision.Reason);
        Assert.DoesNotContain("unable to verify", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(FaultInjection.ForceSkipMarkAnalyzing)]
    [InlineData(FaultInjection.ForceSkipSaveSuggestionEvent)]
    [InlineData(FaultInjection.ForceSkipCompensateRevertedEvent)]
    [InlineData(FaultInjection.ForceAiFail)]
    public void FaultInjection_markers_are_defined(string marker)
    {
        Assert.False(string.IsNullOrWhiteSpace(marker));
        Assert.StartsWith("__", marker);
    }
}
