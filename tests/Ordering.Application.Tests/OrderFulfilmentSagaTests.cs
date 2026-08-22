using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Ordering.Application.Tests;

/// <summary>
/// §12.5's suite: the saga of §9.6 driven end to end over MassTransit's
/// in-memory harness, with no infrastructure at all. It lives in this project
/// rather than <c>Ordering.Api.Tests</c> for exactly that reason — the suites
/// that need Docker pay for a container set each (§12.4), and this one needs
/// none.
/// </summary>
/// <remarks>
/// The saga is <c>Ordering.Infrastructure</c>'s, which this project already
/// references for <c>AddOrderingInfrastructure</c>. §4.2's gate binds
/// <c>Ordering.Application</c> and not its tests.
/// </remarks>
public class OrderFulfilmentSagaTests
{
    /// <summary>
    /// §12.5's two bounds, stated rather than inherited, and stated once per
    /// harness rather than per test.
    /// </summary>
    /// <remarks>
    /// An <c>Any(…)</c> ends at the <em>earliest</em> of a match, the
    /// inactivity bound (MassTransit's default is 1.2 s, measured from the last
    /// bus activity), the test bound (measured from the call) and the caller's
    /// token. Inherit either and a saturated runner fails the suite wearing the
    /// assertion's own message — a saga that did not send, rather than a runner
    /// that did not schedule. The ceiling is kept clear of the bound meant to
    /// fire so that which one reported a failure is never a detail of how long
    /// a publish took.
    /// </remarks>
    private static readonly TimeSpan InactivityTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    private static readonly Guid Customer = Guid.Parse("2a1c9e64-77b1-4b0e-9a3e-6d9c1c2f5a11");

    /// <summary>
    /// The registration every test here shares.
    /// </summary>
    /// <remarks>
    /// <b>The scheduler lines are not §12.5's and the chapter was amended in
    /// the same change.</b> That sample registers the saga and the in-memory
    /// repository and stops, which is enough for a state machine with no
    /// schedules and not for this one: <c>Initially</c> arms
    /// <c>StockTimeout</c>, so the very first <c>OrderPlaced</c> reaches for a
    /// scheduler nothing put on the pipeline.
    /// <para>
    /// <b>Measured by deleting both lines: 11 of the 13 tests this file held
    /// when the measurement was taken fail, and every one of them fails as a
    /// TIMEOUT rather than as an error.</b> The count is left as measured
    /// rather than rescaled to the 20 tests here now — a ratio nobody re-ran
    /// is not evidence about a suite that has since grown.
    /// The two survivors are the structural pair at the bottom, which
    /// construct the state machine and never start a bus — correctly, and
    /// worth knowing, because they are the two that would keep a deleted
    /// registration looking half-covered. What the eleven do not do is say
    /// why: the saga's exception is faulted onto the error queue, the
    /// assertion waits out its inactivity bound and reports the command it
    /// wanted, and the run takes ten seconds per test to say "the saga did not
    /// send". That is the shape §12.5's own trap warns about, arriving from a
    /// missing registration instead of from a loaded runner — so the lines are
    /// stated here rather than inherited from a default.
    /// </para>
    /// <para>
    /// They are the same two lines production uses (ADR-021), which is the
    /// point: the in-memory transport implements the delay itself where
    /// RabbitMQ needs a plugin, so the transports differ and the
    /// <em>registration under test</em> does not.
    /// </para>
    /// </remarks>
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddMassTransitTestHarness(x =>
            {
                x.SetTestTimeouts(TestTimeout, InactivityTimeout);
                x.AddDelayedMessageScheduler();
                x
                    .AddSagaStateMachine<OrderFulfilmentSaga, OrderFulfilmentState>()
                    .InMemoryRepository();
                x.UsingInMemory((context, cfg) =>
                {
                    cfg.UseDelayedMessageScheduler();
                    cfg.ConfigureEndpoints(context);
                });
            })
            .BuildServiceProvider(true);

