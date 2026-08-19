using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Catalog.Infrastructure.Messaging;

/// <summary>
/// The bus registration of §9, in the folder-scoped shape
/// <c>Common.Infrastructure/Redis</c> established. Per-service rather than
/// common, because this is where a service's consumers, sagas and receive
/// endpoints are configured (§9.6 registers Ordering's saga inside its
/// <c>AddMassTransitMessaging</c>). Common code does name a MassTransit type
/// since PR-14 — <c>IPublishEndpoint</c>, on the Broker half of the outbox
/// dispatcher — and that changed nothing here: what keeps this per-service is
/// the transport, not the reference. <c>UsingRabbitMq</c>, the consumers and
/// the receive endpoints are each service's own.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddMassTransitMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Eager, like AddSqlServer's throw one file over: a host with no
        // broker configured must not start. Read inside UsingRabbitMq's
        // callback instead, the missing key would surface at bus start —
        // after the host is up, past ValidateOnBuild, in a background
        // service's log. IsNullOrWhiteSpace, not a null check, on the Redis
        // helper's argument: an empty environment variable configures an
        // empty string, and letting it through defers the failure to the
        // same place the eager read exists to avoid.
        string? connectionString = configuration.GetConnectionString("RabbitMq");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:RabbitMq is not configured. The bus cannot start without it (§13.5).");
        }

        services.AddMassTransit(x =>
        {
            // MassTransit 8.5 reports anonymous usage data to a vendor
            // endpoint after the bus starts, enabled by default. §13.2 owns
            // this platform's telemetry, and none of it leaves silently.
            x.DisableUsageTelemetry();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(connectionString));

                // Nothing further. This service binds no receive endpoint,
                // so there is no retry policy either: §9.8 configures retry
                // per receive endpoint, and there are none.
                //
                // There is deliberately no ConfigureEndpoints(context) call.
                // It is inert while no consumer is registered, and what it
                // does once one is is manufacture a queue named after the
                // consumer type — carrying NEITHER the inbox filter NOR the
                // retry policy, both being per-endpoint configuration that an
                // invented endpoint never receives. §9.8 permits an endpoint
                // without the inbox exactly once, for the saga, and requires
                // that opt-out to be written down where it is taken; a queue
                // MassTransit invents takes it and writes nothing. So a
                // consumer added here needs an explicit ReceiveEndpoint with
                // its own policy, which is what the absence of this line
                // forces.
            });
        });

        // No readiness line here or in AddCatalogInfrastructure, and that is
        // a decision: AddMassTransit registers the bus health check itself —
        // "masstransit-bus", tagged ready — so §13.5's predicate picks it up
        // with nothing further. MassTransitHostOptions stays at its defaults
        // (WaitUntilStarted = false): the host starts while the bus connects
        // in the background, and readiness carries the wait — blocking
        // startup on the broker would turn a RabbitMQ outage into a pod that
        // cannot boot, §13.5's restart-storm argument one dependency over.
        return services;
    }
}
