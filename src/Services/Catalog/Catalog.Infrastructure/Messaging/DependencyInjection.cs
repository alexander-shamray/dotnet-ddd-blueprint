using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Catalog.Infrastructure.Messaging;

/// <summary>
/// The bus registration of §9, in the folder-scoped shape
/// <c>Common.Infrastructure/Redis</c> established. Per-service rather than
/// common, because this is where a service's consumers, sagas and receive
/// endpoints are configured (§9.6 registers Ordering's saga inside its
/// <c>AddMassTransitMessaging</c>) — and because no common code names a
/// MassTransit type until PR-14's outbox, so a shared home today would be a
/// package reference nothing uses.
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
        // service's log.
        string connectionString = configuration.GetConnectionString("RabbitMq") ??
            throw new InvalidOperationException(
                "ConnectionStrings:RabbitMq is not configured. The bus cannot start without it (§13.5).");

        services.AddMassTransit(x =>
        {
            // MassTransit 8.5 reports anonymous usage data to a vendor
            // endpoint after the bus starts, enabled by default. §13.2 owns
            // this platform's telemetry, and none of it leaves silently.
            x.DisableUsageTelemetry();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(connectionString));

                // Nothing to configure until PR-15's consumers — the call
                // stays because it is the line every later consumer rides in
                // on, and its absence is the silent kind of wrong. Retry is
                // deliberately absent too: §9.8 configures it per receive
                // endpoint, and there are none.
                cfg.ConfigureEndpoints(context);
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
