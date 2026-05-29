namespace SupportPoc.Shared.Testing;

// Cac marker dung de inject fault qua noi dung Question.
// Khi text Question chua marker, consumer/service tuong ung se mo phong loi.
// Dung de verify cac failure-mode patterns (compensation/timeout/DLQ) tu HTTP boundary
// ma KHONG can sua code production hay tat dependency that su.
// QUAN TRONG: chi nen bat trong moi truong dev/test. Production nen filter o gateway.
public static class FaultInjection
{
    // Force AiPipelineService throw -> RunAiPipelineConsumer catch va publish AiPipelineFailed
    // -> saga vao Compensating -> ticket revert ve OriginalStatus.
    public const string ForceAiFail = "__FAIL_AI__";

    // RunAiPipelineConsumer throw KHONG catch -> MassTransit retry 5 lan -> dead-letter queue.
    // Dung de verify DLQ pattern thuc su hoat dong.
    public const string ForcePoisonAi = "__POISON_AI__";

    // MarkTicketAnalyzingConsumer silent return (khong publish event nao) -> saga ket o Analyzing
    // -> Timeout fire -> saga sang Failed.
    public const string ForceSkipMarkAnalyzing = "__SKIP_MARK__";

    // SaveTicketSuggestionConsumer commit DB Suggested nhung KHONG publish TicketSuggestionSaved.
    // Dung de verify Saving-timeout suspect path: orchestrator phai probe source-of-truth, khong compensate mu.
    public const string ForceSkipSaveSuggestionEvent = "__SKIP_SAVE_EVENT__";

    // CompensateMarkAnalyzingConsumer revert DB nhung KHONG publish MarkAnalyzingReverted.
    // Dung de verify Compensating-timeout probe -> Compensated (tranh Failed gia).
    public const string ForceSkipCompensateRevertedEvent = "__SKIP_COMPENSATE_EVENT__";

    // RunAiPipelineConsumer ghi AiDraft vao DB nhung KHONG publish AiPipelineCompleted.
    // Dung de verify RunningAi-timeout probe -> Proceed (khong resend LLM).
    public const string ForceSkipAiPipelineCompletedEvent = "__SKIP_AI_EVENT__";

    public static bool Has(this string? text, string marker)
        => !string.IsNullOrEmpty(text) && text.Contains(marker, StringComparison.OrdinalIgnoreCase);
}
