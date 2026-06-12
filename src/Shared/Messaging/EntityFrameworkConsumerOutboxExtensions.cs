using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;

namespace SupportPoc.Shared.Messaging;

public static class EntityFrameworkConsumerOutboxExtensions
{
    /// <summary>
    /// Applies MassTransit EF consumer outbox to every receive endpoint created by <c>ConfigureEndpoints</c>.
    /// Duplicate transport deliveries are suppressed via <c>InboxState</c> (keyed by <c>MessageId</c>).
    /// </summary>
    public static void AddEntityFrameworkConsumerOutbox<TDbContext>(this IBusRegistrationConfigurator bus)
        where TDbContext : DbContext
    {
        bus.AddConfigureEndpointsCallback((context, _, endpoint) =>
        {
            if (endpoint is IReceiveEndpointConfigurator receiveEndpoint)
                receiveEndpoint.UseEntityFrameworkOutbox<TDbContext>(context);
        });
    }
}
