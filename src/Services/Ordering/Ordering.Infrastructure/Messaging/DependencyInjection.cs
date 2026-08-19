using Common.Application;
using Common.Contracts.Catalog.V1;
using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Application.Orders.CancelOrder;
using Ordering.Application.Orders.ConfirmOrder;
using Ordering.Application.Orders.FlagOrderForReview;
using Ordering.Application.Orders.MarkOrderShipped;
using Ordering.Infrastructure.Persistence;

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

    /// <summary>
    /// §9.4's command endpoint — the four commands §3.2 says Ordering accepts.
    /// The name must match <c>Endpoints.OrderingQueue</c>, or the saga sends
    /// into a void: a command addressed to an undeclared queue is not an error.
    /// </summary>
    public const string CommandsQueue = "ordering-commands";

    /// <summary>
    /// §9.8's saga endpoint, which receives the fulfilment events of §9.6.
    /// </summary>
    public const string FulfilmentSagaQueue = "ordering-fulfilment-saga";

    /// <summary>
    /// Ordering's fourth receive endpoint, and the one §9.8 did not have.
    /// </summary>
    /// <remarks>
    /// <b>It exists because <c>StockReserved</c> means two things to this
    /// service and only one of them is the saga's.</b> The saga reads it to
    /// decide what to ask for next; the order has to record that its stock is
    /// held, which is <c>Order.ConfirmStock</c> (§5.4) — a transition the
    /// blueprint documented with no caller until this PR. §3.2 already lists
    /// <c>StockReserved</c> in Ordering's Consumes column and closes its
    /// Accepts column at four commands, so what was missing was a consumer
    /// rather than a contract.
    /// <para>
    /// It does not share the saga endpoint below. That was originally because
    /// the saga endpoint carried no inbox filter — an exemption PR-21 removed —
    /// and what remains is the retry policy: a consumer there would inherit one
    /// written for a state machine, whose failures are inapplicable transitions
    /// rather than the domain rejections <c>Order.ConfirmStock</c> produces.
    /// Two failure vocabularies, two queues.
    /// </para>
    /// </remarks>
    public const string StockEventsQueue = "ordering-stock-events";

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

            // §3.2's Consumes column, Catalog's third of it. Registering a
            // consumer and binding it are two different statements and both
            // are needed: this one makes the type resolvable, and the
            // ConfigureConsumer calls below are what put it on a queue. A
            // consumer registered and never bound receives nothing, and looks
            // exactly like one that does.
            x.AddConsumer<IntegrationEventConsumer<ProductPublished>>();
            x.AddConsumer<IntegrationEventConsumer<PriceChanged>>();
            x.AddConsumer<IntegrationEventConsumer<ProductDiscontinued>>();

            // Inventory's, and the reason it is not on the saga's list below:
            // the saga consumes the same event through its own correlation.
            // Two consumers of one fact, because it means two things here —
            // argued at StockEventsQueue.
            x.AddConsumer<IntegrationEventConsumer<StockReserved>>();

            // §3.2's Accepts column, and exactly it. Each closed generic is a
            // separate registration because CommandConsumer<,> is common code
            // and the container builds the closed type (§9.4).
            x.AddConsumer<CommandConsumer<CancelOrder, CancelOrderCommand>>();
            x.AddConsumer<CommandConsumer<ConfirmOrder, ConfirmOrderCommand>>();
            x.AddConsumer<CommandConsumer<MarkOrderShipped, MarkOrderShippedCommand>>();
            x.AddConsumer<CommandConsumer<FlagOrderForReview, FlagOrderForReviewCommand>>();

            // §9.6's state machine, over the service's own database. The
            // repository is not optional: MassTransit throws at startup
            // without one, and the in-memory repository §12.5 uses in tests
            // discards every in-flight order on restart.
            x
                .AddSagaStateMachine<OrderFulfilmentSaga, OrderFulfilmentState>()
                .EntityFrameworkRepository(r =>
                {
                    r.ExistingDbContext<OrderingDbContext>();
                    // Pessimistic: two events for the same order can arrive
                    // concurrently — StockReserved and a timeout — and
                    // optimistic retry on a state machine replays transitions
                    // that already ran.
                    r.ConcurrencyMode = ConcurrencyMode.Pessimistic;
                });

            // The scheduler §9.6's four Schedule declarations need, and the
            // thing no chapter specified until ADR-021. This half registers
            // IMessageScheduler; the UseDelayedMessageScheduler line inside
            // the transport callback is what puts MessageSchedulerContext on
            // the consume pipeline, which is where a saga activity reaches for
            // it. Either one alone leaves .Schedule(…) throwing at the first
            // OrderPlaced — and only at the first one, since nothing resolves
            // a scheduler at startup.
            x.AddDelayedMessageScheduler();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(connectionString));

                // The transport half of ADR-021's scheduler. On RabbitMQ this
                // is the delayed message exchange, which is a PLUGIN rather
                // than a broker feature — deploy/compose builds the image that
                // carries it. On a broker without it the bus still starts
                // clean and the first schedule HANGS — the declare is refused
                // and MassTransit retries it for ever (ADR-021 has the
                // measurement). The ADR argues the choice and names what it
                // costs.
                cfg.UseDelayedMessageScheduler();

                // §9.8's projection endpoint, verbatim. Ordering's other three
                // follow it, each with its own policy.
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

                // §9.4's command endpoint, and the one whose retry policy is
                // not the plain exponential five.
                cfg.ReceiveEndpoint(
                    CommandsQueue,
                    e =>
                    {
                        e.UseMessageRetry(r =>
                        {
                            // A malformed contract does not parse itself on the
                            // fourth attempt. Retrying it burns a minute of
                            // backoff and delays every message behind it before
                            // reaching the same error queue.
                            //
                            // Domain rejections are not on this list because
                            // they never throw — CommandConsumer acks, counts
                            // and logs them (§9.8). The list is for faults that
                            // are terminal, not for outcomes that are not
                            // faults at all.
                            r.Ignore<ContractMappingException>();

                            r.Exponential(
                                retryLimit: 5,
                                minInterval: TimeSpan.FromSeconds(1),
                                maxInterval: TimeSpan.FromMinutes(1),
                                intervalDelta: TimeSpan.FromSeconds(2));
                        });

                        // Inbox outside the in-memory outbox, for the reason
                        // the projection endpoint states above: the other
                        // nesting commits the inbox row before the buffered
                        // sends have flushed.
                        e.UseConsumeFilter(typeof(InboxFilter<>), context);
                        e.UseInMemoryOutbox(context);

                        // One per command in §3.2's Accepts column. The saga
                        // sends four; a type missing here is sent into a queue
                        // that ignores it.
                        e.ConfigureConsumer<CommandConsumer<CancelOrder, CancelOrderCommand>>(context);
                        e.ConfigureConsumer<CommandConsumer<ConfirmOrder, ConfirmOrderCommand>>(context);
                        e.ConfigureConsumer<CommandConsumer<MarkOrderShipped, MarkOrderShippedCommand>>(context);
                        e.ConfigureConsumer<CommandConsumer<FlagOrderForReview, FlagOrderForReviewCommand>>(context);
                    });

                // Ordering's own reaction to Inventory's reservation, kept off
                // the saga endpoint below because that one carries no inbox
                // filter. Same policy as the projection endpoint — this is a
                // consumer like any other.
                cfg.ReceiveEndpoint(
                    StockEventsQueue,
                    e =>
                    {
                        e.UseMessageRetry(r =>
                            r.Exponential(
                                retryLimit: 5,
                                minInterval: TimeSpan.FromSeconds(1),
                                maxInterval: TimeSpan.FromMinutes(1),
                                intervalDelta: TimeSpan.FromSeconds(2)));

                        e.UseConsumeFilter(typeof(InboxFilter<>), context);
                        e.UseInMemoryOutbox(context);

                        e.ConfigureConsumer<IntegrationEventConsumer<StockReserved>>(context);
                    });

                // §9.8's saga endpoint.
                cfg.ReceiveEndpoint(
                    FulfilmentSagaQueue,
                    e =>
                    {
                        e.UseMessageRetry(r =>
                            r.Exponential(
                                retryLimit: 5,
                                minInterval: TimeSpan.FromSeconds(1),
                                maxInterval: TimeSpan.FromMinutes(1),
                                intervalDelta: TimeSpan.FromSeconds(2)));

                        // **The inbox is here, and §9.8's exemption is gone.**
                        // That exemption said a saga is idempotent by
                        // construction — a redelivered StockReserved finds the
                        // instance already past AwaitingStock and the
                        // transition is not applicable. True, and an argument
                        // about NON-INITIAL events only.
                        //
                        // OrderPlaced is handled in Initially, and
                        // SetCompletedWhenFinalized deletes the row — so
                        // MassTransit's initial-event policy creates a NEW
                        // instance whenever none exists. §9.4 guarantees
                        // at-least-once, so a duplicate arriving after the
                        // workflow finished reserves stock and authorises
                        // payment a second time. Reproduced as a failing test
                        // against the real broker before this line was added.
                        //
                        // The exemption's stated cost was wrong too: an inbox
                        // row does NOT suppress redelivery after a
                        // mid-transition crash, because InboxFilter writes its
                        // row after the inner pipe returns (§9.5). A crash
                        // mid-transition leaves no row, and the redelivery
                        // does the work again.
                        e.UseConsumeFilter(typeof(InboxFilter<>), context);
                        e.UseInMemoryOutbox(context);

                        e.ConfigureSaga<OrderFulfilmentState>(context);
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
                // InboxFilter<> — with no exception at all since PR-21, the
                // saga's having turned out to be a defect rather than a
                // decision. An endpoint MassTransit invents opts out of it
                // anyway and writes nothing down, which is the whole objection:
                // the rule is not "the inbox unless you argue otherwise", it is
                // "the inbox", and an invented endpoint cannot argue.
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
