using Common.Contracts;
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
    /// Publishes, and does not return until <em>that</em> message has been
    /// consumed — by whatever is bound to it, which is usually the saga and in
    /// the barrier's own guard is a consumer the test holds open. A fault
    /// counts as consumed: the harness records a delivery whether the pipeline
    /// returned or threw, so this is a claim about ordering and never about
    /// outcome. The wait is the point: without it two consecutive publishes are
    /// a race, and losing it fails a later assertion wearing the wrong
    /// component's name rather than the runner's.
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
    /// that had failed; fourteen of the twenty-seven harness tests this file
    /// held then still had twenty unfenced publishes between them, and the next
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
    /// because a count of them is a number nobody re-runs. The send context is
    /// what is read because it is the handle <em>both</em> kinds of message
    /// carry — a scheduled expiry has no envelope — and <b>not</b> because a
    /// contract has an id of its own there: this helper writes the envelope
    /// onto it, so for a contract the two are one value (§9.1).
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
            context =>
            {
                // §9.1: body, row, header and inbox key are ONE GUID, and
                // IIntegrationEvent says so in its own words — the envelope's
                // value is "THE message id, not a second one", and that
                // CorrelationId follows the same rule. Every publisher of a
                // contract in this repository copies both, OutboxDispatcher
                // included — though that was MADE true, twice, rather than
                // found so. The claim first went in as "every other
                // publisher"; a reviewer checked it and found three publishes
                // in IntegrationEventConsumerTests doing exactly this. It then
                // went in again covering only MessageId, and a reviewer
                // checked THAT and found seven contract publishers copying one
                // id of the two. Both sweeps are in this branch.
                // A harness that let MassTransit mint its own would
                // give every event two identities, which is the state that
                // comment calls easy to write and hard to see. It was written
                // here, and nothing failed — which is exactly the cost it
                // names.
                // **Both ids, because §9.1's rule is not only about
                // MessageId.** IIntegrationEvent says CorrelationId "follows
                // the same rule for the same reason", and OutboxDispatcher
                // copies both. Copying one was this fix's own half-measure:
                // the commit that removed a second identity left the other
                // one standing.
                if (message is IIntegrationEvent integrationEvent)
                {
                    context.MessageId = integrationEvent.MessageId;
                    context.CorrelationId = integrationEvent.CorrelationId;
                }

                // A scheduled expiry is not a contract (Appendix D) and has no
                // envelope, so the send context is what both kinds have. That
                // — not a second identity — is why the wait reads it.
                messageId = context.MessageId;
            },
            TestContext.Current.CancellationToken);

        // Unset, this degrades into the type-level wait the paragraph above
        // rejects — null == null matches the first consume of T and fences
        // nothing — so the barrier fails loudly rather than quietly weakening.
        messageId.ShouldNotBeNull();

        // The message names no consumer, and that is a correction rather than
        // a generalisation: it said "must reach the saga" while the barrier
        // also fences the gated probe, so a routing failure in that test
        // reported a saga it never registered.
        (await ConsumedWithId<T>(harness, messageId)).ShouldBeTrue(
            $"a published {typeof(T).Name} was never consumed, so this barrier cannot say the " +
            "next publish is ordered after it — an unfenced publish is a race the runner loses " +
            "under load, and it fails a later assertion wearing the wrong component's name.");
    }

    private static Task<bool> Sent<T>(ITestHarness harness, Func<T, bool> match)
        where T : class =>
        harness.Sent.Any<T>(m => match(m.Context.Message), TestContext.Current.CancellationToken);

    private static Task<bool> Consumed<T>(ITestHarness harness, Func<T, bool> match)
        where T : class =>
        harness.Consumed.Any<T>(m => match(m.Context.Message), TestContext.Current.CancellationToken);

    /// <summary>
    /// <see cref="Consumed"/> over the send context's message id, which is what
    /// <see cref="Publish"/> needs and no test does.
    /// </summary>
    /// <remarks>
    /// <b>For a contract this is the envelope's own value</b>, because
    /// <see cref="Publish"/> writes it there — §9.1's body, row, header and
    /// inbox key are one GUID, and nothing here is entitled to a second. The
    /// send context is read rather than the payload because the saga's five
    /// scheduled expiries are not contracts (Appendix D) and have no envelope;
    /// it is the one handle both kinds carry, not a different id.
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
        // what Inventory does with it is ADR-024's, and there is no Inventory
        // in this process to do it. A name claiming the reservation was
        // released would make a green test look like proof of a guarantee
        // nothing here can observe — which was #125's shape before the ADR
        // closed it, and is still true of what the harness sees.
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
    public async Task A_release_does_not_finalise_while_Payments_still_owes_a_verdict()
    {
        // **#124, and the interleaving is the ordinary one rather than the
        // exotic one.** The test above drives the authorisation before the
        // release; this drives them the other way round, which is what
        // happens whenever Inventory answers promptly and the PSP is slow —
        // the expected case, not the degenerate one.
        //
        // Under the unconditional Finalize this branch replaces, the release
        // ended the saga, SetCompletedWhenFinalized deleted the instance, and
        // the authorisation still in flight then correlated to nothing:
        // consumed cleanly, no review row, nothing on §13.6's pager. The two
        // outstanding results come from two services with §9.4 ordering
        // nothing between them, so neither order may be assumed.
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

            // Inventory answers first. The order is cancelled on this
            // transition — that command does not wait on Payments — but the
            // instance is held, because the authorisation can still land.
            await Publish(harness, SagaContracts.StockReleased(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.CustomerRequest))
                    .ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            // **The assertion the defect turned on.** Before this change the
            // instance was gone by here, and everything below was unreachable.
            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "auth-after-release"));

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.PaymentAuthorisedDuringCompensation))
                    .ShouldBeTrue();

            // And now both halves are settled, so the saga ends. Holding the
            // instance open is the mechanism, not the outcome — a saga that
            // never finalised would trade a silent loss for §13.6's
            // unfinalised-saga alert firing on every cancelled order.
            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_cancellation_with_no_authorisation_outstanding_still_finalises_on_the_release()
    {
        // The counterfactual for the test above, and the one that says the
        // join did not simply make every compensation hang. Cancelling in
        // AwaitingStock means AuthorisePayment was never sent, so nothing is
        // owed and the release ends the saga exactly as it did before #124 —
        // the conditional Finalize is a condition, not a delay.
        //
        // This is also the shape every pre-existing compensation test takes,
        // which is why they all stayed green: the join only changes the two
        // doors that arrive owing a verdict.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReleased(orderId));

            (await Sent<CancelOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_verdict_that_never_arrives_bounds_the_wait_and_escalates_nothing()
    {
        // **The bound, and the reason it raises no review row.** Holding the
        // instance for a verdict needs something that ends the hold when none
        // comes, or a slow PSP parks the saga for ever and §13.6's
        // unfinalised-saga alert pages instead.
        //
        // No FlagOrderForReview, and that is a decision rather than an
        // omission: §3.2 has Payments consuming OrderCancelled, so an
        // authorisation abandoned on a cancelled order is what SHOULD happen.
        // A row here would escalate the healthy path — one per cancelled
        // order the PSP correctly dropped. The escalation belongs where the
        // money actually moved, which the test above drives.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));
            await Publish(harness, SagaContracts.StockReleased(orderId));

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            // The wait armed when AuthorisePayment was sent, and deliberately
            // NOT unscheduled by the cancellation branch — this arrival is
            // what that absence is for.
            await Publish(harness, new PaymentAuthorisationExpired(orderId));

            (await saga.NotExists(orderId)).ShouldBeNull();

            // Read as of now, after a Publish that returned only once the saga
            // had consumed the message — so "not yet" has a point in time to
            // be false at.
            (await NotYetSent<FlagOrderForReview>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task The_payment_timeout_door_re_arms_its_own_wait_and_the_second_expiry_ends_it()
    {
        // **The door the test above does not cover, and the one where the
        // bound had to be built rather than inherited.** Cancelling in
        // AwaitingPayment leaves the original fifteen-minute wait armed, so
        // that door gets its bound for free. Reaching Compensating through
        // PaymentTimeout.Received does not: the wait it would have relied on
        // is the one that just fired.
        //
        // So that branch re-arms it, and this test is the reason the re-arm
        // cannot be quietly dropped — without it PaymentVerdictOutstanding
        // stays set with nothing left to clear it, and an order cancelled on
        // a slow PSP holds its instance until §13.6's unfinalised-saga alert
        // pages someone. A leak, in the one place the join could produce one.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));

            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            // The PSP goes quiet and the first wait ends. Compensation starts,
            // and the verdict stays outstanding because a timeout is not one.
            await Publish(harness, new PaymentAuthorisationExpired(orderId));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReleased(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.PaymentTimeout))
                    .ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            // The stock half is settled and the saga still will not end,
            // because this door arrived owing a verdict.
            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            // The SECOND expiry — the one the branch armed on its way in.
            await Publish(harness, new PaymentAuthorisationExpired(orderId));

            (await saga.NotExists(orderId)).ShouldBeNull();

            // And the bound escalates nothing, on this door as on the other.
            (await NotYetSent<FlagOrderForReview>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task A_decline_after_the_release_settles_the_join_without_escalating()
    {
        // A decline is an ANSWER, which is the half that changed. It still
        // raises nothing — no money moved, which is the outcome compensation
        // was heading for anyway — but it now discharges the obligation the
        // cancellation carried in, and the saga ends on it. While this was an
        // Ignore, a declined authorisation left the instance waiting out the
        // full payment window for a verdict that had already arrived.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));
            await Publish(harness, SagaContracts.StockReleased(orderId));

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            // **This line is what makes the test a guard rather than a
            // description**, and it was added after measuring: without it the
            // test passed against the unconditional Finalize too, because a
            // decline reaching no instance is discarded and "no instance, no
            // review row" reads identically from both sides. The instance has
            // to be observed alive at the moment the decline arrives for the
            // assertions below to be about this branch at all.
            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            await Publish(harness, SagaContracts.PaymentDeclined(orderId, "do_not_honour"));

            (await saga.NotExists(orderId)).ShouldBeNull();

            (await NotYetSent<FlagOrderForReview>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task A_release_timeout_holds_the_instance_while_a_verdict_is_outstanding()
    {
        // #124 names this exit as having the same hole, and it does: giving up
        // on the release settles the stock half exactly as StockReleased does,
        // so it asks the same question about the other one. "Settled" means
        // come to rest rather than succeeded — this branch escalates
        // stock_not_released and the instance stays for the verdict, so one
        // order can carry both rows.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));
            await Publish(harness, new StockReleaseExpired(orderId));

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.StockNotReleased))
                    .ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "auth-after-timeout"));

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.PaymentAuthorisedDuringCompensation))
                    .ShouldBeTrue();

            (await saga.NotExists(orderId)).ShouldBeNull();
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
        // than the defect: this order's CancelOrder went out of a branch that
        // finalises, so the OrderCancelled the aggregate then publishes
        // arrives at a queue whose instance has just been deleted. A missing
        // instance must be discarded rather than faulted, or every cancelled
        // order files an error-queue entry and pages someone (§13.6).
        //
        // **Not every cancellation the saga causes reaches a deleted
        // instance**, which is what this comment said: since #124
        // Compensating's stock exits finalise conditionally, so that echo can
        // land on a live instance and be absorbed there instead. This test
        // drives StockReservationFailed precisely because its branch does
        // finalise unconditionally.
        //
        // **What makes it the echo is CancelOrigins.Workflow, and #123 turned
        // that from a description into the condition.** The publish below
        // carried no origin until the field existed, and was discarded on the
        // strength of its type alone; it now has to say who caused it, and the
        // tests below assert that a cancellation saying anything else is not.
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

            OrderCancelled echo = SagaContracts.OrderCancelled(
                orderId,
                Customer,
                CancelReasons.OutOfStock,
                CancelOrigins.Workflow);
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
    public async Task A_release_that_overtakes_the_cancellation_is_absorbed_and_the_compensation_still_ends()
    {
        // #129, and the arrival order nothing covered. §3.2 has Inventory
        // consuming OrderCancelled DIRECTLY, so one publication starts two
        // races to this endpoint — the saga's own copy of the event, and the
        // StockReleased Inventory derives from it. Every other cancellation
        // test in this file delivers the event first; this one delivers the
        // release first, which is the half that used to fault.
        //
        // **The second publish is the assertion that matters, and it is
        // ADR-024 standing in for a service nobody has written.** Absorbing
        // the early release is safe only because the saga's own ReleaseStock
        // is answered whatever Inventory already did with the event. An
        // Inventory that answered the command only when a reservation was held
        // would leave this instance in Compensating until ReleaseTimeout and
        // file a stock_not_released review for stock that came back — so what
        // this test drives is the contract, not merely the branch.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.AwaitingStock)).ShouldNotBeNull();

            StockReleased overtaking = SagaContracts.StockReleased(orderId);
            await Publish(harness, overtaking);

            (await Consumed<StockReleased>(harness, m => m.MessageId == overtaking.MessageId))
                .ShouldBeTrue();

            // The claim the branch exists for. Before this change AwaitingStock
            // declared nothing for StockReleased, so the arrival raised
            // UnhandledEventException and spent §9.8's five retries — six
            // deliveries over about seventy seconds — hoping the cancellation
            // would land first.
            //
            // Consumed says only that it arrived: the harness records a
            // delivery whether the pipeline returned or threw, so every
            // assertion below would stay green through a fault. This is the
            // one that would not.
            ConsumeFaults<StockReleased>(harness).ShouldAllBe(e => e == null);

            // Absorbed rather than acted on — the wait is untouched.
            (await saga.Exists(orderId, x => x.AwaitingStock)).ShouldNotBeNull();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();
            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            // ADR-024's first guarantee, driven: Inventory answers the command
            // although it released on the event a moment ago, because
            // StockReleased reports the postcondition rather than a state
            // change. This is the publish that closes #129's objection to
            // absorbing the first one.
            await Publish(harness, SagaContracts.StockReleased(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.CustomerRequest))
                    .ShouldBeTrue();

            // Nothing escalated. The whole point of the ADR is that this
            // ordinary interleaving does not reach a human.
            (await NotYetSent<FlagOrderForReview>(harness, m => m.OrderId == orderId))
                .ShouldBeFalse();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_release_arriving_after_the_confirmation_is_absorbed_rather_than_faulted()
    {
        // **#129's fourth door, which #129 does not name and neither did the
        // machine.** The issue enumerates the three states whose
        // OrderCancelled branch sends a release; Confirmed is the state whose
        // branch deliberately sends none, and Inventory releases on the event
        // regardless (§3.2). So the release arrives here too.
        //
        // **And here the retry discards where elsewhere it rescues.** The
        // other three doors are races the retry envelope usually wins, because
        // a later attempt finds the instance moved to Compensating.
        // Confirmed's OrderCancelled branch finalises, so by the second
        // attempt there is no instance — and a non-initial event correlating
        // to none is consumed cleanly, which
        // An_event_for_an_order_with_no_instance_is_discarded_in_silence
        // pins. So the unwritten door loses the release quietly rather than
        // paging, and what this line buys is a clean FIRST delivery.
        //
        // Absorbing loses nothing: this state waits on no release, and the
        // cancellation that caused it raises cancelled_after_confirmation on
        // its own branch — the row an operator works both loose ends from.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "PSP-REF-129"));
            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull();

            StockReleased released = SagaContracts.StockReleased(orderId);
            await Publish(harness, released);

            (await Consumed<StockReleased>(harness, m => m.MessageId == released.MessageId))
                .ShouldBeTrue();

            ConsumeFaults<StockReleased>(harness).ShouldAllBe(e => e == null);

            // Nothing sent and nothing moved: a despatch is still expected and
            // this event is not evidence against it.
            (await NotYetSent<CancelOrder>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
            (await NotYetSent<FlagOrderForReview>(harness, m => m.OrderId == orderId))
                .ShouldBeFalse();

            (await saga.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull();
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
            // **`Publish` supplies that wait itself now, and this line is no
            // longer a second claim.** It was, while the helper let MassTransit
            // mint its own header: payload id and transport id were different
            // values, so only this line spoke about the payload. Since the
            // alignment they are one value (§9.1), and the barrier already
            // waited on it. Kept as the assertion that the *payload* carries
            // the duplicate's id — which is what the ConsumeFaults read below
            // is about to be scoped by — and NOT as a claim that it can tell a
            // second fact from a second delivery of the first. Nothing here
            // can: two deliveries of one message share an id by design, which
            // is §9.5's inbox's problem and the residual the barrier's own
            // test names.
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
            $"{nameof(saga.ReleaseTimeout)}.Received",

            // #124's addition, and it moved across this partition rather than
            // being new. It sat in the list below on the argument that "the
            // transitions that enter this state unschedule it" — true of four
            // doors and never of the fifth: cancelling from AwaitingPayment
            // arrives here with Payments still owing a verdict, and that
            // branch now deliberately leaves the wait armed so something
            // bounds how long the instance is held for one. The timeout door
            // re-arms it for the same reason. This is the exit that ends that
            // wait.
            $"{nameof(saga.PaymentTimeout)}.Received"
        ];

        // Not reachable in Compensating, and each for a stated reason rather
        // than by omission: OrderPlaced only creates an instance,
        // ShipmentDispatched and the despatch timeout belong to Confirmed,
        // and the stock and confirmation timeouts are unscheduled by the
        // transitions that enter this state.
        string[] notReachableHere =
        [
            nameof(saga.OrderPlaced),
            nameof(saga.ShipmentDispatched),
            $"{nameof(saga.StockTimeout)}.Received",
            $"{nameof(saga.ConfirmationTimeout)}.Received",
            $"{nameof(saga.DespatchTimeout)}.Received"
        ];

        string[] declared = DeclaredEvents();

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
    public void The_four_states_before_Compensating_write_out_what_they_accept()
    {
        // **The residual above, and it is now closed for every state rather
        // than for the two #126 created.** The partition test one method up
        // reads NextEvents(Compensating) and nothing else, so it demanded the
        // OrderConfirmed branch there and said nothing about the states that
        // actually carry the new event. Both were missed on the first pass: a
        // second OrderConfirmed in Confirmed faulted, and a ShipmentDispatched
        // beating the acknowledgement into AwaitingConfirmation faulted.
        //
        // **Leaving the other two out cost a third defect, which is why the
        // list grew.** #129 is one event — Inventory's StockReleased, derived
        // from an OrderCancelled this saga has not consumed yet — arriving in
        // FOUR states with no branch for it, and the issue itself names only
        // three: Confirmed was missed because nothing here was looking at it.
        // A gate that covers three of the four surfaces reports the fourth as
        // fine, which is this repository's most-repeated failure and was
        // sitting inside the test written to catch it.
        //
        // **Still not a generated sweep.** What makes a claim checkable is
        // naming the events a state can receive AND why, and that argument has
        // to be written per state.
        //
        // **What this method is NOT is a partition, and saying it was is how
        // this test came to overstate itself.** Compensating one method up
        // classifies every DECLARED event into reachable and not, so a new
        // event nobody thought about fails there. Accepts below compares
        // NextEvents against a written list and nothing else — so an event
        // declared with no branch in this state and no entry in this list
        // changes neither side and passes. That is the fail-open shape this
        // file exists to refuse, in the method written to refuse it.
        //
        // Per-state classification would close it and costs four
        // nine-element lists whose entries are mostly "belongs to another
        // state". Every_declared_event_is_handled_in_some_state below closes
        // the same hole from the other end and reads the machine for both
        // sides, so nothing has to be kept in step by hand.
        OrderFulfilmentSaga saga = new();

        // AwaitingStock is entered with ReserveStock in flight. Inventory
        // answers either way, the five-minute wait bounds it, and a
        // cancellation compensates. StockReleased is the fourth because
        // Inventory releases on OrderCancelled itself (§3.2), so it can beat
        // the saga's own copy of that event here.
        Accepts(
            saga,
            saga.AwaitingStock,
            [
                nameof(saga.StockReserved),
                nameof(saga.StockReservationFailed),
                nameof(saga.OrderCancelled),
                nameof(saga.StockReleased),
                $"{nameof(saga.StockTimeout)}.Received"
            ]);

        // AwaitingPayment is the same shape one step on: the PSP answers
        // either way, fifteen minutes bounds it, a cancellation compensates,
        // and the derived release can arrive before the cancellation that
        // caused it.
        Accepts(
            saga,
            saga.AwaitingPayment,
            [
                nameof(saga.PaymentAuthorised),
                nameof(saga.PaymentDeclined),
                nameof(saga.OrderCancelled),
                nameof(saga.StockReleased),
                $"{nameof(saga.PaymentTimeout)}.Received"
            ]);

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
                nameof(saga.StockReleased),
                $"{nameof(saga.ConfirmationTimeout)}.Received"
            ]);

        // Confirmed is entered BY OrderConfirmed, so a second one is a
        // duplicate — from §9.5's unrecorded redelivery, or from a rolling
        // deploy handing a new replica an instance an old one advanced. It is
        // absorbed rather than faulted, and the saga argues that at the line.
        //
        // **StockReleased is here for a different reason from the other three
        // states', and that difference is the fourth door.** Those send a
        // release and absorb the early copy of its answer; this one sends none
        // — a reservation being picked must not be dropped — so the arrival is
        // Inventory acting on the event alone, and nothing here is waiting on
        // it.
        Accepts(
            saga,
            saga.Confirmed,
            [
                nameof(saga.OrderConfirmed),
                nameof(saga.OrderCancelled),
                nameof(saga.ShipmentDispatched),
                nameof(saga.StockReleased),
                $"{nameof(saga.DespatchTimeout)}.Received"
            ]);
    }

    [Fact]
    public void Every_declared_event_is_handled_in_some_state()
    {
        // **The hole the two partition tests leave between them, closed
        // without a sixth hand-written list.** Compensating classifies every
        // declared event; the four states before it compare NextEvents to a
        // written list. So an event declared with no branch ANYWHERE and no
        // entry in any list is absent from both sides of all five assertions
        // and passes them all — which is exactly what a new Event<T> property
        // looks like on the day somebody adds one and forgets the transition.
        //
        // **Both sides of this one are read from the machine**, which is what
        // makes it hold without maintenance: the left is reflection over the
        // declared properties, the right is NextEvents over the declared
        // states. There is no list to forget to update, and no exemption list
        // either — every event this machine declares is legitimately
        // receivable somewhere, including OrderPlaced, which Initially
        // handles in Initial.
        //
        // It is deliberately weaker than a per-state partition and is not a
        // substitute for one: it says an event is handled SOMEWHERE, not that
        // it is handled everywhere it can arrive. That second claim is what
        // the per-state arguments above are for, and #129 is what it costs
        // when one of them is missing.
        OrderFulfilmentSaga saga = new();

        string[] declared = DeclaredEvents();
        declared.ShouldNotBeEmpty("the reflection scan found no events on this machine");

        string[] handled =
        [
            .. saga.States
                .SelectMany(saga.NextEvents)
                .Select(e => e.Name)
                .Where(n => !n.EndsWith(".AnyReceived", StringComparison.Ordinal))
                .Distinct()
        ];

        declared
            .Except(handled)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBeEmpty(
                "every event this machine declares must be receivable in at least one state. " +
                "One that is receivable nowhere is a binding on the saga's queue with no " +
                "transition behind it, which faults on every delivery and reaches the error " +
                "queue once §9.8's five retries are spent.");
    }

    /// <summary>
    /// Every event name the machine declares, including one per
    /// <see cref="Schedule{TInstance, TMessage}"/> in the <c>.Received</c> form
    /// <c>NextEvents</c> reports them under.
    /// </summary>
    /// <remarks>
    /// Extracted rather than copied: two tests classify against this set, and a
    /// second scan that drifted from the first would make one of them quietly
    /// narrower — the failure both of them exist to catch.
    /// </remarks>
    private static string[] DeclaredEvents() =>
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
        // is what left #123 silent. Pinning it here means the day a
        // MassTransit upgrade changes the default, this suite says so instead
        // of the residual quietly closing itself.
        //
        // **The subject used to be PaymentAuthorised and had to move**, which
        // is worth recording rather than quietly rewriting: that event now
        // configures OnMissingInstance(Fault) precisely because the default
        // was wrong for it (#124), so continuing to measure the default
        // through it would have measured the override instead. StockReleased
        // is the honest replacement — ADR-024 has Inventory publish it for
        // every release including a no-op one, so reaching a finalised
        // instance is its ORDINARY case rather than an anomaly, and the
        // default is what this machine wants for it.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orphan = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.StockReleased(orphan));

            (await Consumed<StockReleased>(harness, m => m.OrderId == orphan)).ShouldBeTrue();

            ConsumeFaults<StockReleased>(harness).ShouldAllBe(e => e == null);

            // And nothing was sent, which is the half that matters: no
            // transition ran, so the machine did not merely stay quiet about
            // the event, it never saw it.
            (await NotYetSent<CancelOrder>(harness, m => m.OrderId == orphan)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task An_authorisation_for_an_order_with_no_instance_faults_rather_than_vanishing()
    {
        // **The half of #124 the join above cannot reach, and the reason it
        // is a separate mechanism.** PaymentVerdictOutstanding keeps the
        // instance alive while a verdict can still arrive; this covers the
        // arrival that comes after the machine has stopped waiting — past the
        // bound, or for an order whose saga finalised down some other branch.
        //
        // The distinction that makes the override safe is provenance, not
        // timing: Payments produces PaymentAuthorised, so it can never be
        // Ordering's own echo of a command it sent. Every other event whose
        // missing instance is routine — OrderCancelled, StockReleased — is
        // either this service's echo or ADR-024's postcondition, and both
        // keep the silent default the test above pins.
        //
        // What is asserted is the FAULT, because that is what puts the
        // arrival on §13.6's pager. The money still moved; the change is that
        // somebody is told.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orphan = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.PaymentAuthorised(orphan, "auth-orphan"));

            (await Consumed<PaymentAuthorised>(harness, m => m.OrderId == orphan)).ShouldBeTrue();

            ConsumeFaults<PaymentAuthorised>(harness).ShouldContain(e => e != null);
        }
    }

    [Fact]
    public async Task A_cancellation_this_workflow_did_not_cause_faults_when_no_instance_exists()
    {
        // **#123.** A customer's cancellation that overtakes its own
        // OrderPlaced correlates to nothing, and was consumed cleanly — so the
        // placement that followed started a live saga for an order the
        // aggregate had already cancelled, reserving stock and asking for an
        // authorisation on it, with nothing anywhere saying so.
        //
        // Faulting is what spends §9.8's retry envelope, which is the whole
        // mechanism: five retries is about seventy seconds for the OrderPlaced
        // still in flight to land and create the instance. Only if it never
        // does is the message an error-queue entry, and then it should be.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orphan = Guid.CreateVersion7();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(
                    orphan,
                    Customer,
                    CancelReasons.CustomerRequest,
                    CancelOrigins.User));

            (await Consumed<OrderCancelled>(harness, m => m.OrderId == orphan)).ShouldBeTrue();

            ConsumeFaults<OrderCancelled>(harness).ShouldContain(e => e != null);
        }
    }

    [Fact]
    public async Task A_cancellation_carrying_this_workflows_own_reason_still_faults_if_it_did_not_cause_it()
    {
        // **The test that fails against the discriminator #123 nearly
        // shipped.** Reason looked like the answer — only a customer
        // cancellation carries customer_request, so the reasoning went — and
        // §11.4's endpoint parses all five CancelReasons codes, so a caller
        // may send out_of_stock. A Reason-based branch would discard this
        // message in silence, which is the defect wearing the fix's clothes.
        //
        // Origin is what is read, and it says User here whatever the reason
        // says.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orphan = Guid.CreateVersion7();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(
                    orphan,
                    Customer,
                    CancelReasons.OutOfStock,
                    CancelOrigins.User));

            (await Consumed<OrderCancelled>(harness, m => m.OrderId == orphan)).ShouldBeTrue();

            ConsumeFaults<OrderCancelled>(harness).ShouldContain(e => e != null);
        }
    }

    [Fact]
    public async Task A_cancellation_published_before_the_origin_field_existed_is_discarded()
    {
        // **§15.5's expand phase, pinned so it cannot be tidied away as an
        // oversight.** A rolling deploy has instances publishing this event
        // before they populate Origin, and faulting on absent would file an
        // error-queue entry for every ordinary cancellation for the length of
        // the deploy — a guaranteed incident, traded against #123's race being
        // open across it, which is bounded.
        //
        // **This is a tolerance with a contract phase owed**, not a reading of
        // absent as an origin. When no instance publishes without the field,
        // the branch goes and this test goes with it.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orphan = Guid.CreateVersion7();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orphan, Customer, CancelReasons.CustomerRequest, origin: null));

            (await Consumed<OrderCancelled>(harness, m => m.OrderId == orphan)).ShouldBeTrue();

            ConsumeFaults<OrderCancelled>(harness).ShouldAllBe(e => e == null);
        }
    }

    [Fact]
    public async Task A_reservation_reported_after_an_early_release_withholds_the_authorisation()
    {
        // **#143's money row.** A StockReleased in AwaitingStock proves a
        // cancellation reached Inventory — §3.2 has it consuming OrderCancelled
        // directly (ADR-029) — so the reservation reported after it is one that
        // has since been released. Authorising a card against it is the harm.
        //
        // Before this the release was absorbed with Ignore and the evidence
        // thrown away, so StockReserved took its ordinary transition and sent
        // AuthorisePayment for an order already being cancelled.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReleased(orderId));
            await Publish(harness, SagaContracts.StockReserved(orderId));

            (await NotYetSent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeFalse();

            // And the compensation still converges: the cancellation that
            // caused the release arrives, this state's own branch releases and
            // waits, and Inventory answers a release of nothing (ADR-024).
            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

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
    public async Task An_authorisation_after_an_early_release_escalates_rather_than_confirming()
    {
        // **#143's AwaitingPayment row.** Without the guard PaymentAuthorised
        // sends ConfirmOrder and moves to AwaitingConfirmation — confirming an
        // order the customer cancelled, and consuming the one arrival that
        // raises payment_authorised_during_compensation, because the success
        // branch and the escalation read the same event.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.StockReleased(orderId));
            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "auth-late"));

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.PaymentAuthorisedDuringCompensation))
                    .ShouldBeTrue();

            (await NotYetSent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task A_despatch_after_an_early_release_marks_the_order_shipped_and_escalates()
    {
        // **#143's sharpest row.** Confirmed's ShipmentDispatched finalises,
        // so a cancellation in flight then reaches a deleted instance: no
        // ReleaseStock, no review row, and — before #123 — no fault either.
        // Nothing anywhere recorded that the order was cancelled after
        // despatch.
        //
        // **MarkOrderShipped still goes, and the aggregate refuses it.** The
        // flag is set only by a StockReleased Inventory published off an
        // OrderCancelled staged in the transaction that cancelled the order
        // (ADR-029), so MarkOrderShippedHandler answers order.not_shippable
        // here. The assertion below is therefore that the COMMAND is sent —
        // §5.4 gives the aggregate the transition and this machine does not
        // predict its answer from a flag — and not that the order records a
        // despatch. Nothing in this suite could see the difference: the
        // harness has no aggregate behind the queue.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "auth-1"));
            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));
            await Publish(harness, SagaContracts.StockReleased(orderId));
            await Publish(harness, SagaContracts.ShipmentDispatched(orderId, "TRK-9"));

            (await Sent<MarkOrderShipped>(harness, m =>
                m.OrderId == orderId &&
                m.TrackingNumber == "TRK-9"))
                    .ShouldBeTrue();

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.CancelledAfterConfirmation))
                    .ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_despatch_beating_the_confirmation_after_an_early_release_escalates_too()
    {
        // The same interleaving one state earlier. #126 split AwaitingConfirmation
        // out of Confirmed and §3.2 gives Shipping OrderConfirmed too, so a
        // despatch can reach this saga before its own acknowledgement does —
        // and that branch finalises as well, losing the cancellation the same
        // way. Two doors, one argument, and the enumeration is what keeps the
        // second from being forgotten.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "auth-2"));
            await Publish(harness, SagaContracts.StockReleased(orderId));
            await Publish(harness, SagaContracts.ShipmentDispatched(orderId, "TRK-10"));

            (await Sent<MarkOrderShipped>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.CancelledAfterConfirmation))
                    .ShouldBeTrue();
        }
    }

    [Fact]
    public async Task A_publish_returns_only_after_the_saga_has_consumed_that_message()
    {
        // **The subject is the barrier, not the saga**, and this file needs one
        // because every PRE-EXISTING test here would stay green if the barrier
        // were removed — no longer every test, since the deterministic guard
        // below fails too, and that is the point of it — on this machine. They went green on the branch that shipped
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

            // **Two StockReserved facts, not one object published twice**, and
            // the difference is §9.1's. The fence reads the send context, and
            // for a contract Publish writes the envelope onto it — so two
            // separate SagaContracts calls are two envelope ids and two
            // deliveries the fence can tell apart. Publishing one object twice
            // would put ONE id on the wire twice, which is what §9.5's inbox
            // exists to dedupe and what this barrier therefore cannot separate.
            //
            // That residual is real and named rather than papered over: the
            // barrier distinguishes deliveries of DIFFERENT messages, never two
            // deliveries of the same one. This test used to be the second case,
            // which only worked because the helper was minting a second
            // identity §9.1 forbids.
            //
            // A type-level wait fails here, on the count below rather than on
            // the one above: it is satisfied by the first delivery and returns
            // with the second still in flight.
            StockReserved first = SagaContracts.StockReserved(orderId);
            StockReserved second = SagaContracts.StockReserved(orderId);
            await Publish(harness, first);
            await Publish(harness, second);

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

    /// <summary>
    /// A consumer that does not return until the test lets it, so "did
    /// <see cref="Publish"/> wait?" has an answer that does not depend on
    /// timing.
    /// </summary>
    /// <remarks>
    /// <b>The saga cannot be this consumer.</b> Its transitions return
    /// immediately, so every question about the barrier asked through it is
    /// answered by whichever of two fast operations happened to finish first —
    /// which is how the type-level counterfactual next door ended up a race
    /// that was observed red rather than one that is red. A consumer the test
    /// holds open removes the timing from the question entirely.
    /// </remarks>
    private sealed class GateConsumer : IConsumer<GateProbe>
    {
        internal static TaskCompletionSource Arrived { get; private set; } = new();

        internal static TaskCompletionSource Release { get; private set; } = new();

        internal static void Reset()
        {
            Arrived = new TaskCompletionSource();
            Release = new TaskCompletionSource();
        }

        public async Task Consume(ConsumeContext<GateProbe> context)
        {
            Arrived.TrySetResult();
            await Release.Task;
        }
    }

    /// <summary>
    /// Not an <c>IIntegrationEvent</c>, deliberately: it never crosses a
    /// service boundary, so §4.3 and §9.1 have nothing to say about it, and
    /// giving it an envelope would only add a second thing to keep true.
    /// </summary>
    private sealed record GateProbe(Guid Id);

    [Fact]
    public async Task A_publish_does_not_return_while_its_own_message_is_still_being_consumed()
    {
        // **The deterministic half of the barrier's guarantee**, and it exists
        // because the test above cannot supply it. That one drives the saga,
        // whose transitions return at once, so it can only observe the barrier
        // through a race it has to describe honestly — a type-level wait fails
        // it *usually*. Copilot raised that on this branch and was right: a
        // regression guard that usually fails is the fail-open shape this whole
        // change exists to close, wearing a test's clothes.
        //
        // Here the consumer is held open by the test, so both halves are
        // settled by construction rather than by which operation won:
        //
        //   1. Publish must not return while ITS message is unconsumed.
        //   2. It must not be satisfied by a DIFFERENT message of the same
        //      type — which is exactly what a type-level wait does.
        GateConsumer.Reset();

        ServiceProvider provider = new ServiceCollection()
            .AddMassTransitTestHarness(x =>
            {
                x.SetTestTimeouts(TestTimeout, InactivityTimeout);
                x.AddConsumer<GateConsumer>();
                x.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));
            })
            .BuildServiceProvider(true);

        await using (provider)
        {
            ITestHarness harness = provider.GetRequiredService<ITestHarness>();
            await harness.Start();

            // **The release goes in a finally, and that is not tidiness.**
            // Written without one, a FAILING assertion leaves the consumer
            // blocked for ever: the harness never drains, disposal never
            // returns, and the run hangs instead of going red. Measured that
            // way round — the first counterfactual of this test deadlocked a
            // ten-minute runner rather than reporting — which is a worse
            // failure than the race it was written to remove, since a hang on
            // CI burns the job's whole budget and names nothing.
            Task publish = Publish(harness, new GateProbe(Guid.CreateVersion7()));
            try
            {
                // The consumer has the message and is holding it. Nothing here
                // waits on a clock: the barrier is either open or it is not.
                //
                // **Bounded, for the reason the finally below exists.** If the
                // publish faulted or the probe never routed, an unbounded await
                // here hangs the run rather than failing it — the same hang the
                // finally was added to prevent, one step earlier and reached by
                // a different fault. WaitAsync turns it into a timeout that
                // names the wait.
                await GateConsumer.Arrived.Task
                    .WaitAsync(InactivityTimeout, TestContext.Current.CancellationToken);
                publish.IsCompleted.ShouldBeFalse(
                    "Publish returned while its own message was still inside the consumer, " +
                    "so it is not a barrier at all.");
            }
            finally
            {
                GateConsumer.Release.TrySetResult();
            }

            await publish;

            // And now the second half. The first probe is consumed, so a
            // type-level wait is already satisfied and would return at once;
            // an id-level wait cannot be, because this message has not been
            // delivered yet. Same construction, no timing.
            GateConsumer.Reset();

            Task second = Publish(harness, new GateProbe(Guid.CreateVersion7()));
            try
            {
                await GateConsumer.Arrived.Task
                    .WaitAsync(InactivityTimeout, TestContext.Current.CancellationToken);
                second.IsCompleted.ShouldBeFalse(
                    "Publish returned once SOME message of the type had been consumed rather " +
                    "than its own — which is the type-level wait, and it fences nothing.");
            }
            finally
            {
                GateConsumer.Release.TrySetResult();
            }

            await second;
        }
    }
}
