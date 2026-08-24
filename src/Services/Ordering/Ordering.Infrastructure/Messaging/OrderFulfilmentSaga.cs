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
    //
    // **AwaitingConfirmation is #126, and it exists because the state below it
    // was named after a thing that had not happened.** Confirmed used to be
    // entered in the activity that SENDS ConfirmOrder, so it meant "a command
    // is in flight" while every comment and every review code read it as "the
    // aggregate confirmed and Shipping knows". The two diverge for exactly as
    // long as one local command takes, and a cancellation arriving inside that
    // window took the Confirmed branch: no ReleaseStock, on the argument that
    // a reservation being picked must not be dropped — for a despatch nobody
    // had requested — and a cancelled_after_confirmation row for an order that
    // was never confirmed.
    //
    // The fix is to wait for the acknowledgement rather than to assume it, and
    // the acknowledgement is not a new contract: Order.ConfirmPayment raises
    // OrderConfirmedDomainEvent and §9.3's mapper stages OrderConfirmed on the
    // outbox already. So the honest state is one this service was publishing
    // the evidence for the whole time and nothing was listening to.
    public State AwaitingStock { get; private set; } = null!;
    public State AwaitingPayment { get; private set; } = null!;
    public State AwaitingConfirmation { get; private set; } = null!;
    public State Confirmed { get; private set; } = null!;
    public State Compensating { get; private set; } = null!;

    public Event<OrderPlaced> OrderPlaced { get; private set; } = null!;
    public Event<StockReserved> StockReserved { get; private set; } = null!;
    public Event<StockReservationFailed> StockReservationFailed { get; private set; } = null!;
    public Event<PaymentAuthorised> PaymentAuthorised { get; private set; } = null!;
    public Event<PaymentDeclined> PaymentDeclined { get; private set; } = null!;
    public Event<StockReleased> StockReleased { get; private set; } = null!;
    public Event<ShipmentDispatched> ShipmentDispatched { get; private set; } = null!;

    // Ordering's own event, and not the only one here that is — OrderPlaced
    // above and OrderConfirmed below are the others. §3.2 gives Ordering all
    // three for the same reason: a service is a subscriber to itself whenever
    // a fact it publishes is also a fact its workflow has to react to.
    //
    // **This line carried a count and it went stale exactly as counts here
    // do.** It said "the SECOND of the two", having already been corrected
    // once from "the only one"; #126 made it three without touching this
    // comment's subject. The property they share is what the sentence is for,
    // so it now names them instead of numbering them.
    //
    // "Cancel this order" has two origins and only one of them was reaching
    // the machine. The saga's own CancelOrder is always paired with Finalize,
    // so the workflow ends with it; §11.4's customer endpoint cancels the
    // AGGREGATE and ended nothing, leaving the saga to reserve stock and
    // authorise a card for an order the customer had already cancelled.
    //
    // **Declaring it binds OrderCancelled to an EXISTING queue, and #131
    // is what that costs during a rollout.** Both releases consume
    // ordering-fulfilment-saga at once, so the broker can hand a newly
    // bound OrderCancelled to an old replica whose machine does not
    // declare it — and MassTransit moves a message an endpoint has no
    // consumer for to <queue>_skipped, which §13.6 does not watch. The
    // cancellation is then lost quietly, which is the defect this line
    // exists to fix, reappearing for the length of the deploy.
    public Event<OrderCancelled> OrderCancelled { get; private set; } = null!;

    // The acknowledgement AwaitingConfirmation waits for (#126), and the reason
    // that state costs no contract change: Order.ConfirmPayment already raises
    // OrderConfirmedDomainEvent, OrderingIntegrationEventMapper already maps it
    // to this type, and TransactionBehavior already stages it on the outbox in
    // the same transaction that sets the status. Shipping binds it (§3.2); the
    // saga now binds it too, which is the same "subscriber to itself" edge
    // OrderPlaced and OrderCancelled already draw.
    //
    // **Binding it costs what every new binding on a live queue costs (#131).**
    // Both releases consume ordering-fulfilment-saga during a rollout, so the
    // broker can hand a newly bound OrderConfirmed to an old replica whose
    // machine does not declare it, and MassTransit parks it in <queue>_skipped
    // — which §13.6 does not watch. Here the loss is bounded rather than
    // silent, and that is worth stating precisely: an old replica is still
    // running the old machine, whose Confirmed state it entered on the send, so
    // it is not waiting for this event and loses nothing by missing it. A NEW
    // replica's instance parked in AwaitingConfirmation is the one that needs
    // it, and a new replica declares it. What the rollout can still strand is
    // an instance a new replica advanced and an old replica is handed the
    // acknowledgement for — bounded by ConfirmationTimeout below, which
    // escalates rather than hanging.
    //
    // **Two harder directions exist and neither is closed by that sentence.**
    // The first is an instance an OLD replica advanced, handed to a NEW one:
    // the old machine entered Confirmed on the SEND, so its OrderConfirmed
    // arrives at a state the new machine reaches with the acknowledgement
    // already spent. Ignore(OrderConfirmed) in Confirmed is what absorbs that,
    // and the argument is written at the line itself.
    //
    // The second cannot be closed from here at all. An old replica handed ANY
    // bound event for an instance whose CurrentState reads AwaitingConfirmation
    // throws UnknownStateException before any branch or token check runs —
    // MassTransit resolves the state name against the machine it has, and that
    // one has no such state. ADR-021 guarantees uncancellable expiries in the
    // broker for every order, so there is always something to arrive. The
    // window is the sub-second residency of the new state, and the only real
    // mitigations are draining or a non-overlapping cutover, which is #131's
    // subject rather than this file's.
    public Event<OrderConfirmed> OrderConfirmed { get; private set; } = null!;

    // One schedule per wait. "Every wait has a timeout" is a rule the machine
    // must be able to express, not a habit to remember at each transition.
    public Schedule<OrderFulfilmentState, StockReservationExpired> StockTimeout { get; private set; } = null!;
    public Schedule<OrderFulfilmentState, PaymentAuthorisationExpired> PaymentTimeout { get; private set; } = null!;
    public Schedule<OrderFulfilmentState, ConfirmationExpired> ConfirmationTimeout { get; private set; } = null!;
    public Schedule<OrderFulfilmentState, DespatchExpired> DespatchTimeout { get; private set; } = null!;
    public Schedule<OrderFulfilmentState, StockReleaseExpired> ReleaseTimeout { get; private set; } = null!;

    public OrderFulfilmentSaga()
    {
        InstanceState(x => x.CurrentState);

        // **Nothing catches an unhandled event, and that is the decision
        // rather than an omission.** MassTransit's default raises
        // UnhandledEventException, so an event reaching an instance in a
        // state with no transition for it spends §9.8's retries and lands in
        // the error queue §13.6 pages on. This machine keeps that default.
        //
        // **An OnUnhandledEvent(x => x.Ignore()) catch-all stood here and was
        // removed.** The argument for it was that the only event reaching it
        // is a duplicate: §9.5's inbox suppresses the ordinary completed
        // redelivery, because OutboxMessage.Stage persists the integration
        // event's own message id and OutboxDispatcher restores it on every
        // publish. What the inbox cannot suppress is a redelivery whose row
        // was never written — InboxFilter adds its row AFTER the inner pipe
        // returns, so a crash between the instance committing and that write
        // leaves the event unrecorded and the next delivery finds the
        // instance moved on.
        //
        // **That window contains the in-memory outbox's flush, and that is
        // what settled it (#128).** UseInMemoryOutbox sits inside the inbox
        // filter and releases its buffered sends after the inner pipeline
        // returns — after the repository has committed. So three cases reach
        // here and only one of them wants to be quiet:
        //
        //   * a crash AFTER the flush — the commands went out, the state
        //     advanced, and the redelivery really is a duplicate;
        //   * a crash BEFORE it — the state advanced and its commands were
        //     never sent, including the scheduled timeout that would have
        //     rescued the order. The redelivery is the last thing that could
        //     notice, and ignoring it makes the loss permanent and silent;
        //   * a misroute — a configuration fault, which wants to be loud.
        //
        // Nothing here can tell them apart, so the catch-all had to answer
        // all three the same way and answered two of them wrongly. A log
        // line was tried in its place and is not a signal: §13.6 pages on the
        // error queue, which is exactly what ignoring keeps the event out of.
        //
        // **The cost is real and is the smaller half.** A post-flush
        // duplicate now spends six retries and files one message a human
        // reads. That case is as rare as the loss it used to hide — the
        // NotAcceptedStateMachineException that motivated the catch-all was
        // produced by a test republishing an event, not observed in
        // production, where the inbox suppresses the ordinary redelivery.
        //
        // **What replaces it is enumeration, which this machine already
        // does.** Every event legitimately arriving in a state it has no
        // work for is written out with an explicit Ignore — Compensating
        // carries five — and a structural test asserts the machine's
        // declared next-events partition into "reachable here" and "not".
        // An unenumerated arrival is now a fault by design, which is what
        // makes that enumeration load-bearing rather than documentation.
        //
        // #128 removes the question by persisting the sends with the
        // instance (UseBusOutbox); the pre-flush case stops existing and a
        // catch-all becomes arguable again on evidence rather than on this
        // comment.

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
        Event(() => OrderConfirmed, x => x.CorrelateById(m => m.Message.OrderId));

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
        // ways to get there.
        //
        // **Telling the two apart is NOT possible from the message, and an
        // earlier revision of this comment said it was.** It read Reason as
        // the discriminator, on the premise that only a customer's
        // cancellation carries customer_request. §11.4's endpoint parses the
        // whole CancellationReasons map — all five CancelReasons codes — so a
        // caller may send payment_declined and a saga-caused cancellation may
        // carry customer_request when that is what the saga sent. The
        // contract carries no origin field either. So #123 needs an added
        // discriminator (a §9.2 version bump, with consumers on the other
        // side) or a narrower endpoint vocabulary, and both are §9.6
        // decisions rather than a line to change here.
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

        // #126's wait, and the only one whose far end is this same service —
        // Ordering answering its own ConfirmOrder. So the number is not a
        // guess about a peer; it is a bound on two mechanisms in this
        // repository, and the smaller of them is not the one that decides it.
        //
        // §9.8's retry on ordering-commands is five RETRIES at
        // Exponential(1s, 1min, delta 2s) — six deliveries counting the first,
        // which is what §9.6 says two thousand lines away and what this said
        // "five attempts" for. **The waiting is about seventy seconds in
        // total, not five minutes**: five retries are five intervals whatever
        // the deliveries are numbered, and this comment read "five attempts
        // backing off to a minute apiece", which prices every interval at the
        // cap the ladder never reaches.
        //
        // **The term that actually decides this is §9.4's dispatcher**, and
        // the earlier revision credited its 500ms POLL, which is the one
        // quantity here that cannot matter. A failed publish backs the row off
        // by POWER(2, MIN(Attempts, 8)) * 5 seconds, so the cumulative wait
        // runs 5s, 15s, 35s, 75s, 155s, 315s, 635s. **A publish that only
        // succeeds on its eighth attempt lands after this timeout has already
        // fired**, filing a not_confirmed review for an order that then
        // confirms.
        //
        // Ten minutes is chosen knowing that rather than in spite of it: seven
        // consecutive publish failures is an outbox that is genuinely stuck,
        // which §13.6's abandoned-row alert exists to catch and which nobody
        // wants this saga waiting quietly through. Raising the delay past
        // attempt ten would trade a rare false escalation for a common silent
        // one. It matches ReleaseTimeout below, and for the same reason: both
        // are waits on a message this service has already sent rather than on
        // a third party deciding something.
        //
        // Like DespatchTimeout it escalates rather than compensating: the card
        // is authorised by the time this wait begins and §3.2 gives Ordering no
        // refund command, so there is no automatic action left.
        Schedule(
            () => ConfirmationTimeout,
            x => x.ConfirmationTimeoutTokenId,
            s =>
            {
                s.Delay = TimeSpan.FromMinutes(10);
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
            //
            // **One OrderCancelled starts two races to this endpoint, and
            // only one of them is this transition.** §3.2 has Inventory
            // consuming OrderCancelled DIRECTLY and publishing StockReleased,
            // independently of the ReleaseStock sent below — so a
            // StockReleased derived from this very event can reach the saga
            // before the saga has consumed its own copy. FOUR states can be
            // holding an instance when it does: AwaitingStock, AwaitingPayment
            // and — since #126 — AwaitingConfirmation, each of which sends a
            // release of its own, plus Confirmed, which deliberately does not.
            // Each writes the arrival out with an Ignore and argues it at the
            // site. The four are enumerated rather than counted because this
            // note said "the three states" until Confirmed was read, and #129
            // names three as well.
            //
            // **Absorbing it costs nothing only because ADR-024 makes the
            // release below answerable**, and that is the ordering of the two
            // halves rather than a footnote: Inventory answers the saga's own
            // ReleaseStock whether or not it already released on the event, so
            // Compensating's exit does not depend on the copy that was
            // discarded. Without the ADR this line would trade a transient
            // race for a certain wrong answer — a stock_not_released review
            // for a reservation that came back — which is what #129 said and
            // why it was filed rather than patched.
            //
            // **Not introduced by the branch that added this transition, and
            // the counterfactual has to name its baseline.** This read "on
            // main this machine has no When(OrderCancelled) at all", which
            // stopped being true the moment #117 merged — main now declares
            // four of them, beside this comment. BEFORE #117 the machine had
            // none, so the derived StockReleased landed in a branchless state
            // every time with no retry able to rescue it, because nothing
            // would move the state. #117 narrowed that certainty to an
            // interleaving; the Ignore below closes what was left.
            When(OrderCancelled)
                .Unschedule(StockTimeout)
                // **The event's reason, not a literal — and this line read
                // CancelReasons.CustomerRequest until a review asked what
                // §11.4 actually accepts.** It parses the whole five-code
                // CancellationReasons map, so a caller may cancel with
                // payment_declined; hard-coding here overwrote whatever the
                // aggregate reported and Compensating's exit then sent
                // CancelOrder with a reason no one had chosen. The literals on
                // the decline and timeout branches below are correct because
                // those transitions ARE the decline and the timeout — this one
                // is whatever arrived. CancelOrderMapper parses through the
                // same map and refuses an unknown code, so passing the string
                // through is exactly as safe as the literal was.
                .Then(ctx => ctx.Saga.CancelReason = ctx.Message.Reason)
                .Send(InventoryQueue, ctx => new ReleaseStock(ctx.Saga.OrderId))
                .Schedule(ReleaseTimeout, ctx => new StockReleaseExpired(ctx.Saga.OrderId))
                .TransitionTo(Compensating),

            // **Inventory's release, derived from the cancellation this state
            // has not consumed yet — the race the branch above names, written
            // out (#129).** Left unwritten it faults: the state declares no
            // branch, so MassTransit raises UnhandledEventException and §9.8's
            // five retries — six deliveries with the first — get about seventy
            // seconds to find the instance in Compensating. Usually they do. A backlog on this queue outlasts
            // that ladder, and then the release lands in the error queue §13.6
            // pages on WHILE the instance waits out ReleaseTimeout and files a
            // stock_not_released review for stock that came back an hour
            // earlier — the operator sent to chase a reservation that does not
            // exist.
            //
            // **Absorbing it is sound because of ADR-024, and not on its own.**
            // Ignoring discards the release, so Compensating's exit has to come
            // from somewhere else, and it does: the branch above sends its OWN
            // ReleaseStock, and ADR-024 has Inventory answer that command
            // whatever it already did with the event. Under the other reading —
            // a release of nothing has nothing to report — this line would
            // trade a transient race for a CERTAIN wrong answer, which is
            // exactly what #129 said and what the ADR settles. The line and the
            // ADR are one change; neither is correct without the other.
            //
            // **Nothing else can produce one here**, which is what makes
            // absorbing it safe rather than merely quiet. §3.2 gives Inventory
            // two triggers for StockReleased — a ReleaseStock command and
            // OrderCancelled — and this saga has sent no release in this state.
            // So an arrival is always a cancellation the saga is about to
            // consume on its own copy.
            Ignore(StockReleased));

        During(
            AwaitingPayment,
            // **This activity SENDS ConfirmOrder; it does not confirm the
            // order, and the state it moves to now says so (#126).** It used
            // to land in Confirmed and arm DespatchTimeout here — naming a
            // state after a command's intent and arming a three-day wait on
            // Shipping before Shipping had been told anything. Both now wait
            // for the aggregate's own OrderConfirmed, which is the first
            // moment either claim is true.
            //
            // Not Finalize either way: the order is not finished at payment.
            When(PaymentAuthorised)
                .Unschedule(PaymentTimeout)
                .Send(
                    OrderingQueue,
                    ctx => new ConfirmOrder(ctx.Saga.OrderId, ctx.Message.Reference))
                .Schedule(ConfirmationTimeout, ctx => new ConfirmationExpired(ctx.Saga.OrderId))
                .TransitionTo(AwaitingConfirmation),

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
            //
            // The two-races note on the AwaitingStock branch above applies
            // here unchanged, and so does its answer: this state writes the
            // early release out with an Ignore below, on ADR-024's terms.
            When(OrderCancelled)
                .Unschedule(PaymentTimeout)
                // The event's reason, for the argument on the AwaitingStock
                // branch above — the same defect was in both, because the two
                // transitions were written together.
                .Then(ctx => ctx.Saga.CancelReason = ctx.Message.Reason)
                .Send(InventoryQueue, ctx => new ReleaseStock(ctx.Saga.OrderId))
                .Schedule(ReleaseTimeout, ctx => new StockReleaseExpired(ctx.Saga.OrderId))
                .TransitionTo(Compensating),

            // The second door onto #129, and the argument is AwaitingStock's
            // unchanged: this state's OrderCancelled branch sends its own
            // ReleaseStock, ADR-024 has that command answered, and the early
            // arrival is therefore absorbable without losing the exit.
            //
            // **What differs is what is outstanding beside the stock.**
            // Reaching here means AuthorisePayment has been sent, so a
            // verdict is still in flight — but that is Compensating's
            // problem on either arrival order, and it is #124's rather than
            // this line's. Absorbing the release changes nothing about it:
            // the instance still reaches Compensating on the cancellation.
            Ignore(StockReleased));

        // #126's state. ConfirmOrder is in flight and nothing downstream knows
        // anything yet: the aggregate is still AwaitingPayment, no
        // OrderConfirmed has been published, and Shipping has not been told.
        // Every branch below turns on that being true.
        During(
            AwaitingConfirmation,
            // The acknowledgement. This is the moment the order IS confirmed —
            // the aggregate committed the status and staged this event in the
            // same transaction (§6.3) — so it is also the first moment a
            // despatch can be expected, which is why DespatchTimeout is armed
            // here rather than one state back.
            When(OrderConfirmed)
                .Unschedule(ConfirmationTimeout)
                .Schedule(DespatchTimeout, ctx => new DespatchExpired(ctx.Saga.OrderId))
                .TransitionTo(Confirmed),

            // The cancellation Confirmed's branch used to answer wrongly.
            // Here the release is unambiguously right: no OrderConfirmed has
            // been seen, so Shipping was never told, so nothing is being
            // picked and the reservation is the saga's to give back. This is
            // AwaitingPayment's branch unchanged, and deliberately so — the
            // situation is the same one, a state later.
            //
            // **It escalates nothing, and that is the difference from
            // Confirmed's.** The money is authorised, but §3.2 has Payments
            // void on OrderCancelled itself; what made the confirmed case a
            // human's problem was a despatch that might already be moving, and
            // there is none here. Compensating's exits send CancelOrder, which
            // Order.Cancel absorbs when the customer's own cancellation got
            // there first.
            //
            // **The residual is that this branch cannot see the race it is
            // inside**, and the state below is where it is caught: the
            // aggregate may have confirmed a moment before the customer
            // cancelled, in which case OrderConfirmed is still in flight and
            // arrives in Compensating. That arrival is the only evidence, and
            // it raises cancelled_after_confirmation there rather than being
            // absorbed.
            When(OrderCancelled)
                .Unschedule(ConfirmationTimeout)
                .Then(ctx => ctx.Saga.CancelReason = ctx.Message.Reason)
                .Send(InventoryQueue, ctx => new ReleaseStock(ctx.Saga.OrderId))
                .Schedule(ReleaseTimeout, ctx => new StockReleaseExpired(ctx.Saga.OrderId))
                .TransitionTo(Compensating),

            // No acknowledgement and no cancellation, so the machine is out of
            // moves: the card is authorised, the stock is held, and §3.2 gives
            // Ordering no refund command to compensate with. It escalates on
            // DespatchTimeout's argument — a wait with no compensating action
            // still ends, and a human owns what follows.
            //
            // **Reaching this is a fault somewhere else, not an ordinary
            // outcome.** ConfirmOrder is a local command with a bounded retry
            // budget, and the aggregate refusing it is not this branch's case:
            // a refusal is a Rule failure CommandConsumer acks and counts, and
            // the only thing that refuses it is a cancellation, which arrives
            // here on its own event. What is left is the command never being
            // consumed at all — an outbox that stopped, a queue that is not
            // being drained, a replica that took the acknowledgement during a
            // rollout (#131). Each of those wants a person.
            When(ConfirmationTimeout.Received)
                .Send(
                    OrderingQueue,
                    ctx => new FlagOrderForReview(ctx.Saga.OrderId, ReviewReasons.NotConfirmed))
                .Finalize(),

            // **Shipping can beat this saga to its own acknowledgement, and
            // splitting the state is what made that reachable.** §3.2 gives
            // Shipping OrderConfirmed too, so the aggregate's one publish
            // fans out to two independent consumers and §9.4 orders nothing
            // between them. Under the old machine the saga was already in
            // Confirmed before that publish existed, so a despatch could not
            // arrive early; now it can, whenever this saga's own copy is
            // behind a retry or a backlog.
            //
            // Handled rather than ignored, because ignoring loses the
            // MarkOrderShipped this branch exists to send. It is safe on the
            // aggregate's terms too: Shipping only learns of the order FROM
            // OrderConfirmed, so a ShipmentDispatched arriving at all proves
            // the aggregate committed the confirmation — which is exactly the
            // precondition MarkOrderShipped checks.
            //
            // No Unschedule for DespatchTimeout: it is armed on entry to
            // Confirmed, and this branch is the case where that never
            // happened.
            When(ShipmentDispatched)
                .Unschedule(ConfirmationTimeout)
                .Send(
                    OrderingQueue,
                    ctx => new MarkOrderShipped(ctx.Saga.OrderId, ctx.Message.TrackingNumber))
                .Finalize(),

            // #129's third door. AwaitingStock's argument again — this state's
            // OrderCancelled branch sends a ReleaseStock that ADR-024 has
            // Inventory answer, so the early copy can go.
            //
            // This state's enumeration went stale the moment #126 split it
            // out of Confirmed, which is why the AwaitingStock comment writes
            // all four states out rather than counting them — and why this
            // sentence said "three" until that comment was read again.
            Ignore(StockReleased));

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

            // The card has been authorised, and undoing that is a refund §3.2
            // gives Ordering no command for: its Accepts column closes at
            // AuthorisePayment. Inventing one here would be a §3.2 decision
            // taken in a state machine.
            //
            // **"So the machine cannot compensate" is what this said, and it
            // does not follow.** §3.2 gives Payments a Refund aggregate, has
            // it publish PaymentRefunded, and lists OrderCancelled in its
            // Consumes column — the contract says an authorisation already
            // taken is voided. Payments refunds off the EVENT; Ordering just
            // has no way to ask.
            //
            // **A second revision then said the void is already on its way
            // here, and that is a guess about another service's delivery
            // order.** §9.4 orders nothing between two independent consumers,
            // so this saga seeing OrderCancelled says nothing about when
            // Payments consumed it. Whether a refund has happened is not
            // knowable from this state, which is why the runbook CHECKS on
            // both codes rather than predicting either.
            //
            // What this transition does own is SHIPPING: reaching Confirmed
            // means a despatch may still happen, and Compensating's sibling
            // cannot despatch at all. That is the difference between the two
            // codes; the money is what they have in common.
            //
            // So it escalates and finalises, on the despatch timeout's own
            // argument one row up: a wait with no automatic compensation still
            // ends, and a human owns what follows.
            //
            // **Finalize is what prevents the false not_despatched review,
            // NOT the Unschedule beside it, and this comment credited the
            // wrong one.** ADR-021 measured it against the tagged source:
            // the delayed-message scheduler's CancelScheduledSend returns
            // Task.CompletedTask on both overloads, so every Unschedule in
            // this machine is a no-op and the three-day DespatchExpired
            // stays queued whatever happens here. What makes its later
            // delivery harmless is that SetCompletedWhenFinalized has
            // deleted the instance, so it correlates to nothing and is
            // discarded. The Unschedule stays because ADR-021 names Quartz
            // as its own supersession and the calls become live that day.
            //
            // No ReleaseStock either, and that is deliberate: reaching
            // Confirmed means a despatch is expected, and a reservation being
            // picked is not one Inventory can safely be told to drop. The
            // review row is where both loose ends are worked.
            //
            // **That argument had a hole, it was #126, and this state is now
            // the narrower thing that makes it sound.** The state used to be
            // entered when ConfirmOrder was SENT rather than when it
            // committed, so "a despatch may still happen" was an assumption
            // about a handoff that had not been made: a cancellation beating
            // the command to the aggregate left the order never confirmed and
            // Shipping never told, and this branch then withheld the release
            // on the strength of a picking that was not happening. It is now
            // entered on the aggregate's own OrderConfirmed, so every premise
            // above is established rather than intended.
            //
            // **The same race was also said to page the error queue, and that
            // was wrong in this codebase's favour.** ConfirmOrder against a
            // cancelled order does not escape ConfirmOrderHandler:
            // Order.ConfirmPayment throws, the handler catches and returns
            // OrderErrors.NotAwaitingPayment, and that is Error.Rule — which
            // CommandConsumer acks, counts as a domain rejection and logs.
            // Only ErrorType.Unavailable is rethrown for §9.8's retry policy.
            // SagaCommandHandlerTests.Confirming_an_order_that_has_moved_on_is_a_rejection
            // pins it. The claim is removed rather than corrected in place
            // because nothing about that path is a defect.
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
                .Finalize(),

            // **A second OrderConfirmed lands here on the ordinary path and on
            // the rollout, and this line is what stops both being paged.**
            // Two arrivals reach it. One is §9.5's unrecorded redelivery — the
            // inbox writes its row after the inner pipe returns, so a crash in
            // that window leaves the next delivery to find an instance that has
            // moved on. The other is #131 at its sharpest: the OLD machine
            // entered Confirmed when it SENT ConfirmOrder, so every order it
            // confirmed publishes an OrderConfirmed moments later — and the
            // binding this release declares is durable and queue-scoped, so the
            // first new replica to boot starts copying those into this queue
            // for instances an old replica put in Confirmed. §15.5's canary
            // runs both releases for the length of its ladder, so that is
            // half an hour of it rather than an instant.
            //
            // **Ignoring is a real trade and not a tidy-up.** Left unwritten
            // both fault, and the deploy case faults on ORDINARY traffic —
            // §13.6 pages on the error queue, so shipping this without the
            // line is shipping a pager for every order in flight at cutover.
            //
            // **What it costs is the #128 signal on THIS transition, and the
            // reason that is acceptable here is what the transition sends.**
            // The catch-all this machine removed was wrong because it answered
            // a pre-flush crash — which loses commands — the same way as a
            // duplicate. Entering Confirmed sends no command: it arms
            // DespatchTimeout and nothing else. So a crash between the commit
            // and the flush loses a three-day BACKSTOP, not an action, and the
            // order is left in a state §13.6's own saga-age alert exists to
            // find — unfinalised in Confirmed past four days. Losing a
            // backstop that is itself backstopped is a different trade from
            // losing an AuthorisePayment, which is why StockReserved still has
            // no Ignore one state back and this one does.
            Ignore(OrderConfirmed),

            // **#129's fourth door, and the one no issue enumerated —
            // including #129 itself, which names three states.** The race is
            // the same shape: §3.2 has Inventory releasing on OrderCancelled,
            // so a cancellation reaching a confirmed order produces a
            // StockReleased for a state that declared no branch for it.
            //
            // **What the retry does here is discard rather than rescue, and
            // that is the difference from the other three doors.** Elsewhere a
            // later attempt finds the instance moved to Compensating and the
            // transition runs, so the event is delivered late rather than
            // lost. This state's own When(OrderCancelled) FINALISES, so by
            // the second attempt there is no instance — and a non-initial event
            // correlating to none is consumed cleanly, measured above and
            // pinned by a test. So the unwritten door is SILENT rather than
            // loud: one fault, then a clean ack on the redelivery and a
            // discarded release. **One ack, not four** — a retry pipeline
            // stops at its first success, so the remaining retries are never
            // made rather than being made cleanly.
            //
            // **It is not free, and "faulting would page" was the wrong
            // reason.** Nothing reaches the error queue unless the
            // cancellation is still unconsumed through the first delivery and
            // all five retries — the same backlog condition the AwaitingStock
            // comment states, no sharper here. What the line actually buys is that the FIRST
            // delivery is clean, on every cancellation of a confirmed order,
            // instead of burning a retry on a transition that cannot exist.
            //
            // **The reason to absorb it is not the one the other three have.**
            // Those states send a ReleaseStock and wait for the answer; this
            // one deliberately sends none, because reaching Confirmed means a
            // despatch may be moving and a reservation being picked is not one
            // Inventory can safely be told to drop. So there is no exit to
            // lose — and no dependency on ADR-024 either, which is why this
            // site argues from §3.2's fan-out and the other three do not.
            //
            // **What withholding the command does NOT do is keep the
            // reservation.** Inventory releases off OrderCancelled regardless,
            // so this arrival is the stock coming back for an order a picker
            // may still be working. The saga has no way to raise that —
            // cancelled_after_confirmation on this state's When(OrderCancelled)
            // is the row an operator works it from, and #141 is the §3.2
            // question behind it.
            //
            // **Named rather than pointed at, because both sentences said
            // "the branch below" and it is above** — Ignore(OrderConfirmed)
            // sits between them. The same false pointer the commit before
            // this one closed one state over, which is the argument for
            // naming a transition instead of its direction.
            Ignore(StockReleased));

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
            // still land here. §3.2 gives Ordering no refund command, and
            // Payments' own void is keyed on OrderCancelled — which on the
            // decline and timeout doors has not been PUBLISHED yet when this
            // fires, because CancelOrder goes on this state's exit below.
            //
            // A revision of this comment said the authorisation is therefore
            // beyond the automatic path's reach. It is not: the cancellation
            // is still coming, and on the two doors where it already happened
            // §9.4 orders nothing between Payments and this saga anyway. The
            // honest statement is that nothing here knows, which is why the
            // row exists and why the runbook checks rather than predicts.
            //
            // This is Confirmed's case arriving by the other door — the same
            // money problem, which is why it escalates too. It raises a
            // DIFFERENT code: Confirmed sends cancelled_after_confirmation
            // because an order that reached it may still be despatched, and
            // this state sends payment_authorised_during_compensation because it cannot.
            //
            // Left unwritten it would FAULT, and a paged error queue is not
            // what this case is owed: the money problem has a review row to
            // land in, which is the whole point of the code above. Writing
            // the transition is what turns the fault into the row.
            //
            // An earlier revision of this branch also carried an
            // OnUnhandledEvent catch-all, and then this line was what kept
            // the case from being swallowed instead. The catch-all is gone;
            // the reason to write this transition is unchanged either way,
            // which is what makes it the right kind of line.
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
                        ReviewReasons.PaymentAuthorisedDuringCompensation)),

            // **The confirmation that arrives after compensation has begun,
            // and the one thing in this machine that can prove #126's race
            // actually happened.** Only AwaitingConfirmation reaches here with
            // an OrderConfirmed still outstanding: that state cancels on the
            // premise that the aggregate had not confirmed — true whenever the
            // customer got there first, and unknowable at the moment the
            // branch runs, because OrderConfirmed and OrderCancelled are two of
            // Ordering's own outbox rows and §9.4 orders nothing between them.
            //
            // If it arrives, the premise was false: the order WAS confirmed,
            // Shipping was told, a despatch may be moving — and a ReleaseStock
            // for it is already in flight. That is precisely
            // cancelled_after_confirmation's case, so it raises the same code
            // Confirmed's branch does. Both loose ends are now on one row
            // rather than neither being anywhere.
            //
            // **Not Ignore, and the difference is the whole point of the
            // enumeration.** Absorbing it would restore #126's silence one
            // state over: the release would go out for stock being picked and
            // nothing would say so. Not a fault either — the arrival is
            // legitimate and there is a row for it.
            //
            // No Finalize: like PaymentAuthorised above, this state is still
            // waiting on StockReleased and the exits own the cancellation.
            //
            // **What it does not do is recall the release**, and the honest
            // reason is that there is nothing to recall it with: §3.2 gives
            // Inventory no way to be told "keep the reservation after all".
            // The row is the mechanism, which is what Confirmed's branch has
            // always relied on for the money half.
            When(OrderConfirmed)
                .Send(
                    OrderingQueue,
                    ctx => new FlagOrderForReview(
                        ctx.Saga.OrderId,
                        ReviewReasons.CancelledAfterConfirmation)),

            // Written rather than left to fault, and the difference is
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
            // races by design rather than misroutes: Compensating's own exits
            // own the cancellation, so neither answer has work left here.
            //
            // **"ReleaseStock is already in flight" is what this comment used
            // to say, and in flight is not the same as effective.** §9.4
            // orders nothing, so Inventory may handle the release BEFORE the
            // reserve it was meant to undo. That was #125, and it is closed in
            // §3.2 rather than here: ADR-024 has Inventory REMEMBER a release
            // for an order whose ReserveStock has not arrived and refuse the
            // reserve that follows with StockReservationFailed, so no
            // reservation is created for an order this saga has cancelled.
            //
            // **It could not be closed here, and that is worth stating because
            // the saga-side fix was the obvious one.** Sending a second
            // ReleaseStock on StockReserved needs this state to still hold an
            // instance when the late reserve lands — and under ADR-024's other
            // half the no-op release has already published, so the exit above
            // has already finalised. The branch that would send the second
            // release is one nothing reaches. Only the receiver still has both
            // facts, which is why the guarantee is Inventory's.
            //
            // **Both events are therefore absorbed on a stated contract rather
            // than on a hope about ordering**, and the difference from the
            // previous revision is that the contract now exists. §3.2 said
            // nothing about a release for a reservation that was never held
            // (#130), and the two readings were opposite: publish, and the
            // stranding is silent; do not, and StockReservationFailed — which
            // PROVES no reservation is held — leaves this state through
            // ReleaseTimeout with a stock_not_released review naming stock
            // nobody ever reserved. ADR-024 takes the first reading precisely
            // because the second escalates the routine case.
            //
            // Written for the same reason as the line above, and they were the
            // last two arrivals with no branch. **The enumeration is now the
            // whole mechanism**, not a tidy-up beside a catch-all: with
            // MassTransit's default kept, anything not written out here
            // reaches the error queue. §9.6's trap used to justify a
            // catch-all by claiming every declared event is handled in every
            // state it can reach one in — a claim that was false for
            // PaymentAuthorised and then for these two. A structural test
            // partitions the declared next-events now, so the claim is
            // checked rather than asserted.
            Ignore(StockReserved),
            Ignore(StockReservationFailed),

            // **The enumeration above missed one, and this branch is what made
            // it reachable.** Reaching Compensating from AwaitingPayment used
            // to mean the payment had already answered — declined, or timed
            // out. The OrderCancelled transition this branch added arrives
            // there with the authorisation still OUTSTANDING, so its verdict
            // can be either: PaymentAuthorised is handled above, and a decline
            // is this line. Left unwritten it would reach the error queue,
            // for an outcome compensation was already heading towards.
            //
            // Ignored rather than escalated, unlike its sibling: a decline
            // means no money moved, which is the outcome compensation was
            // heading for anyway. Nothing for a human to do.
            Ignore(PaymentDeclined));

        SetCompletedWhenFinalized();
    }
}
