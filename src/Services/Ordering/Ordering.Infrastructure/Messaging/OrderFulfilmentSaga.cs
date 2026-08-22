using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;
using MassTransit;
using static Ordering.Infrastructure.Messaging.Endpoints;

namespace Ordering.Infrastructure.Messaging;

/// <summary>
/// §9.6's order fulfilment saga: the workflow across Inventory, Payments and
/// Shipping, coordinated without a distributed transaction. Each forward step
/// has a compensating action and every wait has a timeout.
/// </summary>
/// <remarks>
/// <b>Commands are sent; events are published.</b> <c>ReserveStock</c>,
/// <c>ReleaseStock</c>, <c>AuthorisePayment</c>, <c>CancelOrder</c>,
/// <c>ConfirmOrder</c>, <c>MarkOrderShipped</c> and <c>FlagOrderForReview</c>
/// are commands — imperative, addressed to exactly one owning service.
/// Publishing one delivers it to every subscriber that happened to bind the
/// type, so a second service starts silently executing this platform's
/// business commands (§9.6).
/// <para>
/// The class is public because MassTransit's registration resolves it from the
/// container by type; nothing else in this assembly names it.
/// </para>
/// </remarks>
public sealed class OrderFulfilmentSaga : MassTransitStateMachine<OrderFulfilmentState>
{
    // Every state in §9.6's diagram, including the ones a saga could
    // technically skip by finalising early. Confirmed exists because the order
    // is not done at payment — it is waiting for despatch, and a wait the
    // machine cannot represent is a wait it cannot time out.
    //
    // Cancelled and Shipped are NOT states here: they are terminal outcomes,
    // and SetCompletedWhenFinalized() deletes the instance at that point, so a
    // state for either would be one no saga is ever observed in.
    public State AwaitingStock { get; private set; } = null!;
    public State AwaitingPayment { get; private set; } = null!;
    public State Confirmed { get; private set; } = null!;
    public State Compensating { get; private set; } = null!;

    public Event<OrderPlaced> OrderPlaced { get; private set; } = null!;
    public Event<StockReserved> StockReserved { get; private set; } = null!;
    public Event<StockReservationFailed> StockReservationFailed { get; private set; } = null!;
    public Event<PaymentAuthorised> PaymentAuthorised { get; private set; } = null!;
    public Event<PaymentDeclined> PaymentDeclined { get; private set; } = null!;
    public Event<StockReleased> StockReleased { get; private set; } = null!;
    public Event<ShipmentDispatched> ShipmentDispatched { get; private set; } = null!;

    // Ordering's own event, and the SECOND of the two here that are —
    // OrderPlaced above is the other, which the sentence that used to end
    // "and the only one here that is" contradicted in its own next clause.
    // §3.2 gives Ordering both for the same reason: a service is a subscriber
    // to itself whenever a fact it publishes is also a fact its workflow has
    // to react to.
    //
    // "Cancel this order" has two origins and only one of them was reaching
    // the machine. The saga's own CancelOrder is always paired with Finalize,
    // so the workflow ends with it; §11.4's customer endpoint cancels the
    // AGGREGATE and ended nothing, leaving the saga to reserve stock and
    // authorise a card for an order the customer had already cancelled.
    public Event<OrderCancelled> OrderCancelled { get; private set; } = null!;

    // One schedule per wait. "Every wait has a timeout" is a rule the machine
    // must be able to express, not a habit to remember at each transition.
    public Schedule<OrderFulfilmentState, StockReservationExpired> StockTimeout { get; private set; } = null!;
    public Schedule<OrderFulfilmentState, PaymentAuthorisationExpired> PaymentTimeout { get; private set; } = null!;
    public Schedule<OrderFulfilmentState, DespatchExpired> DespatchTimeout { get; private set; } = null!;
    public Schedule<OrderFulfilmentState, StockReleaseExpired> ReleaseTimeout { get; private set; } = null!;

