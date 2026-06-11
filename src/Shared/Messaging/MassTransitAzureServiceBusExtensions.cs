using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using MassTransit;
using MassTransit.AzureServiceBusTransport;
using SupportPoc.Shared.Contracts;

namespace SupportPoc.Shared.Messaging;

public static class MassTransitAzureServiceBusExtensions
{
    /// <summary>Emulator max entity TTL/auto-delete is 1 hour (not 366 days).</summary>
    public static readonly TimeSpan EmulatorMaxEntityLifetime = TimeSpan.FromHours(1);

    public static void ApplyDevelopmentEmulatorTopologyDefaults()
    {
        Defaults.DefaultMessageTimeToLive = EmulatorMaxEntityLifetime;
        Defaults.BasicMessageTimeToLive = EmulatorMaxEntityLifetime;
        Defaults.AutoDeleteOnIdle = EmulatorMaxEntityLifetime;
    }

    public static void AddSupportPocAzureServiceBusHost(
        this IBusRegistrationConfigurator bus,
        ServiceBusOptions serviceBus,
        Action<IBusRegistrationContext, IServiceBusBusFactoryConfigurator> configure)
    {
        if (serviceBus.IsDevelopmentEmulator)
        {
            ApplyDevelopmentEmulatorTopologyDefaults();
            bus.AddConfigureEndpointsCallback((_, endpoint) =>
            {
                if (endpoint is IServiceBusEndpointConfigurator serviceBusEndpoint)
                {
                    serviceBusEndpoint.DefaultMessageTimeToLive = EmulatorMaxEntityLifetime;
                    serviceBusEndpoint.AutoDeleteOnIdle = EmulatorMaxEntityLifetime;
                }
            });
        }

        bus.UsingAzureServiceBus((context, cfg) =>
        {
            var messagingConnectionString = serviceBus.GetMessagingConnectionString()!;

            if (serviceBus.IsDevelopmentEmulator)
            {
                var adminConnectionString = serviceBus.GetAdministrationConnectionString()!;
                cfg.Host(
                    ToServiceBusUri(messagingConnectionString),
                    new ServiceBusClient(messagingConnectionString),
                    new ServiceBusAdministrationClient(adminConnectionString));

                cfg.AddPublishMessageTypesFromNamespaceContaining<ITicketCreated>(ApplyEmulatorPublishTopology);
            }
            else
            {
                cfg.Host(messagingConnectionString);
            }

            configure(context, cfg);
        });
    }

    private static void ApplyEmulatorPublishTopology(IServiceBusMessagePublishTopologyConfigurator topology, Type _)
    {
        topology.DefaultMessageTimeToLive = EmulatorMaxEntityLifetime;
        topology.AutoDeleteOnIdle = EmulatorMaxEntityLifetime;
    }

    private static Uri ToServiceBusUri(string connectionString)
    {
        if (!ServiceBusOptions.TryParseEndpoint(connectionString, out var endpoint))
            throw new InvalidOperationException("ServiceBus connection string khong parse duoc Endpoint.");

        return endpoint.Port is > 0
            ? new Uri($"sb://{endpoint.Host}:{endpoint.Port}")
            : new Uri($"sb://{endpoint.Host}");
    }
}
