using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;
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
    /// rather than rescaled to the suite here now — a ratio nobody re-ran
    /// is not evidence about a suite that has since grown.
    /// The two survivors are the structural pair the file then ended with, and
    /// the ones beside them now would survive the same way. <b>How many of
    /// those there are is deliberately not written here</b>: this sentence has
    /// said "a third" while there were four, and a count kept in prose beside a
    /// measurement it is not part of is a count nobody re-checks. They
    /// construct the state machine and never start a bus — correctly, and
    /// worth knowing, because they are the ones that would keep a deleted
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
    /// Publishes, and does not return until the saga has consumed <em>that</em>
    /// message. The wait is the point: without it two consecutive publishes are
    /// a race, and losing it fails a later assertion wearing the saga's message
    /// rather than the runner's.
    /// </summary>
    /// <remarks>
    /// <b>This and the helpers under it exist to carry one argument</b>, and it
    /// is the one xUnit1051 requires on every call that accepts a token —
    /// spelled out at each of the call sites it would bury the assertions.
    /// They also keep §9.6's distinction visible at the call site: a command is
    /// <see cref="Sent"/> and an event is published, the harness records the
    /// two separately, and a test that asks the wrong list fails while looking
    /// like a saga defect. There is no waiting <c>Published</c> sibling of
    /// <see cref="Sent"/>, because nothing here asserts a publish positively:
    /// every message this saga emits is a command, and the one published
    /// assertion in the suite is the negative that proves it.
    /// <see cref="NotYetPublished"/> is that one.
    /// <para>
    /// <b>A publish returns when the message reaches the transport, not when
    /// the saga has consumed it.</b> So a test that publishes
    /// <c>OrderPlaced</c> and then <c>StockReserved</c> is asking for the
    /// second to be handled in <c>AwaitingStock</c> while nothing has put the
    /// instance there — and the reverse arrival is discarded in silence when no
    /// instance exists yet — see
    /// <see cref="An_event_for_an_order_with_no_instance_is_discarded_in_silence"/>
    /// — or faults when the state has no branch for it. Neither outcome is
    /// visible where it happens: the test runs on, and the <em>next</em> waiting
    /// assertion bills the full inactivity bound and reports a command the saga
    /// did not send.
    /// </para>
    /// <para>
    /// <b>Measured rather than argued.</b> Forcing the losing order of
    /// <see cref="A_payment_timeout_compensates_with_its_own_reason"/> — the
    /// scheduled expiry published before <c>StockReserved</c> — gives
    /// <c>ReleaseStock</c> not sent, after 10.1 s. That is exactly what CI
    /// reported on the merge commit of #135: the same assertion, false, at ten
    /// seconds.
    /// </para>
    /// <para>
    /// <b>The barrier is here rather than at the call sites, and that is the
    /// whole fix.</b> #107 was closed by interleaving waits into the one test
    /// that had failed; fourteen of this file's twenty-seven harness tests
    /// still had twenty unfenced publishes between them, and the next one
    /// failed on the very run that merged the fix. Per-site discipline fails
    /// open — the test that forgets is the test that flakes, and it flakes on a
    /// loaded runner and nowhere else. A barrier inside the helper every test
    /// already calls leaves nothing to forget, which is the argument
    /// <c>Common.Web.Tests</c>' assembly-wide parallelisation attribute won
    /// over a shared collection.
    /// </para>
    /// <para>
    /// <b>The wait is on this message's own id, not on its type.</b>
    /// <see cref="A_redelivered_event_faults_rather_than_being_absorbed_silently"/>
    /// and <see cref="A_second_confirmation_in_Confirmed_is_absorbed_rather_than_faulted"/>
    /// each deliver one type twice, and a type-level wait would match the first
    /// delivery and return immediately, fencing nothing for exactly the tests
    /// whose subject is a second arrival. They are named rather than counted
    /// because a count of them is a number nobody re-runs. The id is read off
    /// the send context rather than the contract because the two differ: a
    /// scheduled expiry carries no envelope of its own.
    /// </para>
    /// <para>
    /// <b>It costs nothing on a green run</b> — the consume has already
    /// happened or is about to, so this returns in milliseconds. Measured: the
    /// suite runs in the same one second it did unfenced.
    /// <b>A fault releases it too</b>, which is what makes that true rather
    /// than lucky: the harness records a delivery whether the pipeline returned
    /// or threw, so an event the machine has no branch for satisfies this
    /// barrier as readily as one it handles. The barrier is about ordering and
    /// never about outcome, and a test that cares which it got reads
    /// <see cref="ConsumeFaults"/>.
    /// What it does spend the inactivity bound on is a message
    /// <em>no consumer takes at all</em> — for this harness, a type the machine
    /// does not declare. Measured at 10.0 s, and worth it: that is a real
    /// defect, and the assertion below names it where it happened.
    /// </para>
    /// <para>
    /// The explicit <see cref="Sent"/> waits the tests still carry are not
    /// redundant with this. They fenced, and now they only assert — that the
    /// transition sent the command it owes — which is what they were always
    /// worth keeping for.
    /// </para>
    /// </remarks>
    private static async Task Publish<T>(ITestHarness harness, T message)
        where T : class
    {
        Guid? messageId = null;
        await harness.Bus.Publish(
            message,
            context => messageId = context.MessageId,
            TestContext.Current.CancellationToken);

        // Unset, this degrades into the type-level wait the paragraph above
        // rejects — null == null matches the first consume of T and fences
        // nothing — so the barrier fails loudly rather than quietly weakening.
        messageId.ShouldNotBeNull();

        (await ConsumedWithId<T>(harness, messageId)).ShouldBeTrue(
                $"a published {typeof(T).Name} must reach the saga before the test publishes " +
                "the message that depends on it — an unfenced publish is a race the runner " +
                "loses under load, and it fails a later assertion wearing the saga's message.");
    }

    private static Task<bool> Sent<T>(ITestHarness harness, Func<T, bool> match)
        where T : class =>
        harness.Sent.Any<T>(m => match(m.Context.Message), TestContext.Current.CancellationToken);

    private static Task<bool> Consumed<T>(ITestHarness harness, Func<T, bool> match)
        where T : class =>
        harness.Consumed.Any<T>(m => match(m.Context.Message), TestContext.Current.CancellationToken);

    /// <summary>
    /// <see cref="Consumed"/> over the transport's message id rather than the
    /// contract's, which is what <see cref="Publish"/> needs and no test does.
    /// </summary>
    /// <remarks>
    /// The two are different ids and only one of them exists for every message
    /// here: §9.1's envelope gives a contract its own <c>MessageId</c>, and the
    /// saga's five scheduled expiries are not contracts (Appendix D) and carry
    /// no envelope at all. The send context has one either way.
    /// </remarks>
    private static Task<bool> ConsumedWithId<T>(ITestHarness harness, Guid? messageId)
        where T : class =>
        harness.Consumed.Any<T>(m => m.Context.MessageId == messageId, TestContext.Current.CancellationToken);

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
    /// then accept the very command the negative was there to forbid. So
    /// "not yet" needs a point in time to be false at.
    /// </para>
    /// <para>
    /// <b>Where the negative follows a publish, <see cref="Publish"/> is that
    /// point</b> and the caller supplies nothing — it returns only once the
    /// saga has consumed the message, which is the strongest point in time
    /// available. This used to read "the caller's job is to put a positive
    /// assertion before each of these", and that was the whole defect: a job
    /// left to callers is a job fourteen of them did not do. What is still the
    /// caller's is a negative asserted anywhere <em>else</em> — after a
    /// scheduled message the test did not publish, or partway through a
    /// transition chain — where nothing has pinned the moment for it.
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
    /// (§9.6 keeps MassTransit's default), and every negative in this file would
    /// stay green through it — the transition did not run, which is what the
    /// negative asserts, and the message went to the error queue, which is
    /// what nothing asked. Read this list once the delivery it asks about has
    /// been pinned — which, for a message the test published, is what
    /// <see cref="Publish"/> already did before returning.
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

            // **These two waits are assertions now and not the ordering, and
            // the difference is #107's whole second act.** They were added here
            // as the fix for it: a publish returns when the message reaches the
            // transport, not when the saga has consumed it, so under a loaded
            // parallel run PaymentDeclined could reach the endpoint before
            // OrderPlaced had created the instance (discarded in silence) or
            // before StockReserved had moved it to AwaitingPayment (a state
            // with no decline branch, so a fault) — and both landed on the
            // ReleaseStock assertion below, which burned the inactivity timeout
            // and failed wearing "the saga did not send".
            //
            // Fixing the one test that had failed left twenty unfenced
            // publishes in fourteen others, and the merge commit's own CI run
            // failed on one of them. The ordering is Publish's job from that
            // change on. What these lines still do is assert the machine sent
            // the command each transition owes, which is what they were worth
            // keeping for.
            //
            // **They are therefore not a template.** A test that needs no such
            // assertion does not need a wait either; the barrier is not
            // something a call site can forget.
            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReserved(orderId));
            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentDeclined(orderId, "insufficient_funds"));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            // CancelOrder must not be sent until stock is confirmed released —
            // and "not yet" needs a point in time to be false *at*. The Publish
            // three lines up IS that point now: it returned only once the saga
            // had consumed PaymentDeclined. A Consumed<PaymentDeclined> stood
            // here to establish it and was deleted when the barrier moved into
            // the helper — a second wait for the same fact reads as though the
            // first were not enough.
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
    public async Task A_redelivered_event_faults_rather_than_being_absorbed_silently()
    {
        // §9.4 guarantees at-least-once, and §9.8's inbox suppresses the
        // completed redelivery — the outbox preserves the event's message id,
        // so a republished row arrives as the same message. What the saga's
        // own state must absorb is the delivery the inbox never recorded: the
        // filter writes its row after the consumer returns, so a crash between
        // the saga state committing and that write leaves the next delivery
        // free to land on an instance that has moved on. **It faults**, and
        // this test asserts the fault rather than its absence.
        //
        // **This test asserted the opposite until #128 was understood.** An
        // OnUnhandledEvent(x => x.Ignore()) catch-all made every such
        // arrival quiet, on the argument that it can only be a duplicate.
        // The window it was justified by contains the in-memory outbox's
        // flush, so it also contains the case where the instance advanced
        // and its commands were never sent — there the redelivery is the
        // last thing that could notice, and quiet is permanent loss.
        //
        // The stimulus below is the POST-flush half: it republishes after
        // the first delivery completed, so the commands really did go out
        // and the second delivery is a genuine duplicate. That is the one
        // case the old behaviour got right, and it now costs six retries
        // and one error-queue message — the price of the other two being
        // loud. The pre-flush half is unreachable from this harness, which
        // cannot interrupt between the repository commit and the flush, so
        // nothing here covers it; #128 is where it stops existing.
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

            // The subject, and the reason §12.5 insists on reading Exception
            // rather than an effect: "no transition ran" is what BOTH a
            // silent absorb and a fault look like from every other
            // assertion in this file. Only this one tells them apart, and
            // it is the assertion that changed direction when the catch-all
            // was removed.
            ConsumeFaults<StockReserved>(harness)
                .ShouldContain(
                    e => e != null,
                    "an event no transition accepts must reach the error queue §13.6 pages on. " +
                    "Absorbing it silently would answer a lost-command crash and a misroute the " +
                    "same way it answers a duplicate (#128).");

            // And the fault costs nothing beyond the noise: one authorisation
            // for one order, not two charges because the event arrived twice.
            // The instance refuses the transition either way — what changed
            // is whether anybody is told. Read as of now, for the reason every
            // negative here is: the positive above is the point in time, and a
            // waiting read would hand a late second send somewhere to hide.
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

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            // **Sending the command is not confirming the order, and this
            // assertion is #126.** The machine waits here for the aggregate's
            // own acknowledgement; it used to call this state Confirmed, which
            // made every downstream branch reason about a handoff that had not
            // happened yet.
            (await saga.Exists(orderId, x => x.AwaitingConfirmation)).ShouldNotBeNull();

            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));

            // Not finalised: the order is confirmed, not finished, and the
            // instance has to survive to time the despatch out. Confirmed is a
            // state precisely because a wait the machine cannot represent is a
            // wait it cannot time out.
            (await saga.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task A_cancellation_before_the_confirmation_lands_releases_the_stock()
    {
        // #126's payload. Reaching AwaitingConfirmation means ConfirmOrder is
        // in flight and NOTHING downstream has been told: no OrderConfirmed has
        // been published, so Shipping has no despatch to prepare. The old
        // machine took the Confirmed branch here — withholding ReleaseStock on
        // the argument that a reservation being picked must not be dropped,
        // for a picking that was not happening — and stranded the reservation.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReserved(orderId));
            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-126"));
            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.AwaitingConfirmation)).ShouldNotBeNull();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();
            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            // And no review row: Payments voids off OrderCancelled itself
            // (§3.2), and what made the confirmed case a human's problem was a
            // despatch that might already be moving. There is none here, so
            // there is nothing for a person to do.
            harness.Sent
                .Select<FlagOrderForReview>(Spent())
                .ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task A_confirmation_arriving_after_compensation_began_escalates()
    {
        // The other side of #126's race, and the reason Compensating writes
        // OrderConfirmed out rather than ignoring it. OrderConfirmed and
        // OrderCancelled are both Ordering's own outbox rows and §9.4 orders
        // nothing between them, so the cancellation can reach the saga first —
        // and the confirmation landing afterwards is the ONLY evidence that
        // the aggregate had already confirmed, Shipping had already been told,
        // and the ReleaseStock now in flight is for stock somebody may be
        // picking. Absorbing it would restore the silence #126 was about.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReserved(orderId));
            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-127"));
            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.CancelledAfterConfirmation))
                    .ShouldBeTrue();

            // The absence of the exception, not the absence of the effect:
            // harness.Consumed records a delivery whether the pipeline returned
            // or threw, so an unwritten branch would look identical from the
            // assertion above if it happened to have been sent already.
            ConsumeFaults<OrderConfirmed>(harness).ShouldAllBe(e => e == null);

            // Still waiting on Inventory — the exits own the cancellation, so
            // this branch adds the row and nothing else.
            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task A_confirmation_that_never_arrives_escalates_rather_than_hanging()
    {
        // The bound on #126's new wait. ConfirmOrder is a local command with a
        // retry budget, and the aggregate REFUSING it is not this case — that
        // is a Rule failure CommandConsumer acks, and the cancellation behind
        // it reaches the saga on its own event. What is left is the command
        // never being consumed at all, with the card authorised and the stock
        // held, which is a person's problem rather than the machine's.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReserved(orderId));
            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-128"));
            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.AwaitingConfirmation)).ShouldNotBeNull();

            // Driven rather than waited out, exactly as the other timeout tests
            // are: the schedule is ten minutes and a test that slept for it
            // would be a test nobody runs.
            await Publish(harness, new ConfirmationExpired(orderId));

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.NotConfirmed))
                    .ShouldBeTrue();

            // No CancelOrder: §3.2 gives Ordering no refund command, so there
            // is nothing to compensate WITH and pretending otherwise would
            // cancel an order whose money has moved.
            harness.Sent
                .Select<CancelOrder>(Spent())
                .Count(m => m.Context.Message.OrderId == orderId)
                .ShouldBe(0);

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task Despatch_marks_the_order_shipped_and_finalises()
    {
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            // The Sent lines assert the command each transition owes; they
            // are NOT the ordering, which is Publish's job. This chain grew a
            // fifth message with #126 and is the longest here, so it is the
            // one where the difference matters most — and the comment that
            // stood here credited the waits with #107's fix, which is the
            // template the payment-declined test four hundred lines up was
            // rewritten to retire.
            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReserved(orderId));
            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-2"));
            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            // The acknowledgement is what puts the saga in Confirmed (#126).
            // AwaitingConfirmation binds ShipmentDispatched too, so arriving
            // early is handled rather than faulted — but this test drives the
            // ordinary path, and the wait is what keeps it doing so.
            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> confirmed =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await confirmed.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull();

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
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReserved(orderId));
            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-3"));
            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            // DespatchTimeout is armed on entering Confirmed, and #126 moved
            // that entry onto the acknowledgement — so the despatch wait does
            // not begin until the order actually is confirmed, which is the
            // point of the split. The expiry below is discarded unless the
            // token is set, so the wait for Confirmed is what makes this test
            // drive a transition rather than a no-op.
            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> armed =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await armed.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull();

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
    public async Task A_cancellation_while_awaiting_stock_requests_release_and_sends_no_authorisation()
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
        //
        // **The name says REQUESTS release, and the two words are the whole
        // of what this harness can see.** It observes a ReleaseStock sent;
        // whether Inventory acted on a reservation is #125, and under that
        // ordering it may not have. A name claiming the reservation was
        // released makes a green test look like proof of the one guarantee
        // the implementation now says it cannot give.
        //
        // The trailing "and never charges" went for the same reason, and its
        // sibling one state over lost that phrase in an earlier round: the
        // negative below is read as of now, after a positive, so what it
        // establishes is that no authorisation HAS been sent, not that none
        // ever will be.
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

            // And it is absorbed rather than filed. A catch-all used to do
            // this; Compensating writes Ignore(StockReserved) explicitly and
            // the catch-all is gone, so this now asserts the declared
            // transition and NOTHING ELSE could make it pass — delete the
            // Ignore and the event faults. Measured by deleting it.
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
    public async Task A_cancellation_carries_its_own_reason_into_compensation_from_AwaitingStock()
    {
        // **The reason the branch above overwrote.** Both OrderCancelled
        // transitions recorded CancelReasons.CustomerRequest as a literal, so
        // Compensating's exit sent CancelOrder under a reason nobody chose —
        // and every cancellation test in this file used customer_request, which
        // is exactly why it survived: an assertion comparing the right answer
        // against a hard-coded copy of the right answer cannot fail.
        //
        // §11.4 parses the whole five-code CancellationReasons map, so any of
        // them can arrive here. payment_declined is chosen because it is the
        // one a reader is most likely to assume the saga alone can produce.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.PaymentDeclined));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReleased(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.PaymentDeclined))
                    .ShouldBeTrue();
        }
    }

    [Fact]
    public async Task A_cancellation_carries_its_own_reason_into_compensation_from_AwaitingPayment()
    {
        // The same claim one state over, and it is a separate test rather than
        // a theory case because the two transitions are separate lines that
        // were written together and were wrong together. A gate that pins one
        // of a copied pair leaves the copy free to drift, which is this
        // repository's most-repeated failure in its smallest form.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));

            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.OutOfStock));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReleased(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.OutOfStock))
                    .ShouldBeTrue();
        }
    }

    [Fact]
    public async Task A_payment_authorised_while_compensating_escalates_rather_than_being_ignored()
    {
        // The case the two halves of this branch created between them. A
        // catch-all was added so a redelivered event does not page anyone —
        // and Compensating had no PaymentAuthorised transition, so it would
        // have swallowed the money arriving after a cancellation. The
        // catch-all is gone and this transition is not: it was always the
        // escalation that mattered, and the catch-all was what made its
        // absence invisible. That is Confirmed's case by the other door — the same
        // SYMPTOM under a different code, and the difference is SHIPPING:
        // this state cannot despatch and Confirmed may. Not the refund —
        // Payments voids off OrderCancelled (§3.2) on both, and whether it
        // has done so is not knowable from either state, since §9.4 orders
        // nothing between independent consumers and on this door the
        // cancellation has not been sent yet. Either way silence is the one
        // outcome this transition must not have.
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
                m.Reason == ReviewReasons.PaymentAuthorisedDuringCompensation))
                    .ShouldBeTrue();

            // And it did not reach the error queue: the point is that this is
            // handled, not merely that it is loud.
            ConsumeFaults<PaymentAuthorised>(harness).ShouldAllBe(e => e == null);

            // The saga is STILL RUNNING, and this assertion is the one that
            // keeps the runbook honest. Confirmed's
            // cancelled_after_confirmation finalises; this one is raised mid-wait, so the review row can
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
        // A cancellation this machine cannot compensate ITSELF: the card is
        // authorised, and undoing that is a refund §3.2 gives Ordering no
        // command for. Payments consumes OrderCancelled and voids an
        // authorisation already taken, and OrderCancelled is the event this
        // transition fires on — but whether that void has happened is not
        // knowable here, because §9.4 orders nothing between Payments and
        // this saga. What the row escalates that its sibling cannot is
        // SHIPPING: a confirmed order may still despatch. It escalates and
        // finalises on the despatch timeout's own argument. What stops a false not_despatched review
        // three days later is the FINALIZE, not the Unschedule beside it:
        // ADR-021's scheduler cannot cancel, so the timeout stays queued and
        // is discarded on delivery for want of an instance.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-4"));

            // Sent, and that is all a send establishes — the harness registers
            // no command consumer, so nothing here confirms the aggregate.
            // **That gap used to be this test's blind spot and is now its
            // setup (#126).** The saga entered Confirmed on the SEND, so this
            // test reached the branch below without any confirmation having
            // happened, and it asserted the confirmed-order behaviour anyway;
            // the machine now waits, so the acknowledgement has to be driven
            // and the state below means what its name says.
            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            // The Confirmed code, not Compensating's. Both origins used to
            // raise payment_authorised_during_compensation and the row persists nothing else,
            // so the runbook selected its procedure on a saga state that is
            // gone by the time anyone reads the queue.
            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.CancelledAfterConfirmation))
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
    public async Task A_decline_arriving_after_a_cancellation_is_absorbed_rather_than_faulted()
    {
        // The branch the Compensating enumeration missed, and this branch is
        // what made it reachable. Cancelling from AwaitingPayment arrives in
        // Compensating with AuthorisePayment ALREADY SENT and unanswered, so
        // the PSP's verdict can still be either — an authorisation, which the
        // transition above escalates, or a decline, which is this.
        //
        // Absorbed rather than escalated: a decline means no money moved,
        // which is the outcome compensation was heading for. Written as an
        // Ignore rather than left to fault, because an outcome compensation
        // was already heading towards is not worth an error-queue entry.
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

            PaymentDeclined declined = SagaContracts.PaymentDeclined(orderId, "do_not_honour");
            await Publish(harness, declined);

            (await Consumed<PaymentDeclined>(harness, m => m.MessageId == declined.MessageId))
                .ShouldBeTrue();

            // Not faulted, and this assertion is load-bearing again. While an
            // OnUnhandledEvent(x => x.Ignore()) catch-all stood in the machine
            // it produced the identical observable outcome, so this stayed
            // green with the Ignore deleted — verified by deleting it, and the
            // reason the structural test below was written. With the catch-all
            // gone the two outcomes differ: a missing branch faults.
            //
            // Measured on Ignore(StockReserved) rather than on this line,
            // because that one has a behavioural test to catch it: deleting
            // it fails the structural test AND
            // A_cancellation_while_awaiting_stock_requests_release_and_sends_no_authorisation,
            // where under the catch-all only the structural one went red.
            // This event has no such pair, which is the asymmetry the
            // partition below exists for.
            ConsumeFaults<PaymentDeclined>(harness).ShouldAllBe(e => e == null);

            // And nothing escalated. A decline is not a payment_authorised_during_compensation
            // — there is no money for a human to chase.
            (await NotYetSent<FlagOrderForReview>(harness, m => m.OrderId == orderId)).ShouldBeFalse();

            // The compensation is untouched and still waiting on Inventory.
            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();
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
    public async Task A_second_confirmation_in_Confirmed_is_absorbed_rather_than_faulted()
    {
        // **The rollout case, and the one this branch would have paged on.**
        // The old machine entered Confirmed when it SENT ConfirmOrder, so
        // every order it confirmed publishes an OrderConfirmed moments after
        // the instance is already there. The binding this release declares is
        // durable and queue-scoped, so the first new replica starts copying
        // those into the saga queue for instances an old replica advanced —
        // and §15.5's canary runs both releases for the length of its ladder.
        // Left unwritten it faults, and §13.6 pages on the error queue.
        //
        // The same line covers §9.5's unrecorded redelivery, which needs no
        // deploy to happen.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReserved(orderId));
            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-dup"));
            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));
            (await saga.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull();

            // The duplicate carries its own message id, and waiting on THAT id
            // is what makes the assertion below mean anything. Read as of now
            // against an unsynchronised publish, `ConsumeFaults` answers before
            // the second delivery has been consumed at all — measured: the test
            // passed against a machine with no branch here until this wait was
            // added, which is the vacuous-pass shape §12.5 keeps warning about.
            //
            // **`Publish` supplies that wait itself now**, and this line stays
            // as the assertion it also is: the id being the *contract's* and
            // the barrier's being the *transport's* are two different claims,
            // and only this one says a second OrderConfirmed — not a second
            // delivery of the first — reached the machine.
            OrderConfirmed duplicate = SagaContracts.OrderConfirmed(orderId, Customer);
            await Publish(harness, duplicate);

            (await Consumed<OrderConfirmed>(harness, m => m.MessageId == duplicate.MessageId))
                .ShouldBeTrue();

            // The absence of the exception, not the absence of an effect:
            // harness.Consumed records a delivery whether the pipeline
            // returned or threw, so every assertion about "nothing happened"
            // reads the same on a fault. This is the one that does not.
            ConsumeFaults<OrderConfirmed>(harness).ShouldAllBe(e => e == null);

            (await saga.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task A_despatch_that_beats_the_confirmation_still_marks_the_order_shipped()
    {
        // **Splitting the state made this reachable and it was not before.**
        // §3.2 gives Shipping OrderConfirmed too, so the aggregate's one
        // publish fans out to two independent consumers with no ordering
        // between them (§9.4). The old machine was already in Confirmed before
        // that publish existed; now the saga's own copy can be behind a retry
        // or a backlog when the despatch lands.
        //
        // Handled rather than ignored, because ignoring loses MarkOrderShipped.
        // Safe on the aggregate's terms too: Shipping learns of the order only
        // FROM OrderConfirmed, so a despatch arriving at all proves the
        // confirmation committed.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReserved(orderId));
            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-early"));
            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.AwaitingConfirmation)).ShouldNotBeNull();

            // No OrderConfirmed published at all — the despatch arrives first.
            await Publish(harness, SagaContracts.ShipmentDispatched(orderId, "TRACK-EARLY"));

            (await Sent<MarkOrderShipped>(harness, m =>
                m.OrderId == orderId &&
                m.TrackingNumber == "TRACK-EARLY"))
                    .ShouldBeTrue();

            ConsumeFaults<ShipmentDispatched>(harness).ShouldAllBe(e => e == null);

            (await saga.NotExists(orderId)).ShouldBeNull();
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
                [
                    "Initial",
                    "Final",
                    "AwaitingStock",
                    "AwaitingPayment",
                    "AwaitingConfirmation",
                    "Confirmed",
                    "Compensating"
                ],
                ignoreOrder: true);
    }

    [Fact]
    public void Compensating_writes_out_every_event_it_can_receive()
    {
        // The structural half. **Its original reason no longer holds and it
        // is kept for a better one.** While a catch-all stood in the machine,
        // an unwritten branch and an explicit Ignore were observably
        // identical — no fault, no transition, nothing sent — so every
        // behavioural test above stayed green when a branch was deleted. With
        // the catch-all removed a deleted branch faults, and those tests do
        // catch it.
        //
        // What they still cannot catch is an event NOBODY WROTE A TEST FOR.
        // A ninth event arriving in Compensating with no branch faults in
        // production and nowhere in this file, because no test publishes it.
        // That is what the partition below is for, and it is why this test
        // survives the change that removed its first justification.
        //
        // What the machine's own declaration can say is whether the branch is
        // there. Compensating is the state where that matters: it is reachable
        // from AwaitingStock and AwaitingPayment, so most of the declared
        // events can arrive, and the comment beside it claims none is left to
        // the catch-all. PaymentDeclined was missing from that enumeration —
        // which is why this test exists at all.
        //
        // **It was a list of ShouldContain and that did not enforce its own
        // name.** A ninth event declared with no Compensating branch leaves
        // NextEvents returning the same set, so every hard-coded assertion
        // still passes — the gate-coverage regression this file exists to
        // catch, in the test written to catch it.
        //
        // So the subject is the PARTITION rather than a membership list. Every
        // event the machine declares is classified below, the two halves must
        // together account for all of them, and the reachable half must equal
        // what the machine says it accepts. A new event fails the first
        // assertion until someone classifies it, and classifying it as
        // reachable without writing the branch fails the second.
        OrderFulfilmentSaga saga = new();

        string[] reachableHere =
        [
            nameof(saga.StockReleased),
            nameof(saga.PaymentAuthorised),
            nameof(saga.PaymentDeclined),
            nameof(saga.OrderCancelled),
            nameof(saga.StockReserved),
            nameof(saga.StockReservationFailed),

            // #126's addition. AwaitingConfirmation is a third door into this
            // state, and it is the only one that can be entered with an
            // OrderConfirmed still outstanding — the customer cancelled while
            // the aggregate's own confirmation was in flight.
            nameof(saga.OrderConfirmed),
            $"{nameof(saga.ReleaseTimeout)}.Received"
        ];

        // Not reachable in Compensating, and each for a stated reason rather
        // than by omission: OrderPlaced only creates an instance,
        // ShipmentDispatched and the despatch timeout belong to Confirmed,
        // and the stock, payment and confirmation timeouts are unscheduled by
        // the transitions that enter this state.
        string[] notReachableHere =
        [
            nameof(saga.OrderPlaced),
            nameof(saga.ShipmentDispatched),
            $"{nameof(saga.StockTimeout)}.Received",
            $"{nameof(saga.PaymentTimeout)}.Received",
            $"{nameof(saga.ConfirmationTimeout)}.Received",
            $"{nameof(saga.DespatchTimeout)}.Received"
        ];

        string[] declared =
        [
            .. typeof(OrderFulfilmentSaga)
                .GetProperties()
                .Where(pi => typeof(Event).IsAssignableFrom(pi.PropertyType))
                .Select(pi => pi.Name),
            .. typeof(OrderFulfilmentSaga)
                .GetProperties()
                .Where(pi => pi.PropertyType.IsGenericType &&
                    pi.PropertyType.GetGenericTypeDefinition() == typeof(Schedule<,>))
                .Select(pi => $"{pi.Name}.Received")
        ];

        declared.ShouldNotBeEmpty("the reflection scan found no events on this machine");

        reachableHere
            .Concat(notReachableHere)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(
                declared.OrderBy(n => n, StringComparer.Ordinal),
                "every event this machine declares must be classified as reachable in " +
                "Compensating or not. An event in neither list is one nobody decided about, " +
                "which is exactly how PaymentDeclined came to be missing (§9.6).");

        // .AnyReceived is MassTransit's own, one per Schedule, and it is
        // accepted in every state whether or not anybody wrote a branch — so
        // it says nothing about the subject here and would only dilute it.
        // The schedule itself is still classified, through its .Received.
        saga.NextEvents(saga.Compensating)
            .Select(e => e.Name)
            .Where(n => !n.EndsWith(".AnyReceived", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(
                reachableHere.OrderBy(n => n, StringComparer.Ordinal),
                "Compensating accepts exactly the events classified as reachable there. A name " +
                "missing from the machine is a branch nobody wrote, which now faults in " +
                "production and is caught by no test here; one the machine has and this list " +
                "does not is a branch nobody argued.");
    }

    [Fact]
    public void The_two_states_a_confirmation_can_reach_write_it_out()
    {
        // **The residual above, closed for the two states #126 created and
        // narrowed — and it is here because leaving it open cost this branch
        // two defects.** The partition test one method up reads
        // NextEvents(Compensating) and nothing else, so it demanded the
        // OrderConfirmed branch there and said nothing about the two states
        // that actually carry the new event. Both were missed on the first
        // pass: a second OrderConfirmed in Confirmed faulted, and a
        // ShipmentDispatched beating the acknowledgement into
        // AwaitingConfirmation faulted.
        //
        // **A test whose subject is what the gate is looking at**, which is
        // this repository's most-repeated lesson arriving in the one place it
        // had not been applied. It is deliberately NOT a general sweep over
        // every state: what makes a claim here checkable is naming the events
        // a state can receive and why, and that argument has to be written
        // per state rather than generated.
        OrderFulfilmentSaga saga = new();

        // AwaitingConfirmation is entered with ConfirmOrder in flight. The
        // acknowledgement ends the wait; a cancellation compensates; the
        // timeout escalates; and a despatch can beat the acknowledgement,
        // because Shipping subscribes to the same OrderConfirmed this saga
        // does and §9.4 orders nothing between two consumers.
        Accepts(
            saga,
            saga.AwaitingConfirmation,
            [
                nameof(saga.OrderConfirmed),
                nameof(saga.OrderCancelled),
                nameof(saga.ShipmentDispatched),
                $"{nameof(saga.ConfirmationTimeout)}.Received"
            ]);

        // Confirmed is entered BY OrderConfirmed, so a second one is a
        // duplicate — from §9.5's unrecorded redelivery, or from a rolling
        // deploy handing a new replica an instance an old one advanced. It is
        // absorbed rather than faulted, and the saga argues that at the line.
        Accepts(
            saga,
            saga.Confirmed,
            [
                nameof(saga.OrderConfirmed),
                nameof(saga.OrderCancelled),
                nameof(saga.ShipmentDispatched),
                $"{nameof(saga.DespatchTimeout)}.Received"
            ]);
    }

    private static void Accepts(OrderFulfilmentSaga saga, State state, string[] expected) =>
        saga.NextEvents(state)
            .Select(e => e.Name)
            .Where(n => !n.EndsWith(".AnyReceived", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(
                expected.OrderBy(n => n, StringComparer.Ordinal),
                $"{state.Name} accepts exactly the events written out for it. One the machine " +
                "has and this list does not is a branch nobody argued; one this list has and " +
                "the machine does not is an arrival that faults to the error queue.");

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
            saga.ConfirmationTimeout,
            saga.DespatchTimeout,
            saga.ReleaseTimeout
        ];

        schedules.ShouldAllBe(s => s != null);

        // One schedule per state that waits. The equality is the guard: a new
        // wait state added without a schedule fails here, and so does a
        // schedule left behind by a wait state that was removed. #126 was the
        // first change to test it in the first direction — AwaitingConfirmation
        // and ConfirmationTimeout arrived together, and either one alone would
        // have gone red.
        schedules.Length.ShouldBe(saga.States.Count(s => s.Name is not ("Initial" or "Final")));
    }

    [Fact]
    public async Task An_event_for_an_order_with_no_instance_is_discarded_in_silence()
    {
        // **Measured, because two review findings turn on it and neither the
        // chapter nor this file said which way it goes.** MassTransit's
        // policy for a NON-INITIAL event that correlates to no instance is
        // not the same thing as the unhandled-event path, which governs an
        // event reaching an instance in a state that does not handle it and
        // faults. This is the
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

    [Fact]
    public async Task A_publish_returns_only_after_the_saga_has_consumed_that_message()
    {
        // **The subject is the barrier, not the saga**, and this file needs one
        // because every other test here would stay green if the barrier were
        // removed — on this machine. They went green on the branch that shipped
        // twenty unfenced publishes, and CI failed on the merge commit.
        //
        // A test whose subject is what a gate is looking at is what this
        // repository owes every gate it has, and the reason is that a barrier
        // is only ever observed working.
        //
        // **CONSUMED, not "transitioned", and the name says so on purpose.**
        // The barrier's guarantee is that the saga has taken delivery, which
        // is the ordering property every caller needs; whether a transition
        // ran is the machine's business and is not always yes. The second
        // half of this test is the case that proves it — a StockReserved in
        // AwaitingPayment runs nothing at all. An earlier name here said
        // "the transition it triggers has run" and was falsified by the test's
        // own second stimulus.
        //
        // Read as of NOW, on a spent token: if
        // Publish returned before the saga had consumed OrderPlaced, the
        // command the Initially transition sends would not be recorded yet and
        // this is false.
        //
        // **Observed red both ways the helper can break, and the two reds are
        // not the same strength.** Against the helper as it was — no wait at
        // all — this first assertion fails deterministically, in under a
        // second. Against a wait on the message TYPE rather than its id it is
        // the SECOND assertion that fails, and that one is a race by
        // construction: the early-returning publish leaves the duplicate's
        // consume in flight, so the spent-token read below usually sees one
        // and may legitimately see two. It was observed red; it is not
        // guaranteed red, and the difference is written down rather than
        // rounded off, because a counterfactual nobody can re-run is not
        // evidence the next reader can check.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));

            harness.Sent
                .Select<ReserveStock>(Spent())
                .Count(m => m.Context.Message.OrderId == orderId)
                .ShouldBe(1);

            // And the id it fenced on is the transport's, which is the half a
            // type-level wait gets wrong. A second delivery of the same fact
            // carries its own id, so the fence cannot be satisfied by the
            // first — asserted here rather than trusted, because the tests
            // that depend on it would stay green if it were.
            StockReserved redelivered = SagaContracts.StockReserved(orderId);
            await Publish(harness, redelivered);
            await Publish(harness, redelivered);

            harness.Consumed
                .Select<StockReserved>(Spent())
                .Count(m => m.Context.Message.OrderId == orderId)
                .ShouldBe(2);

            // **The second delivery faults, and saying so is not decoration.**
            // It lands in AwaitingPayment, which declares no StockReserved
            // branch — the machine's default, asserted one test over by
            // A_redelivered_event_faults_rather_than_being_absorbed_silently.
            // Left unstated it would be a test quietly pushing a message onto
            // the error queue, which is the thing §13.6 pages on. Stated, it is
            // also the stronger claim: exactly one of the two consumes threw,
            // so the two are genuinely distinct deliveries that each reached
            // the state machine rather than one delivery counted twice.
            ConsumeFaults<StockReserved>(harness)
                .Count(e => e != null)
                .ShouldBe(1);
        }
    }
}
