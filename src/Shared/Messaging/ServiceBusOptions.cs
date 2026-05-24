namespace SupportPoc.Shared.Messaging;

public sealed class ServiceBusOptions
{
    public const string SectionName = "ServiceBus";
    public string? ConnectionString { get; set; }
    public string TopicName { get; set; } = "support-events";
    public bool Enabled => !string.IsNullOrWhiteSpace(ConnectionString);
}
