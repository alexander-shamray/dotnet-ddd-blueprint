using Common.Contracts.Catalog.V1;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ordering.Infrastructure.Messaging;

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
    /// <summary>
    /// §9.4's and §9.8's projection endpoint, by the name both chapters print.
    /// A constant rather than a literal because two other things have to agree
    /// with it: the <c>Endpoint</c> column §9.5's inbox keys each row on, and
    /// the assertions that read those rows back.
    /// </summary>
    /// <remarks>
    /// Public, unlike §9.6's <c>Endpoints</c> class, and the two are answering
    /// different questions. That one holds <c>queue:</c> addresses the saga
    /// <em>sends</em> to and is internal because nothing outside this assembly
    /// sends; this is the name a queue is <em>declared</em> under, which is
    /// what the inbox row records and therefore what a test has to be able to
    /// name.
    /// </remarks>
    public const string CatalogEventsQueue = "ordering-catalog-events";

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
            throw new InvalidOperationException(
                "ConnectionStrings:RabbitMq is not configured. The bus cannot start without it (§13.5).");

        services.AddMassTransit(x =>
        {
            // MassTransit 8.5 reports anonymous usage data to a vendor
            // endpoint after the bus starts, enabled by default. §13.2 owns
            // this platform's telemetry, and none of it leaves silently.
            x.DisableUsageTelemetry();

            // §3.2's Consumes column, Catalog's third of it. Registering a
            // consumer and binding it are two different statements and both
            // are needed: this one makes the type resolvable, and the
            // ConfigureConsumer calls below are what put it on a queue. A
            // consumer registered and never bound receives nothing, and looks
            // exactly like one that does.
            x.AddConsumer<IntegrationEventConsumer<ProductPublished>>();
            x.AddConsumer<IntegrationEventConsumer<PriceChanged>>();
            x.AddConsumer<IntegrationEventConsumer<ProductDiscontinued>>();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(connectionString));

                // §9.8's projection endpoint, verbatim. Ordering's other two —
                // ordering-commands and ordering-fulfilment-saga — arrive with
                // the saga that needs them (§9.6), each with its own policy.
                cfg.ReceiveEndpoint(
                    CatalogEventsQueue,
                    e =>
                    {
                        // §9.8's plain exponential five, with nothing
                        // excluded the way ordering-commands excludes
                        // ContractMappingException. The faults worth retrying
                        // dominate — a deadlock or a dropped connection is
                        // exactly what a backoff is for — but they are not the
                        // only ones this endpoint can raise, and claiming so
                        // would be false about the assembly's own throw:
                        // IntegrationEventConsumer<T> fails when the §6.2 scan
                        // registered no handler, which no backoff repairs.
                        // That still reaches the error queue, five attempts
                        // later than it might, and §9.4 wants it there: a
                        // misconfigured endpoint should be loud rather than
                        // quick. An exclusion list is what changes if that
                        // ever stops being an acceptable trade.
                        e.UseMessageRetry(r =>
                            r.Exponential(
                                retryLimit: 5,
                                minInterval: TimeSpan.FromSeconds(1),
                                maxInterval: TimeSpan.FromMinutes(1),
                                intervalDelta: TimeSpan.FromSeconds(2)));

                        // BEFORE the in-memory outbox, which is a correctness
                        // rule rather than a preference (§9.8): filters added
                        // first are outermost, and the outbox flushes its
                        // buffered sends AFTER the inner pipeline returns. The
                        // other order commits the inbox row first, so a failed
                        // flush leaves a message acknowledged, its sends lost,
                        // and the redelivery suppressed by the filter's own
                        // row. This projection publishes nothing today and the
                        // order still matters, because the day it does is not
                        // the day anybody re-reads this block.
                        //
                        // The context argument is not decoration and §9.8's
                        // sample did not have it: the parameterless overload
                        // carries CS0618 at the 8.5.3 pin, which ADR-019 makes
                        // an error, so the chapter's line had been
                        // unbuildable since it was written. Both chapters were
                        // amended.
                        e.UseConsumeFilter(typeof(InboxFilter<>), context);
                        e.UseInMemoryOutbox(context);

                        // One line per event in §3.2's Consumes column that
                        // Catalog owns. A handler with no line here is never
                        // invoked and looks correct while doing nothing, which
                        // is why the set is asserted rather than read off this
                        // file.
                        e.ConfigureConsumer<IntegrationEventConsumer<ProductPublished>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<PriceChanged>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<ProductDiscontinued>>(context);
                    });

                // No ConfigureEndpoints, and its removal is this PR's, on a
                // measurement rather than on taste. PR-13 left the call here
                // with a comment calling it "the line every later consumer
                // rides in on"; the first later consumer is the one above, and
                // what that line actually does for a registered consumer with
                // no explicit binding is manufacture a queue named after the
                // consumer type — with NEITHER the inbox filter NOR the retry
                // policy, because both are configured per endpoint and this
                // one was configured by nobody.
                //
                // §9.8 is explicit that every receive endpoint applies
                // InboxFilter<>, and that the saga is the one exception
                // because its state is its idempotency check — "any other
                // opt-out needs the same kind of written justification, in the
                // endpoint that takes it". An endpoint MassTransit invents
                // takes that opt-out and writes nothing down.
                //
                // Measured both ways by deleting the ProductPublished line
                // above and running CatalogEventEndpointTests. With
                // ConfigureEndpoints present the event was still projected and
                // no inbox row was written — at-least-once delivery landing on
                // a handler with no duplicate suppression — and only one of
                // the three tests noticed. With it gone all three go red,
                // because a forgotten binding is then a message nobody
                // consumes rather than one consumed off the record.
                //
                // The cost is stated rather than dodged: a consumer added
                // later needs a line here as well as an AddConsumer, and
                // nothing at startup complains if it gets one and not the
                // other. That is the trade — a gap that fails visibly against
                // a convenience that fails quietly.
            });
        });

        // No readiness line here or in AddOrderingInfrastructure, and that is
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