    public OrderFulfilmentSaga()
    {
        InstanceState(x => x.CurrentState);

        // "Not applicable" has to be spelled, because the default is to throw.
        //
        // **The path that reaches this callback is narrower than an earlier
        // version of this comment claimed, and the narrow one is what the
        // trade below has to be justified against.** A republished outbox row
        // carries the SAME message id — OutboxMessage.Stage persists the
        // integration event's own id, and OutboxDispatcher restores it onto
        // every publish — so §9.5's inbox suppresses the ordinary completed
        // redelivery. What it cannot suppress is a redelivery whose inbox row
        // was never written: InboxFilter adds its row AFTER the inner pipe
        // returns, in a second SaveChangesAsync, so a crash between the saga
        // state committing and that write leaves the event unrecorded, and the
        // next delivery lands on an instance that has already moved on. A
        // producer staging one fact as two outbox rows is the other way in,
        // and that one is a defect rather than routine.
        //
        // MassTransit's default unhandled-event callback raises
        // UnhandledEventException, so §9.8's retry policy spends six attempts
        // on a transition that can never become applicable and files the
        // message in the error queue §13.6 pages on. The design considers that
        // duplicate correctly absorbed; this is the line that makes it so.
        //
        // Measured before it was written: a redelivered StockReserved in
        // AwaitingPayment came back as NotAcceptedStateMachineException. A
        // stale TIMEOUT does not — MassTransit filters a scheduled message
        // whose token id no longer matches the instance, which is why
        // ADR-021's uncancellable timeouts were harmless while this was not.
        //
        // What it costs is stated rather than discovered later: an event
        // genuinely misrouted to this queue is now silent. That is a
        // configuration fault, and it is traded for a routine one — and the
        // trade is only safe because every event this machine declares is
        // handled in every state it can reach one in, including the
        // Ignore(OrderCancelled) below, which is written rather than left to
        // this callback.
        OnUnhandledEvent(x => x.Ignore());

        // Correlated on the order in every case, which is also what §9.3's
        // mapper sets CorrelationId to — so one id follows the workflow across
        // every service that touches it.
        Event(() => OrderPlaced, x => x.CorrelateById(m => m.Message.OrderId));
        Event(() => StockReserved, x => x.CorrelateById(m => m.Message.OrderId));
        Event(() => StockReservationFailed, x => x.CorrelateById(m => m.Message.OrderId));
        Event(() => PaymentAuthorised, x => x.CorrelateById(m => m.Message.OrderId));
        Event(() => PaymentDeclined, x => x.CorrelateById(m => m.Message.OrderId));
        Event(() => StockReleased, x => x.CorrelateById(m => m.Message.OrderId));
        Event(() => ShipmentDispatched, x => x.CorrelateById(m => m.Message.OrderId));

        // Discarded when no instance exists, and this one needs saying because
        // it is the routine case rather than the exotic one: every cancellation
        // the saga itself causes ends in Finalize, and the OrderCancelled the
        // aggregate then publishes arrives at a queue whose instance has just
        // been deleted. Stated rather than inherited — a default that changed
        // would turn every cancelled order into an error-queue entry.
        //
        // **It states the default rather than changing it, and the difference
        // is measured**: a non-initial event correlating to no instance is
        // consumed CLEANLY — no transition, no fault, nothing on §13.6's
        // pager. A test pins that, because the residual below depends on it.
        //
        // **The residual is #123 and this line is where it lives.** A CUSTOMER
        // cancellation that overtakes its own OrderPlaced is discarded here
        // too, and the later placement then starts a live saga for an order
        // the aggregate has already cancelled. §9.4 orders nothing, and the
        // dispatcher's READPAST claim plus a retried publish are two ordinary
        // ways to get there. Telling the two apart is possible — Reason is a
        // CancelReasons code and only the customer's is customer_request — and
        // it is a §9.6 decision rather than a line to change here.
        Event(
            () => OrderCancelled,
            x =>
            {
                x.CorrelateById(m => m.Message.OrderId);
                x.OnMissingInstance(m => m.Discard());
            });

        Schedule(
            () => StockTimeout,
            x => x.StockTimeoutTokenId,
            s =>
            {
                s.Delay = TimeSpan.FromMinutes(5);
                s.Received = e => e.CorrelateById(m => m.Message.OrderId);
            });

        // Payment authorisation involves a third party and is the wait most
        // likely to hang. Longer than stock because a PSP retry is normal.
        Schedule(
            () => PaymentTimeout,
            x => x.PaymentTimeoutTokenId,
            s =>
            {
                s.Delay = TimeSpan.FromMinutes(15);
                s.Received = e => e.CorrelateById(m => m.Message.OrderId);
            });

        // Despatch is measured in days, and unlike the other two it has no
        // automatic compensation — payment is taken and stock is gone. The
        // timeout escalates to a human instead. A wait with no compensating
        // action still needs a bound; "no timeout" is not the alternative.
        Schedule(
            () => DespatchTimeout,
            x => x.DespatchTimeoutTokenId,
            s =>
            {
                s.Delay = TimeSpan.FromDays(3);
                s.Received = e => e.CorrelateById(m => m.Message.OrderId);
            });

        // Compensation is a wait like any other. Stock that is never released
        // is stock nobody can sell, and a saga stuck mid-compensation is the
        // worst place to be stuck — the order is already failing.
        Schedule(
            () => ReleaseTimeout,
            x => x.ReleaseTimeoutTokenId,
            s =>
            {
                s.Delay = TimeSpan.FromMinutes(10);
                s.Received = e => e.CorrelateById(m => m.Message.OrderId);
            });

        Initially(
            When(OrderPlaced)
                .Then(ctx =>
                {
                    ctx.Saga.OrderId = ctx.Message.OrderId;
                    ctx.Saga.CustomerId = ctx.Message.CustomerId;
                    ctx.Saga.Total = ctx.Message.TotalAmount;
                    ctx.Saga.Currency = ctx.Message.Currency;
                    ctx.Saga.StartedAt = ctx.Message.OccurredAt;
                })
                .Schedule(StockTimeout, ctx => new StockReservationExpired(ctx.Saga.OrderId))
                // Send, not Publish — these are commands with one owner.
                // Mapped, not forwarded: ReserveStock owns its line type, so
                // versioning OrderPlaced does not version Inventory's command.
                .Send(
                    InventoryQueue,
                    ctx => new ReserveStock(
                        ctx.Saga.OrderId,
                        [.. ctx.Message.Lines.Select(l => new StockLine(l.ProductId, l.Quantity))]))
                .TransitionTo(AwaitingStock));

        During(
            AwaitingStock,
            When(StockReserved)
                .Unschedule(StockTimeout)
                // Currency travels with the amount — a bare decimal is a
                // charge waiting to be made in the wrong denomination.
                .Send(
                    PaymentsQueue,
                    ctx => new AuthorisePayment(
                        ctx.Saga.OrderId,
                        ctx.Saga.CustomerId,
                        ctx.Saga.Total,
                        ctx.Saga.Currency))
                // Arm the next wait in the same activity that begins it.
                .Schedule(PaymentTimeout, ctx => new PaymentAuthorisationExpired(ctx.Saga.OrderId))
                .TransitionTo(AwaitingPayment),

            When(StockReservationFailed)
                .Unschedule(StockTimeout)
                // String codes, not the domain enum — a published contract
                // carrying one pins its member names as wire format (§9.6).
                .Send(
                    OrderingQueue,
                    ctx => new CancelOrder(ctx.Saga.OrderId, CancelReasons.OutOfStock))
                .Finalize(),

            When(StockTimeout.Received)
                .Send(
                    OrderingQueue,
                    ctx => new CancelOrder(ctx.Saga.OrderId, CancelReasons.StockTimeout))
                .Finalize(),

            // The customer cancelled while ReserveStock was in flight. Nothing
            // has been charged, and the reservation may or may not exist yet —
            // so this compensates rather than finalising, which is what the
            // Compensating state is for. It is the SAME shape as a declined
            // payment because it is the same situation: stock asked for, no
            // money taken, and a release that has to be waited on.
            //
            // The alternative was ReleaseStock followed by Finalize, and it was
            // rejected for losing the wait: a release nobody waits on is a
            // reservation nobody notices is stranded, and §9.6 already gives
            // compensation a timeout for exactly that reason.
            When(OrderCancelled)
                .Unschedule(StockTimeout)
                .Then(ctx => ctx.Saga.CancelReason = CancelReasons.CustomerRequest)
                .Send(InventoryQueue, ctx => new ReleaseStock(ctx.Saga.OrderId))
                .Schedule(ReleaseTimeout, ctx => new StockReleaseExpired(ctx.Saga.OrderId))
                .TransitionTo(Compensating));

        During(
            AwaitingPayment,
            When(PaymentAuthorised)
                .Unschedule(PaymentTimeout)
                .Send(
                    OrderingQueue,
                    ctx => new ConfirmOrder(ctx.Saga.OrderId, ctx.Message.Reference))
                // Not Finalize: the order is confirmed, not finished. It is now
                // waiting on Shipping, and that wait needs a state to live in.
                .Schedule(DespatchTimeout, ctx => new DespatchExpired(ctx.Saga.OrderId))
                .TransitionTo(Confirmed),

            When(PaymentDeclined)
                .Unschedule(PaymentTimeout)
                // Why we are compensating, recorded on entry. Both exits from
                // Compensating below are shared, and by the time one runs the
                // triggering event is gone — so the reason has to be state, not
                // something re-derived from the transition that is finishing.
                .Then(ctx => ctx.Saga.CancelReason = CancelReasons.PaymentDeclined)
                // Compensate: stock was reserved and must be released.
                .Send(InventoryQueue, ctx => new ReleaseStock(ctx.Saga.OrderId))
                .Schedule(ReleaseTimeout, ctx => new StockReleaseExpired(ctx.Saga.OrderId))
                .TransitionTo(Compensating),

            When(PaymentTimeout.Received)
                // Same compensation as a decline — an answer that never came
                // and an answer of "no" leave the same stock reserved. Not the
                // same reason: the stock branch above already distinguishes
                // out_of_stock from stock_timeout, and collapsing the payment
                // pair would make the PSP going quiet indistinguishable from
                // customers being declined on the one dashboard that asks.
                .Then(ctx => ctx.Saga.CancelReason = CancelReasons.PaymentTimeout)
                .Send(InventoryQueue, ctx => new ReleaseStock(ctx.Saga.OrderId))
                .Schedule(ReleaseTimeout, ctx => new StockReleaseExpired(ctx.Saga.OrderId))
                .TransitionTo(Compensating),

            // The state this defect was worth its severity in: stock is held
            // and AuthorisePayment HAS ALREADY BEEN SENT — entering this state
            // is what sends it. Cancelling here compensates on the decline
            // branch's own terms: release the reservation, wait for it, cancel.
            //
            // **This transition does not stop a charge, and an earlier comment
            // here said it did.** The authorisation request is already with
            // Payments; whether it completes is Payments' race, and §3.2 has
            // that service consuming OrderCancelled without specifying that it
            // voids an authorisation in flight. What this saga guarantees is
            // narrower and worth stating exactly: it sends no FURTHER
            // AuthorisePayment, and if one is authorised anyway the
            // Compensating state escalates it for a human.
            When(OrderCancelled)
                .Unschedule(PaymentTimeout)
                .Then(ctx => ctx.Saga.CancelReason = CancelReasons.CustomerRequest)
                .Send(InventoryQueue, ctx => new ReleaseStock(ctx.Saga.OrderId))
                .Schedule(ReleaseTimeout, ctx => new StockReleaseExpired(ctx.Saga.OrderId))
                .TransitionTo(Compensating));

        During(
            Confirmed,
            When(ShipmentDispatched)
                .Unschedule(DespatchTimeout)
                .Send(
                    OrderingQueue,
                    ctx => new MarkOrderShipped(ctx.Saga.OrderId, ctx.Message.TrackingNumber))
                .Finalize(),

            When(DespatchTimeout.Received)
                // Escalation, not compensation. The saga finalises because it
                // has nothing further to coordinate; a human now owns the order.
                .Send(
                    OrderingQueue,
                    ctx => new FlagOrderForReview(ctx.Saga.OrderId, ReviewReasons.NotDespatched))
                .Finalize(),

            // The card has been authorised, so this is the one cancellation
            // the machine cannot compensate: undoing it is a refund, and §3.2
            // closes Payments' Accepts column at AuthorisePayment — there is no
            // refund contract to send. Inventing one here would be a §3.2
            // decision taken in a state machine.
            //
            // So it escalates and finalises, on the despatch timeout's own
            // argument one row up: a wait with no automatic compensation still
            // ends, and a human owns what follows. Unscheduling matters more
            // than usual — left armed, the despatch timeout raises a
            // not_despatched review three days later for an order that was
            // cancelled, which is a false escalation on top of a real one.
            //
            // No ReleaseStock either, and that is deliberate: Confirmed means
            // Shipping has been asked for a despatch, and a reservation being
            // picked is not one Inventory can safely be told to drop. The
            // review row is where both loose ends are worked.
            When(OrderCancelled)
                .Unschedule(DespatchTimeout)
                // **A different code from Compensating's, and the row is the
                // only thing an operator gets.** ordering.OrderReviews persists
                // (OrderId, Reason, RaisedAt); the saga has usually finalised
                // before the one-hour alert, so its state is gone. One code for
                // both origins left the runbook selecting a procedure on a
                // state nothing recorded — and the two procedures differ at the
                // first step: from here the order reached Confirmed, so
                // Shipping may still despatch it and stopping that comes first.
                .Send(
                    OrderingQueue,
                    ctx => new FlagOrderForReview(
                        ctx.Saga.OrderId,
                        ReviewReasons.CancelledAfterConfirmation))
                .Finalize());

        During(
            Compensating,
            When(StockReleased)
                .Unschedule(ReleaseTimeout)
                // The reason recorded on entry, not a literal: this transition
                // is reached from a decline and from a timeout alike.
                .Send(
                    OrderingQueue,
                    ctx => new CancelOrder(ctx.Saga.OrderId, ctx.Saga.CancelReason))
                .Finalize(),

            When(ReleaseTimeout.Received)
                // Cancel the order regardless — the customer must not be left
                // waiting on Inventory. The stranded reservation is escalated
                // separately, because it is Inventory's to resolve.
                .Send(
                    OrderingQueue,
                    ctx => new CancelOrder(ctx.Saga.OrderId, ctx.Saga.CancelReason))
                .Send(
                    OrderingQueue,
                    ctx => new FlagOrderForReview(ctx.Saga.OrderId, ReviewReasons.StockNotReleased))
                .Finalize(),

            // The money arriving after the cancellation was already the
            // outcome, and it is the one event this state must NOT be quiet
            // about. Reaching Compensating from AwaitingPayment means
            // AuthorisePayment had already been sent, so an authorisation can
            // still land here — and §3.2 gives Payments no refund command, so
            // a human owns it exactly as they do one state over.
            //
            // This is Confirmed's cancelled_after_payment case arriving by the
            // other door. Left unwritten it would fall to OnUnhandledEvent and
            // be IGNORED — the catch-all this branch added for redelivery
            // would silently swallow the case this branch's other half exists
            // to escalate. The two fixes interacted, and only writing the
            // transition separates them.
            //
            // No Finalize: the saga is still waiting on StockReleased, and the
            // exits below own the cancellation. This adds the review row and
            // nothing else.
            //
            // **It covers one interleaving of two, and the other is #124.** If
            // StockReleased lands FIRST the exit above finalises, the instance
            // is deleted, and an authorisation still in flight then correlates
            // to nothing — discarded in silence, by the same default the
            // OnMissingInstance comment above measures. Inventory answering
            // promptly while a PSP is slow is the expected case rather than the
            // degenerate one, so this is a real hole and not a corner. Closing
            // it means Compensating waiting on both outstanding results, which
            // is a change to the shape of the machine.
            When(PaymentAuthorised)
                .Send(
                    OrderingQueue,
                    ctx => new FlagOrderForReview(
                        ctx.Saga.OrderId,
                        ReviewReasons.CancelledAfterPayment)),

            // Written, not left to OnUnhandledEvent, and the difference is
            // whether a reader can tell a decision from an omission. Reaching
            // Compensating means a cancellation is already the outcome — from
            // a decline, a timeout, or the customer's own request one state
            // back — so a customer cancellation arriving now adds nothing to
            // do. The exits from this state cancel the order regardless, and
            // Order.Cancel is idempotent, so the second CancelOrder they send
            // is a no-op rather than a second cancellation.
            Ignore(OrderCancelled),

            // The two Inventory answers to a reservation this saga no longer
            // wants, both reachable by cancelling in AwaitingStock and both
            // designed races rather than misroutes: ReleaseStock is already in
            // flight by the time either lands, and Compensating's own exits
            // own the cancellation.
            //
            // Written for the same reason as the line above, and they were the
            // last two events left on the catch-all. §9.6's trap justifies
            // OnUnhandledEvent by claiming every declared event is handled in
            // every state it can reach one in — a claim that was false for
            // PaymentAuthorised when the callback landed and false for these
            // two after that was fixed. Enumerating beat patching: the
            // declared events are eight, Compensating can be reached from
            // AwaitingStock and AwaitingPayment, and these are the ones that
            // follow.
            Ignore(StockReserved),
            Ignore(StockReservationFailed),

            // **The enumeration above missed one, and this branch is what made
            // it reachable.** Reaching Compensating from AwaitingPayment used
            // to mean the payment had already answered — declined, or timed
            // out. The OrderCancelled transition this branch added arrives
            // there with the authorisation still OUTSTANDING, so its verdict
            // can be either: PaymentAuthorised is handled above, and a decline
            // is this line. Left unwritten it fell to OnUnhandledEvent, which
            // is the catch-all the comment beside it claims Compensating does
            // not lean on.
            //
            // Ignored rather than escalated, unlike its sibling: a decline
            // means no money moved, which is the outcome compensation was
            // heading for anyway. Nothing for a human to do.
            Ignore(PaymentDeclined));

        SetCompletedWhenFinalized();
    }
}
