namespace SupportPoc.AiOrchestrator.Options;

// Cau hinh timeout cho saga TicketSuggestion.
// Default 5 phut cho production. Test co the override xuong vai chuc giay
// de verify scenario timeout fire ma khong phai cho lau.
public sealed class SagaTimeoutOptions
{
    public const string SectionName = "Saga";

    // Sau bao nhieu giay khong nhan dien event tiep theo thi Timeout.Received fire.
    public int TimeoutSeconds { get; set; } = 300;
}