    private static async Task<(ServiceProvider Provider, ITestHarness Harness)> StartHarnessAsync()
    {
        ServiceProvider provider = BuildProvider();
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        return (provider, harness);
    }

    /// <summary>
    /// The helpers below exist to carry one argument, and it is the one
    /// xUnit1051 requires on every call that accepts a token — spelled out at
    /// each of the forty call sites it would bury the assertions.
    /// </summary>
    /// <remarks>
    /// They also keep §9.6's distinction visible at the call site: a command is
    /// <see cref="Sent"/> and an event is published, the harness records the
    /// two separately, and a test that asks the wrong list fails while looking
    /// like a saga defect.
    /// <para>
    /// There is no waiting <c>Published</c> sibling of <see cref="Sent"/>,
    /// because nothing here asserts a publish positively: every message this
    /// saga emits is a command, and the one published assertion in the suite is
    /// the negative that proves it. <see cref="NotYetPublished"/> is that one.
    /// </para>
    /// </remarks>
    private static Task Publish<T>(ITestHarness harness, T message)
        where T : class =>
        harness.Bus.Publish(message, TestContext.Current.CancellationToken);

    private static Task<bool> Sent<T>(ITestHarness harness, Func<T, bool> match)
        where T : class =>
        harness.Sent.Any<T>(m => match(m.Context.Message), TestContext.Current.CancellationToken);

    private static Task<bool> Consumed<T>(ITestHarness harness, Func<T, bool> match)
        where T : class =>
        harness.Consumed.Any<T>(m => match(m.Context.Message), TestContext.Current.CancellationToken);

    /// <summary>
    /// A negative assertion, read as of now: no wait, no deadline for a late
    /// saga to hide inside, and the harness's one shared inactivity token left
    /// unspent for whatever follows (§12.5).
    /// </summary>
    /// <remarks>
    /// <b>Every negative in this suite goes through here, including the ones
    /// that end their test.</b> §12.5 permits a trailing negative to simply
    /// wait, and waiting costs the full inactivity timeout for an answer that
    /// is already knowable — so the technique is applied uniformly and this
    /// file never spends that bound. <b>It did until a review counted</b>: the
    /// commands-are-sent test ended on a published negative that took the
    /// ordinary token, so the one assertion guaranteed never to match was
    /// billing ten seconds on every green run, underneath this sentence.
    /// "Every negative" has to include the published list, which is why
    /// <see cref="NotYetPublished"/> exists.
    /// <para>
    /// A <em>deadline</em> would be the wrong tool and fails open: a window is
    /// something a late-sending saga fits inside, and a later positive would
    /// then accept the very command the negative was there to forbid. The
    /// caller's job is to put a positive assertion before each of these, so
    /// that "not yet" has a point in time to be false at.
    /// </para>
    /// </remarks>
    private static Task<bool> NotYetSent<T>(ITestHarness harness, Func<T, bool> match)
        where T : class =>
        harness.Sent.Any<T>(m => match(m.Context.Message), Spent());

    /// <summary>
    /// <see cref="NotYetSent"/>'s sibling over the <em>published</em> list, and
    /// it exists because the claim above was false without it.
    /// </summary>
    /// <remarks>
    /// The commands-are-sent test ends on a published negative, and a published
    /// negative is the one this suite is guaranteed never to match — the whole
    /// point of that test is that a command is not published. So it was the one
    /// assertion here billing the full inactivity bound, on every green run,
    /// under a comment saying this file never spends it. One helper short of
    /// true.
    /// </remarks>
    private static Task<bool> NotYetPublished<T>(ITestHarness harness, Func<T, bool> match)
        where T : class =>
        harness.Published.Any<T>(m => match(m.Context.Message), Spent());

