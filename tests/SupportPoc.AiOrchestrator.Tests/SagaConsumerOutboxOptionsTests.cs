using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Options;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class SagaConsumerOutboxOptionsTests
{
    [Fact]
    public void UseSagaConsumerOutbox_defaults_to_false_for_sqlite_poc()
    {
        var options = new AutoSuggestionOptions();
        Assert.False(options.UseSagaConsumerOutbox);
    }

    [Fact]
    public void UseSagaConsumerOutbox_binds_from_configuration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AutoSuggestion:UseSagaConsumerOutbox"] = "true"
            })
            .Build();

        var bound = config.GetSection(AutoSuggestionOptions.SectionName).Get<AutoSuggestionOptions>();
        Assert.NotNull(bound);
        Assert.True(bound.UseSagaConsumerOutbox);
    }
}
