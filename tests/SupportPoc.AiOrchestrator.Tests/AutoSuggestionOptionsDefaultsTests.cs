using SupportPoc.AiOrchestrator.Options;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class AutoSuggestionOptionsDefaultsTests
{
    [Fact]
    public void StepTimeoutSeconds_default_is_at_least_ai_generation_lease()
    {
        var options = new AutoSuggestionOptions();

        Assert.True(
            options.StepTimeoutSeconds >= options.AiGenerationLeaseSeconds,
            $"StepTimeoutSeconds ({options.StepTimeoutSeconds}) phai >= AiGenerationLeaseSeconds ({options.AiGenerationLeaseSeconds}).");
    }

    [Fact]
    public void AiGenerationHardTimeoutSeconds_default_is_at_least_lease()
    {
        var options = new AutoSuggestionOptions();

        Assert.True(
            options.AiGenerationHardTimeoutSeconds >= options.AiGenerationLeaseSeconds,
            $"AiGenerationHardTimeoutSeconds ({options.AiGenerationHardTimeoutSeconds}) phai >= AiGenerationLeaseSeconds ({options.AiGenerationLeaseSeconds}).");
    }
}