    /// <summary>
    /// An already-cancelled token, so an assertion reads the record as of now
    /// rather than waiting for something the test has just proved will not
    /// come.
    /// </summary>
    /// <remarks>
    /// <b>The constructor, not a cancelled <c>CancellationTokenSource</c>.</b>
    /// §12.5 prints the source form because it is written inside the test that
    /// uses it, where the <c>using</c> scope outlives the assertion. Behind a
    /// helper it does not: the source is disposed on return, and a token whose
    /// source has been disposed still reports <c>IsCancellationRequested</c>
    /// while throwing <c>ObjectDisposedException</c> from <c>Register</c> —
    /// which is what a consumer of the token does. Cancelled-on-construction
    /// owns nothing and cannot be disposed out from under a caller.
    /// </remarks>
    private static CancellationToken Spent() => new(canceled: true);

    /// <summary>
    /// The exception each recorded consume of <typeparamref name="T"/> ended
    /// with, or null where it ended cleanly.
    /// </summary>
    /// <remarks>
    /// <b>The harness records a consume whether the pipeline threw or not</b>,
    /// so <see cref="Consumed"/> answers "did it arrive" and never "what
    /// happened to it". A saga event that no longer applies faults by default
    /// (§9.6's <c>OnUnhandledEvent</c>), and every negative in this file would
    /// stay green through it — the transition did not run, which is what the
    /// negative asserts, and the message went to the error queue, which is
    /// what nothing asked. Read this list after a positive has pinned the
    /// point in time, exactly as the negatives are.
    /// <para>
    /// <b><see cref="Spent"/>, and it is load-bearing rather than tidy.</b>
    /// The token-less overload enumerates until the harness's inactivity bound
    /// — which is the harness's ONE shared bound, so a mid-test read of this
    /// list spends it and every assertion after it answers immediately and
    /// falsely. Measured: two of this file's cancellation tests failed on a
    /// message the saga had never been given a chance to consume, in exactly
    /// ten seconds, and both pass in under one with the token here.
    /// </para>
    /// </remarks>
    private static IEnumerable<Exception?> ConsumeFaults<T>(ITestHarness harness)
        where T : class =>
        harness.Consumed.Select<T>(Spent()).Select(m => m.Exception);

