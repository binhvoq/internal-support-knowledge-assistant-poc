using SupportPoc.AiOrchestrator.Services;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class HttpTicketSuggestionReconcileClientTests
{
    [Theory]
    [InlineData(typeof(HttpRequestException), true)]
    [InlineData(typeof(TaskCanceledException), true)]
    [InlineData(typeof(InvalidOperationException), false)]
    public void IsTransient_recognizes_transient_failures(Type exceptionType, bool expected)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType, "test")!;
        Assert.Equal(expected, HttpTicketSuggestionReconcileClient.IsTransient(ex));
    }
}
