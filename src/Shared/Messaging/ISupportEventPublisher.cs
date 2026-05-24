using SupportPoc.Shared.Events;

namespace SupportPoc.Shared.Messaging;

public interface ISupportEventPublisher
{
    Task PublishAsync<TPayload>(string eventType, TPayload payload, CancellationToken cancellationToken = default);
}