    [Fact]
    public async Task Commands_are_sent_and_events_are_published()
    {
        // The distinction §9.6 rests on, asserted directly: publishing a
        // command would deliver it to every subscriber that bound the type, and
        // nothing else in the suite would notice.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));

            // The positive first, which is what gives the negative below a
            // point in time to be false at.
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();
            (await NotYetPublished<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task The_order_lines_are_mapped_rather_than_forwarded()
    {
        // ReserveStock owns its line type, so versioning OrderPlaced does not
        // version Inventory's command (§9.6). Asserted on the product and the
        // quantity because a StockLine has nothing else — and the absence of a
        // price is the whole point of the separate type.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));

            (await Sent<ReserveStock>(harness, m =>
                m.OrderId == orderId &&
                m.Lines.Count == 1 &&
                m.Lines[0].ProductId == SagaContracts.Product &&
                m.Lines[0].Quantity == 2))
                    .ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Payment_declined_releases_stock_before_cancelling()
    {
        // Appendix C names this one: "harness tests including the
        // payment-declined compensation ordering".
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentDeclined(orderId, "insufficient_funds"));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            // CancelOrder must not be sent until stock is confirmed released —
            // and "not yet" needs a point in time to be false *at*. The saga
            // consuming PaymentDeclined is that point.
            (await Consumed<PaymentDeclined>(harness, m => m.OrderId == orderId)).ShouldBeTrue();
            (await NotYetSent<CancelOrder>(harness, m => m.OrderId == orderId)).ShouldBeFalse();

            await Publish(harness, SagaContracts.StockReleased(orderId));

            // The reason, not just the send. Both exits from Compensating read
            // ctx.Saga.CancelReason, so a transition that forgets to set it on
            // entry produces a CancelOrder carrying null — which this assertion
            // fails on and an unqualified one would not.
            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.PaymentDeclined))
                    .ShouldBeTrue();
        }
    }

    [Fact]
    public async Task A_payment_timeout_compensates_with_its_own_reason()
    {
        // The same compensation as a decline and deliberately not the same
        // reason: a PSP that went quiet and a customer whose bank said no are
        // one dimension value apart on orders.cancelled (§13.3) and a different
        // incident. Collapsing the pair is invisible to the decline test above.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));

            // The scheduled message, published directly rather than waited for.
            // Letting the schedule fire would take fifteen minutes, and what is
            // under test is the transition rather than MassTransit's timer.
            await Publish(harness, new PaymentAuthorisationExpired(orderId));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReleased(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.PaymentTimeout))
                    .ShouldBeTrue();
        }
    }

    [Fact]
    public async Task A_failed_reservation_cancels_out_of_stock()
    {
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReservationFailed(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.OutOfStock))
                    .ShouldBeTrue();

            // No compensation: nothing was reserved, so nothing is released.
            (await NotYetSent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task A_stock_timeout_cancels_with_its_own_reason()
    {
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, new StockReservationExpired(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.StockTimeout))
                    .ShouldBeTrue();
        }
    }

    [Fact]
    public async Task A_timeout_that_arrives_after_its_wait_has_ended_changes_nothing()
    {
        // §9.8's old reason for the saga endpoint carrying no inbox filter, and
        // the half of it that survived PR-21 removing that exemption:
        // a state machine is idempotent by construction, because a transition
        // that does not apply in the current state is simply not applicable.
        // This is that claim, measured — the stock timeout is unscheduled on
        // StockReserved, and a copy already in flight must not cancel a paid
        // order.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));

            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, new StockReservationExpired(orderId));

            // The point in time the negative is false at: the stale timeout has
            // been delivered and had nothing to match.
            (await Consumed<StockReservationExpired>(harness, m => m.OrderId == orderId))
                .ShouldBeTrue();
            (await NotYetSent<CancelOrder>(harness, m => m.OrderId == orderId)).ShouldBeFalse();

            // "Changes nothing" has to include not faulting, and until this
            // line the test could not fail for the reason it named: a saga
            // event that does not apply in the current state throws by
            // default, and the two assertions above are green either way —
            // no transition ran, and the message went to the error queue.
            // ADR-021 leans on this being harmless, because its scheduler
            // cancels nothing and every order therefore keeps all of its
            // timeouts until they fire.
            ConsumeFaults<StockReservationExpired>(harness).ShouldAllBe(e => e == null);
        }
    }

    [Fact]
    public async Task A_redelivered_event_is_ignored_rather_than_faulted()
    {
        // §9.4 guarantees at-least-once, and §9.8's inbox suppresses the
        // completed redelivery — the outbox preserves the event's message id,
        // so a republished row arrives as the same message. What the saga's
        // own state must absorb is the delivery the inbox never recorded: the
        // filter writes its row after the consumer returns, so a crash between
        // the saga state committing and that write leaves the next delivery
        // free to land on an instance that has moved on. "Not applicable" is
        // OnUnhandledEvent(x => x.Ignore()) and not a default that throws:
        // without it, six attempts of §9.8's retry policy end in the error
        // queue §13.6 pages on, for a duplicate the design considers correctly
        // absorbed.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));

            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            // A second delivery of the same fact. It carries its own message
            // id, and that is a property of this harness rather than of the
            // wire: §12.5's saga suite configures no inbox at all, so the id
            // decides nothing here and the stimulus is the state machine's
            // input either way. **Not "what an outbox republish looks like"**,
            // which is what this line used to say — a republish carries the
            // id the outbox persisted, and in production it is the inbox row
            // that fails to exist, not the id that changes.
            StockReserved redelivered = SagaContracts.StockReserved(orderId);
            await Publish(harness, redelivered);

            (await Consumed<StockReserved>(harness, m => m.MessageId == redelivered.MessageId))
                .ShouldBeTrue();

            ConsumeFaults<StockReserved>(harness).ShouldAllBe(e => e == null);

            // And absorbed rather than merely survived: one authorisation for
            // one order, not two charges because the event arrived twice. Read
            // as of now, for the reason every negative here is — the positive
            // above is the point in time, and a waiting read would hand a late
            // second send somewhere to hide.
            harness.Sent
                .Select<AuthorisePayment>(Spent())
                .Count(m => m.Context.Message.OrderId == orderId)
                .ShouldBe(1);
        }
    }

    [Fact]
    public async Task Authorised_payment_confirms_the_order_and_waits_for_despatch()
    {
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));

            // Currency travels with the amount — a bare decimal is a charge
            // waiting to be made in the wrong denomination (§9.6).
            (await Sent<AuthorisePayment>(harness, m =>
                m.OrderId == orderId &&
                m.CustomerId == Customer &&
                m.Amount == SagaContracts.Total &&
                m.Currency == SagaContracts.Currency))
                    .ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-1"));

            (await Sent<ConfirmOrder>(harness, m =>
                m.OrderId == orderId &&
                m.PaymentReference == "psp-ref-1"))
                    .ShouldBeTrue();

            // Not finalised: the order is confirmed, not finished, and the
            // instance has to survive to time the despatch out. Confirmed is a
            // state precisely because a wait the machine cannot represent is a
            // wait it cannot time out.
            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task Despatch_marks_the_order_shipped_and_finalises()
    {
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-2"));
            await Publish(harness, SagaContracts.ShipmentDispatched(orderId, "TRACK-9"));

            (await Sent<MarkOrderShipped>(harness, m =>
                m.OrderId == orderId &&
                m.TrackingNumber == "TRACK-9"))
                    .ShouldBeTrue();

            // SetCompletedWhenFinalized deletes the instance, which is why
            // §9.6's diagram has no Shipped state: it would be one no saga is
            // ever observed in.
            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_despatch_timeout_escalates_rather_than_compensating()
    {
        // The wait with no automatic compensation: payment is taken and stock
        // is gone, so the timeout escalates to a human instead. "No timeout" is
        // not the alternative (§9.6).
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-3"));
            await Publish(harness, new DespatchExpired(orderId));

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.NotDespatched))
                    .ShouldBeTrue();

            // Not cancelled, and this is what separates an escalation from a
            // compensation: the customer has paid and the parcel may yet leave.
            (await NotYetSent<CancelOrder>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task A_release_timeout_cancels_the_order_and_escalates_the_stock()
    {
        // Two sends from one transition, and they answer different people. The
        // customer must not be left waiting on Inventory, so the order is
        // cancelled regardless; the stranded reservation is Inventory's to
        // resolve, so it is escalated separately.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentDeclined(orderId, "do_not_honour"));
            await Publish(harness, new StockReleaseExpired(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.PaymentDeclined))
                    .ShouldBeTrue();

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.StockNotReleased))
                    .ShouldBeTrue();
        }
    }

    [Fact]
    public async Task A_cancellation_while_awaiting_stock_releases_the_reservation_and_never_charges()
    {
        // The defect this suite could not see: §11.4's endpoint cancels the
        // AGGREGATE, and until the machine declared Event<OrderCancelled> the
        // saga went on coordinating — reserving stock and authorising a card
        // for an order the customer had already cancelled.
        //
        // ReserveStock is in flight here, so the reservation may or may not
        // exist. Compensating is the state for exactly that: release it, and
        // wait, because a release nobody waits on is a reservation nobody
        // notices is stranded.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            // The race the severity was about, driven rather than argued: the
            // reservation lands AFTER the cancellation. Nothing may charge.
            StockReserved late = SagaContracts.StockReserved(orderId);
            await Publish(harness, late);

            (await Consumed<StockReserved>(harness, m => m.MessageId == late.MessageId)).ShouldBeTrue();
            (await NotYetSent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeFalse();

            // And it is absorbed rather than filed. It used to be the
            // OnUnhandledEvent catch-all doing this; Compensating now writes
            // Ignore(StockReserved) explicitly, so what this asserts is a
            // declared transition rather than a default — which is the whole
            // difference between a decision and an omission (§9.6).
            ConsumeFaults<StockReserved>(harness).ShouldAllBe(e => e == null);

            await Publish(harness, SagaContracts.StockReleased(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.CustomerRequest))
                    .ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_cancellation_while_awaiting_payment_compensates_and_sends_no_second_authorisation()
    {
        // Stock is held and AuthorisePayment has already gone. What must not
        // happen is a SECOND authorisation, and what must happen is the
        // decline branch's own compensation under the customer's reason.
        //
        // The name used to end "and never charges", which this body does not
        // establish and this transition does not guarantee: the authorisation
        // is already with Payments. Whether it completes is Payments' race,
        // and the case where it does is the test below.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));

            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReleased(orderId));

            // The reason, not just the send: Compensating reads it off the
            // instance, so a branch that forgets to record it on entry sends a
            // CancelOrder carrying null and an unqualified assertion passes.
            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.CustomerRequest))
                    .ShouldBeTrue();

            harness.Sent
                .Select<AuthorisePayment>(Spent())
                .Count(m => m.Context.Message.OrderId == orderId)
                .ShouldBe(1);
        }
    }

    [Fact]
    public async Task A_payment_authorised_while_compensating_escalates_rather_than_being_ignored()
    {
        // The case the two halves of this branch created between them.
        // OnUnhandledEvent(Ignore) was added so a redelivered event does not
        // page anyone — and Compensating had no PaymentAuthorised transition,
        // so the catch-all would have swallowed the money arriving after a
        // cancellation. That is Confirmed's cancelled_after_payment case by
        // the other door, and §3.2 gives Payments no refund command, so
        // silence is the one outcome it must not have.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));

            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            // In Compensating now, waiting on StockReleased — and Payments
            // authorises anyway.
            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "auth-late"));

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.CancelledAfterPayment))
                    .ShouldBeTrue();

            // And it did not reach the error queue: the point is that this is
            // handled, not merely that it is loud.
            ConsumeFaults<PaymentAuthorised>(harness).ShouldAllBe(e => e == null);

            // The saga is STILL RUNNING, and this assertion is the one that
            // keeps the runbook honest. Confirmed's cancelled_after_payment
            // finalises; this one is raised mid-wait, so the review row can
            // sit beside a live instance until StockReleased or the
            // ReleaseTimeout. A runbook written for the finalised case tells
            // an on-call the state row is gone, and without this line nothing
            // contradicts it.
            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task A_cancellation_after_confirmation_escalates_rather_than_compensating()
    {
        // The one cancellation this machine cannot compensate: the card is
        // authorised, and undoing that is a refund §3.2 gives Payments no
        // contract to accept. So it escalates and finalises, on the despatch
        // timeout's own argument — and unscheduling is what stops a false
        // not_despatched review being raised three days later for an order
        // that was cancelled.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-4"));

            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.CancelledAfterPayment))
                    .ShouldBeTrue();

            // Not a compensation: the reservation is being picked, and telling
            // Inventory to drop it is not this machine's call to make.
            (await NotYetSent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeFalse();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_cancellation_the_saga_itself_caused_finds_no_instance_and_is_discarded()
    {
        // The routine case, and the one that would have made this fix worse
        // than the defect: every cancellation the saga causes ends in
        // Finalize, so the OrderCancelled the aggregate then publishes arrives
        // at a queue whose instance has just been deleted. A missing instance
        // must be discarded rather than faulted, or every cancelled order
        // files an error-queue entry and pages someone (§13.6).
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReservationFailed(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.OutOfStock))
                    .ShouldBeTrue();

            OrderCancelled echo = SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.OutOfStock);
            await Publish(harness, echo);

            (await Consumed<OrderCancelled>(harness, m => m.MessageId == echo.MessageId)).ShouldBeTrue();

            ConsumeFaults<OrderCancelled>(harness).ShouldAllBe(e => e == null);
        }
    }

    [Fact]
    public async Task A_cancellation_while_compensating_changes_nothing()
    {
        // Compensating already ends in a cancellation, so the customer's
        // request adds nothing to do. It is Ignored explicitly rather than
        // left to OnUnhandledEvent, because a reader cannot tell a decision
        // from an omission — and the assertion is that the release in flight
        // is not disturbed.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentDeclined(orderId, "do_not_honour"));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            OrderCancelled cancelled =
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest);
            await Publish(harness, cancelled);

            (await Consumed<OrderCancelled>(harness, m => m.MessageId == cancelled.MessageId))
                .ShouldBeTrue();
            ConsumeFaults<OrderCancelled>(harness).ShouldAllBe(e => e == null);

            // Ignored means the state is untouched, not merely that nothing
            // was sent — the compensation still has to be waiting on Inventory
            // when the release arrives.
            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            await Publish(harness, SagaContracts.StockReleased(orderId));

            // payment_declined, not customer_request: the reason recorded on
            // entry is the one that caused the compensation, and a cancellation
            // arriving mid-flight must not rewrite it.
            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.PaymentDeclined))
                    .ShouldBeTrue();

            harness.Sent
                .Select<ReleaseStock>(Spent())
                .Count(m => m.Context.Message.OrderId == orderId)
                .ShouldBe(1);
        }
    }

    [Fact]
    public void The_machine_declares_the_states_the_chapter_draws_and_no_others()
    {
        // §9.6's own rule about its diagram, turned into a test: "A picture
        // that shows states the code does not have is a specification the code
        // silently fails to meet." Cancelled and Shipped are terminal OUTCOMES
        // and must not appear — SetCompletedWhenFinalized deletes the instance,
        // so a state for either would be one no saga is ever observed in.
        //
        // Initial and Final are MassTransit's and are always present.
        OrderFulfilmentSaga saga = new();

        saga.States
            .Select(s => s.Name)
            .ShouldBe(
                ["Initial", "Final", "AwaitingStock", "AwaitingPayment", "Confirmed", "Compensating"],
                ignoreOrder: true);
    }

    [Fact]
    public void Every_wait_state_declares_a_schedule()
    {
        // The rule Appendix C names — "a timeout on every wait state" — as a
        // structural claim rather than four behavioural ones. The tests above
        // prove each timeout FIRES; this proves none was quietly dropped, which
        // is the failure a behavioural suite cannot see: a wait whose schedule
        // is deleted simply has no test left to go red.
        OrderFulfilmentSaga saga = new();

        object?[] schedules =
        [
            saga.StockTimeout,
            saga.PaymentTimeout,
            saga.DespatchTimeout,
            saga.ReleaseTimeout
        ];

        schedules.ShouldAllBe(s => s != null);

        // Four states that wait, four schedules. The equality is the guard: a
        // fifth wait state added without a schedule fails here, and so does a
        // schedule left behind by a wait state that was removed.
        schedules.Length.ShouldBe(saga.States.Count(s => s.Name is not ("Initial" or "Final")));
    }

    [Fact]
    public async Task An_event_for_an_order_with_no_instance_is_discarded_in_silence()
    {
        // **Measured, because two review findings turn on it and neither the
        // chapter nor this file said which way it goes.** MassTransit's
        // policy for a NON-INITIAL event that correlates to no instance is
        // not the same thing as OnUnhandledEvent, which governs an event that
        // reaches an instance in a state that does not handle it. This is the
        // other one, and the default is to consume the message CLEANLY and
        // drop it: no transition, no fault, no error-queue entry, nothing on
        // §13.6's pager.
        //
        // That default is what makes OrderCancelled's explicit Discard cheap
        // — it states the routine echo rather than changing anything — and it
        // is also what makes the two races in #123 and #124 silent rather
        // than loud. Pinning it here means the day a MassTransit upgrade
        // changes the default, this suite says so instead of the residual
        // quietly closing itself.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orphan = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.PaymentAuthorised(orphan, "auth-1"));

            (await Consumed<PaymentAuthorised>(harness, m => m.OrderId == orphan)).ShouldBeTrue();

            ConsumeFaults<PaymentAuthorised>(harness).ShouldAllBe(e => e == null);

            // And nothing was escalated, which is the half that matters: the
            // Compensating transition one state over exists precisely to turn
            // this event into a review row, and it cannot run for an instance
            // that is gone.
            (await NotYetSent<FlagOrderForReview>(harness, m => m.OrderId == orphan)).ShouldBeFalse();
        }
    }
}
