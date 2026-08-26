using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Infrastructure.Inbox;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Infrastructure.Messaging;
using Ordering.TestSupport;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>
/// The saga against real SQL Server and a real broker: the EF repository §9.6
/// registers, the row it writes, and what happens to a message that arrives
/// after the instance is gone.
/// </summary>
/// <remarks>
/// <b>§12.5's harness cannot reach any of this.</b> That suite swaps in
/// <c>.InMemoryRepository()</c> and the in-memory transport, so the EF mapping,
/// the pessimistic locking, the persistence across a transition and the
/// delete-on-finalise are all replaced by the thing under test's double.
/// Copilot named the gap; this is the other half, and it is deliberately thin —
/// the transitions themselves are the harness suite's subject and are not
/// re-proved over a broker.
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class OrderFulfilmentSagaEndpointTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly Guid Customer = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Every message this test published, so the teardown can wait for the
    /// saga endpoint's inbox row before the next test truncates.
    /// </summary>
    private readonly List<(Guid MessageId, string Endpoint)> _published = [];

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    /// <summary>
    /// Drains what this test started.
    /// </summary>
    /// <remarks>
    /// <b>This was a no-op, and it was the same flake the suite next door
    /// exists to document.</b> The assertions here observe the saga
    /// repository's commit — a row appearing or disappearing — which happens
    /// <em>inside</em> the consumer; §9.5's filter writes its inbox row after
    /// control returns to it. So a test could see the row go, return, and let
    /// the next <c>ResetAsync</c> truncate the schema underneath a
    /// <c>SaveChangesAsync</c> still in flight. Copilot caught it in the round
    /// after the one that added the filter — the filter is what created the
    /// second write to wait for.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        foreach ((Guid messageId, string endpoint) in _published)
        {
            await Eventually(
                async () => (await InboxRowsAsync(messageId, endpoint)).Count,
                expected: 1,
                because: $"a delivery still running when the next test resets is a flake in that test " +
                    $"— {endpoint}, for message {messageId}");
        }
    }

    [Fact]
    public async Task The_instance_is_persisted_across_a_transition_and_deleted_on_finalise()
    {
        var orderId = Guid.CreateVersion7();

        await PublishPlacedAsync(orderId, Guid.CreateVersion7());

        await Eventually(
            () => SagaRowsAsync(orderId),
            expected: 1,
            because: "AddSagaStateMachine's EntityFrameworkRepository is what writes this row, and the " +
                "harness suite replaces it with an in-memory double");

        (await fixture.ScalarAsync<string>(
            "SELECT Value = CurrentState FROM ordering.OrderFulfilmentStates WHERE CorrelationId = {0}",
            orderId))
            .ShouldBe("AwaitingStock", "the state column is the mapping under test, not the transition");

        // A failure that finalises immediately, so the delete is observable
        // without waiting out a timeout.
        await PublishReservationFailedAsync(orderId, Guid.CreateVersion7());

        await Eventually(
            () => SagaRowsAsync(orderId),
            expected: 0,
            because: "SetCompletedWhenFinalized deletes the instance, which is why §9.6's diagram has no " +
                "Cancelled state — and nothing else in the suite watches that it really does");
    }

    [Fact]
    public async Task A_cancellation_that_overtakes_its_placement_is_rescued_by_the_retry()
    {
        // **#123's whole argument for faulting rather than discarding, and
        // §12.5's suite cannot express it.** That harness registers no
        // UseMessageRetry, so its tests prove the callback faults ONCE and
        // nothing more — while the reason a fault is the right answer is that
        // §9.8's envelope gives the placement time to land and a later attempt
        // correlates. A fault that never recovers would be a regression this
        // branch argued for in prose and never measured. Copilot found the gap.
        //
        // **What this proves, and what it cannot.** It establishes that a
        // cancellation published before its placement still ends in
        // Compensating rather than in the error queue — the recovery §9.8's
        // envelope is supposed to give #123, which §12.5's harness cannot
        // express because it registers no UseMessageRetry.
        //
        // **The delay is the strongest arrangement available and is not a
        // proof.** Publish returns at the transport boundary rather than when a
        // consumer has run, so on a loaded runner the cancellation can still be
        // queued when the clock expires; the placement then wins, the first
        // delivery correlates, and this passes having exercised the ordinary
        // path. It never fails spuriously — it occasionally proves less.
        // Copilot named that, and the fence it asked for is **not observable
        // here**: UseMessageRetry wraps the pipeline, so retries run inside it
        // and no fault reaches an IReceiveObserver or IConsumeObserver until
        // the ladder is exhausted — about seventy seconds, which is this
        // repository's own figure for it and well past this suite's budget.
        // Measured, by writing both observers and watching them stay silent for
        // the whole wait.
        //
        // **No per-attempt arithmetic here, deliberately.** An earlier revision
        // wrote the ladder out as 1s, 3s, 7s, 15s, 31s and priced it at
        // fifty-seven — both a contradiction of the seventy this corpus states
        // everywhere else and a determinism the policy does not have. The only
        // property this test needs is that the shortest ladder still outlasts
        // thirty seconds.
        //
        // Closing it needs a signal the platform does not have — a first-attempt
        // hook, or a shorter ladder on a test-only endpoint. Stated rather than
        // papered over, because the alternative is a comment claiming a
        // determinism the code does not deliver.
        var orderId = Guid.CreateVersion7();

        await PublishCancelledAsync(orderId, Guid.CreateVersion7(), CancelOrigins.User);

        await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);

        (await SagaRowsAsync(orderId)).ShouldBe(
            0,
            "nothing has created an instance yet, so the delivery that has " +
                "already run found none");

        await PublishPlacedAsync(orderId, Guid.CreateVersion7());

        // The row has to exist before its state can be read at all — ScalarAsync
        // throws on an empty result rather than answering, so polling the state
        // directly would die on the first read instead of waiting.
        await Eventually(
            () => SagaRowsAsync(orderId),
            expected: 1,
            because: "the placement creates the instance the retry needs");

        await Eventually(
            () => fixture.ScalarAsync<string>(
                "SELECT Value = CurrentState FROM ordering.OrderFulfilmentStates " +
                "WHERE CorrelationId = {0}",
                orderId),
            expected: "Compensating",
            because: "a retried delivery has to correlate to the instance the " +
                "placement created and compensate; reaching the error queue " +
                "instead is the outcome #123 chose faulting to avoid");
    }

    [Fact]
    public async Task An_observed_cancellation_is_persisted_and_withholds_the_authorisation()
    {
        // **#143's flag is written by one delivery and read by a later one, and
        // §12.5's suite cannot see that.** Every scenario there runs on
        // .InMemoryRepository(), so the column, its mapping and the read-back
        // across two consume transactions are all replaced by a double — and a
        // guard that reads a field EF never persisted would pass there and fail
        // in production. Copilot named the gap; this is the half that closes it.
        var orderId = Guid.CreateVersion7();

        await PublishPlacedAsync(orderId, Guid.CreateVersion7());

        await Eventually(
            () => SagaRowsAsync(orderId),
            expected: 1,
            because: "the arrange half");

        // Inventory releasing off an OrderCancelled this saga has not consumed
        // (ADR-029) — the arrival AwaitingStock records rather than ignores.
        await PublishReleasedAsync(orderId, Guid.CreateVersion7());

        await Eventually(
            () => fixture.ScalarAsync<int>(
                "SELECT Value = CAST(CancellationObserved AS int) " +
                "FROM ordering.OrderFulfilmentStates WHERE CorrelationId = {0}",
                orderId),
            expected: 1,
            because: "the recording branch has to reach the COLUMN, which is the " +
                "half a saga harness with an in-memory repository cannot prove");

        // The forward event the guard exists for. Read back on a later delivery,
        // so this asserts the round trip rather than the assignment.
        var reservedId = Guid.CreateVersion7();
        await PublishReservedAsync(orderId, reservedId);

        // **Fence on the delivery before reading the state, or this assertion
        // cannot fail.** AwaitingStock is what the row already says when
        // StockReserved is published, so an Eventually that merely waits for it
        // returns on its first read — before the saga has consumed anything — and
        // passes just as happily with the guard removed. Measured: it did.
        // §9.5's inbox row is written after the consumer returns, so waiting for
        // it is the barrier that makes the read mean something.
        await Eventually(
            async () => (await SagaInboxRowsAsync(reservedId)).Count,
            expected: 1,
            because: "the state below is only evidence once this delivery has been " +
                "handled");

        (await fixture.ScalarAsync<string>(
            "SELECT Value = CurrentState FROM ordering.OrderFulfilmentStates " +
            "WHERE CorrelationId = {0}",
            orderId))
            .ShouldBe(
                "AwaitingStock",
                "an unguarded StockReserved sends AuthorisePayment and moves to " +
                "AwaitingPayment; the state column is where withholding is visible " +
                "without a harness to count sends with");
    }

    [Fact]
    public async Task A_row_this_build_writes_defaults_the_retained_CustomerId_to_empty()
    {
        // The behavioural half of ADR-028's expand/contract, and until this
        // test it was an argument rather than a measurement. The instance no
        // longer declares CustomerId, so the generated INSERT does not name
        // that column; what supplies it is the database default this branch's
        // migration adds. Nothing else in the suite watches that — the smoke
        // test checks the migration was applied, which is not the same claim.
        //
        // **What rests on the value being Guid.Empty is the mixed-version
        // window.** §15.5 runs two releases at once over the same queues, so a
        // pod on the previous build can step an instance this one created,
        // materialise this column into its non-nullable Guid, and send its
        // four-field AuthorisePayment. Empty means it names nobody; any other
        // value would name a real customer who never placed this order.
        var orderId = Guid.CreateVersion7();

        await PublishPlacedAsync(orderId, Guid.CreateVersion7());

        await Eventually(
            () => SagaRowsAsync(orderId),
            expected: 1,
            because: "the row has to exist before its column values mean anything");

        (await fixture.ScalarAsync<Guid>(
            "SELECT Value = CustomerId FROM ordering.OrderFulfilmentStates WHERE CorrelationId = {0}",
            orderId))
            .ShouldBe(
                Guid.Empty,
                "the instance does not write this column, so the database default is the only thing " +
                "that can have supplied it — and empty is what makes an old build's read name nobody");
    }

    [Fact]
    public async Task The_retained_CustomerId_column_carries_a_default_constraint()
    {
        // The mechanism behind the test above, asserted separately because the
        // two can fail apart. A row could read empty because something wrote
        // Guid.Empty into it; the constraint is what makes the value a
        // property of the schema rather than of one insert path.
        (await fixture.ScalarAsync<int>(
            """
            SELECT Value = COUNT(*)
            FROM sys.default_constraints d
            INNER JOIN sys.columns c
                ON c.object_id = d.parent_object_id
                AND c.column_id = d.parent_column_id
            WHERE d.parent_object_id = OBJECT_ID('ordering.OrderFulfilmentStates')
                AND c.name = 'CustomerId'
            """))
            .ShouldBe(
                1,
                "§15.5 forbids dropping a column the previous release still writes, so the column " +
                "survives this release with a default that lets this build's INSERT omit it");
    }

    [Fact]
    public async Task A_replayed_OrderPlaced_does_not_restart_a_finished_saga()
    {
        // **The one that made the saga endpoint take an inbox filter.**
        //
        // OrderPlaced is handled in Initially and SetCompletedWhenFinalized
        // deletes the row, so MassTransit's initial-event policy creates a NEW
        // instance whenever none exists. §9.4 guarantees at-least-once — a
        // crash between publishing and marking the outbox row processed
        // republishes it — so a duplicate arriving after the workflow finished
        // would reserve stock and authorise payment a second time.
        //
        // §9.8's exemption ("a redelivered StockReserved finds the instance
        // already past AwaitingStock") is an argument about NON-initial events
        // and never covered this one. Copilot found it.
        var orderId = Guid.CreateVersion7();
        var messageId = Guid.CreateVersion7();

        await PublishPlacedAsync(orderId, messageId);
        await Eventually(() => SagaRowsAsync(orderId), expected: 1, because: "the arrange half");

        await PublishReservationFailedAsync(orderId, Guid.CreateVersion7());
        await Eventually(() => SagaRowsAsync(orderId), expected: 0, because: "the saga must finish first");

        // The same message, again — which is what the outbox does after a crash.
        await PublishPlacedAsync(orderId, messageId);

        // Then a sentinel for an unrelated order, and the wait is on its row
        // rather than on a clock. **Copilot's finding, and it named the second
        // half too:** _published holds each id once, so the teardown drain was
        // already satisfied by the *first* delivery of this id and never waited
        // for the replay — the exact shape the drain was added to close, one
        // level down. The sentinel is a fresh id, so it is drained on its own.
        var sentinelOrderId = Guid.CreateVersion7();
        await PublishPlacedAsync(sentinelOrderId, Guid.CreateVersion7());
        await Eventually(
            () => SagaRowsAsync(sentinelOrderId),
            expected: 1,
            because: "a message published after the replay has been consumed, so the endpoint has had " +
                "the replay in front of it — five seconds was generous on this machine and a guess " +
                "about every other one");

        (await SagaRowsAsync(orderId)).ShouldBe(
            0,
            "a replayed OrderPlaced must not start fulfilment again — a second ReserveStock and a second " +
            "AuthorisePayment for one order is a double charge, and the row is the observable half of it");

        // **A bound, not a proof of ordering**, on the terms
        // OrderingCommandEndpointTests states in full: nothing pins this
        // endpoint to one message at a time, so the sentinel may overtake. What
        // it replaces is a fixed delay that could not scale with the runner at
        // all, and unlike that delay it fails the test rather than passing it
        // when the broker is the thing that stalled.
    }

    [Fact]
    public async Task A_scheduled_expiry_survives_the_endpoints_inbox_filter()
    {
        // The filter throws on a message with no MessageId, and the saga
        // endpoint now carries one — so every message type that reaches it has
        // to be checked, not just the contracts. The four expiry records are
        // published by the scheduler rather than by a mapper, which is the
        // path least like the others.
        //
        // Published directly rather than waited for: what is under test is that
        // the type crosses the filter, not MassTransit's timer.
        var orderId = Guid.CreateVersion7();

        await PublishPlacedAsync(orderId, Guid.CreateVersion7());
        await Eventually(() => SagaRowsAsync(orderId), expected: 1, because: "the arrange half");

        await PublishExpiredAsync(orderId, Guid.CreateVersion7());

        await Eventually(
            () => SagaRowsAsync(orderId),
            expected: 0,
            because: "the stock timeout cancels the order and finalises — if the filter had rejected the " +
                "message for want of a MessageId, the row would still be there");
    }

    [Fact]
    public async Task Two_events_for_one_instance_arriving_together_are_both_consumed()
    {
        // Copilot's finding was that ConcurrencyMode.Pessimistic is justified
        // by a race no test runs, and every other test here delivers one event
        // at a time. This publishes two without awaiting between them, so both
        // are on the queue before either is consumed.
        //
        // **It does not pin the mode, and that was measured rather than
        // assumed.** With the registration flipped to Optimistic this passes
        // in 915 ms — the two transitions are a few milliseconds each, so the
        // endpoint drains them back to back and no concurrency conflict ever
        // arises. Writing the name Two_events_..._are_serialised over that
        // would be this round's own finding committed a second time: a test
        // green against both sides of the thing it claims to check.
        //
        // What it does cover is real and was uncovered before: two events for
        // one instance, in flight together, are both consumed without
        // faulting and leave one instance or none. The residual — a genuine
        // overlap, which needs a transition slow enough to hold the lock — is
        // recorded in the decision log rather than papered over here.
        var orderId = Guid.CreateVersion7();

        await PublishPlacedAsync(orderId, Guid.CreateVersion7());
        await Eventually(() => SagaRowsAsync(orderId), expected: 1, because: "the arrange half");

        Guid reservedId = Guid.CreateVersion7();
        Guid expiredId = Guid.CreateVersion7();

        await Task.WhenAll(
            PublishReservedAsync(orderId, reservedId),
            PublishExpiredAsync(orderId, expiredId));

        // InboxFilter records the id *after* the consumer returns — its own
        // summary says so — so a consume that faults writes no row at all.
        // That makes this the assertion with teeth: whatever order the two
        // transitions run in, neither may throw.
        await Eventually(
            async () => (await SagaInboxRowsAsync(reservedId)).Count + (await SagaInboxRowsAsync(expiredId)).Count,
            expected: 2,
            because: "a row is written only once its consumer returns, so a transition that faulted on " +
                "the instance the other one changed leaves this at one");

        // Stated for what it rules out, which is less than it looks: the
        // primary key on CorrelationId already forbids a second row. It is
        // here because two instances is the failure a reader expects the mode
        // to be about, and leaving it unasserted invites the next reader to
        // add it as though it were the missing coverage.
        (await SagaRowsAsync(orderId)).ShouldBeLessThanOrEqualTo(
            1,
            "one instance or none — never two for one CorrelationId");
    }

    [Fact]
    public async Task The_sagas_sends_are_committed_with_its_instance_rather_than_buffered()
    {
        // **#128 and ADR-032, and the only place in the solution where the
        // mechanism is observable at all.** The saga used to send through
        // UseInMemoryOutbox, which buffers in the process and flushes AFTER
        // EntityFrameworkRepository has committed the instance. A crash in that
        // window left the order advanced with its commands never sent — and for
        // OrderPlaced that is the worst shape available, because the
        // StockReservationExpired schedule that would have rescued the order
        // was in the same buffer as the ReserveStock that failed to go.
        //
        // **What this asserts is not "a message arrived".** Every other test in
        // this class already proves delivery, and every one of them passed
        // before this change too — delivery is what the in-memory outbox also
        // does, right up until the process dies. The observable difference is
        // WHERE the messages were between the commit and the send, and
        // MassTransit records exactly that: the endpoint's filter writes an
        // ordering.InboxState row inside the saga's own transaction, stages the
        // sends in ordering.OutboxMessage beside it, and stamps
        // LastSequenceNumber on that row once it has delivered them.
        //
        // So LastSequenceNumber IS NOT NULL is the assertion with teeth. It is
        // false in three distinguishable ways — the filter is not on the
        // endpoint, the filter is there but the transition sent nothing, or the
        // rows exist and delivery never ran — and the first of those is the
        // regression #128 is about.
        var orderId = Guid.CreateVersion7();
        Guid placedId = Guid.CreateVersion7();

        await PublishPlacedAsync(orderId, placedId);

        await Eventually(
            () => SagaRowsAsync(orderId),
            expected: 1,
            because: "the arrange half — nothing below can be true before the transition has run");

        // Anti-vacuity, and it is not decoration: the two assertions differ by
        // one predicate, so without this one a WHERE that matched nothing at
        // all would read exactly like a delivery that had not happened yet, and
        // this test would fail for the wrong reason for thirty seconds.
        await Eventually(
            () => TransactionalInboxRowsAsync(placedId, deliveredOnly: false),
            expected: 1,
            because: "UseEntityFrameworkOutbox writes this row inside the saga's transaction; with the " +
                "in-memory outbox on the endpoint instead, ordering.InboxState is never written at all");

        await Eventually(
            () => TransactionalInboxRowsAsync(placedId, deliveredOnly: true),
            expected: 1,
            because: "LastSequenceNumber is stamped once the messages staged in ordering.OutboxMessage " +
                "have been delivered — which is the proof they were staged there and not in a " +
                "process-local buffer that a crash would have discarded (#128, ADR-032)");
    }

    /// <summary>
    /// Rows in MassTransit's own inbox for one message —
    /// <c>ordering.InboxState</c>, which is ADR-032's table and not §9.5's
    /// <c>ordering.InboxMessages</c>. The two are different mechanisms with
    /// different windows and both are on the saga endpoint; reading the wrong
    /// one is the obvious way to write a test that proves nothing.
    /// </summary>
    /// <param name="deliveredOnly">
    /// Narrows to rows whose staged messages have been sent. The saga's
    /// endpoint is the only one that stages any, so on this endpoint the
    /// narrowed count is the mechanism and the wide one is only its record.
    /// </param>
    private Task<int> TransactionalInboxRowsAsync(Guid messageId, bool deliveredOnly) =>
        fixture.ScalarAsync<int>(
            deliveredOnly
                ? "SELECT Value = COUNT(*) FROM ordering.InboxState " +
                    "WHERE MessageId = {0} AND Consumed IS NOT NULL AND LastSequenceNumber IS NOT NULL"
                : "SELECT Value = COUNT(*) FROM ordering.InboxState WHERE MessageId = {0}",
            messageId);

    /// <summary>
    /// One message's inbox rows on one endpoint — the inbox key is the pair,
    /// so a <c>StockReserved</c> leaves one row on the saga's queue and
    /// another on <c>ordering-stock-events</c>.
    /// </summary>
    /// <remarks>
    /// <b>This took an endpoint after Copilot found the teardown draining
    /// half of one.</b> `StockReserved` has two consumers by design — the saga
    /// correlates on it and `StockReservedHandler` records it on the aggregate
    /// — so the pair-scoped helper that made the concurrency test possible also
    /// let its publisher register a drain for the saga alone. The saga could
    /// then finish, the teardown pass, and the next `ResetAsync` truncate the
    /// schema underneath a `StockReservedHandler` still committing. That is
    /// exactly the flake this class's teardown was added to close, one
    /// endpoint over.
    /// </remarks>
    private async Task<IReadOnlyList<InboxMessage>> InboxRowsAsync(Guid messageId, string endpoint) =>
        [.. (await fixture.InboxAsync()).Where(r => r.MessageId == messageId && r.Endpoint == endpoint)];

    private Task<IReadOnlyList<InboxMessage>> SagaInboxRowsAsync(Guid messageId) =>
        InboxRowsAsync(messageId, DependencyInjection.FulfilmentSagaQueue);

    private Task<int> SagaRowsAsync(Guid orderId) =>
        fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM ordering.OrderFulfilmentStates WHERE CorrelationId = {0}",
            orderId);

    /// <summary>
    /// A scheduled expiry, published with an id of this test's choosing so the
    /// teardown can wait for it. The scheduler assigns its own in production;
    /// what is under test is that the type crosses the endpoint's filter.
    /// </summary>
    private async Task PublishExpiredAsync(Guid orderId, Guid messageId)
    {
        // The saga alone: nothing else binds a timeout.
        _published.Add((messageId, DependencyInjection.FulfilmentSagaQueue));

        await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .Publish(
                new StockReservationExpired(orderId),
                c => c.MessageId = messageId,
                TestContext.Current.CancellationToken);
    }

    private async Task PublishPlacedAsync(Guid orderId, Guid messageId)
    {
        OrderPlaced placed = new()
        {
            MessageId = messageId,
            CorrelationId = orderId,
            OccurredAt = DateTimeOffset.UtcNow,
            OrderId = orderId,
            CustomerId = Customer,
            TotalAmount = 19.99m,
            Currency = "EUR",
            Lines = [new PlacedLine(Guid.CreateVersion7(), 1, 19.99m)]
        };

        // Added once even when the same id is published twice: the replay is
        // suppressed by the filter, so it writes no second row.
        if (!_published.Contains((messageId, DependencyInjection.FulfilmentSagaQueue)))
            _published.Add((messageId, DependencyInjection.FulfilmentSagaQueue));

        await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .Publish(
                placed,
                c =>
                {
                    c.MessageId = messageId;
                    c.CorrelationId = placed.CorrelationId;
                },
                TestContext.Current.CancellationToken);
    }

    private async Task PublishCancelledAsync(Guid orderId, Guid messageId, string origin)
    {
        OrderCancelled cancelled = new()
        {
            MessageId = messageId,
            CorrelationId = orderId,
            OccurredAt = DateTimeOffset.UtcNow,
            OrderId = orderId,
            CustomerId = Customer,
            Reason = CancelReasons.CustomerRequest,
            Origin = origin
        };

        _published.Add((messageId, DependencyInjection.FulfilmentSagaQueue));

        await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .Publish(
                cancelled,
                c =>
                {
                    c.MessageId = messageId;
                    c.CorrelationId = cancelled.CorrelationId;
                },
                TestContext.Current.CancellationToken);
    }

    private async Task PublishReleasedAsync(Guid orderId, Guid messageId)
    {
        StockReleased released = new()
        {
            MessageId = messageId,
            CorrelationId = orderId,
            OccurredAt = DateTimeOffset.UtcNow,
            OrderId = orderId
        };

        // One consumer in this service, unlike StockReserved above: §3.2 gives
        // StockReleased to the saga alone, so the saga queue is the whole
        // drain list.
        _published.Add((messageId, DependencyInjection.FulfilmentSagaQueue));

        await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .Publish(
                released,
                c =>
                {
                    c.MessageId = messageId;
                    c.CorrelationId = released.CorrelationId;
                },
                TestContext.Current.CancellationToken);
    }

    private async Task PublishReservedAsync(Guid orderId, Guid messageId)
    {
        StockReserved reserved = new()
        {
            MessageId = messageId,
            CorrelationId = orderId,
            OccurredAt = DateTimeOffset.UtcNow,
            OrderId = orderId
        };

        // **Both, and this is the pair the teardown was missing.** §3.2 gives
        // StockReserved two consumers in this service — the saga correlates on
        // it, StockReservedHandler records it on the aggregate — so a drain
        // that waits for the saga's row alone lets the next ResetAsync
        // truncate under the other one.
        _published.Add((messageId, DependencyInjection.FulfilmentSagaQueue));
        _published.Add((messageId, DependencyInjection.StockEventsQueue));

        await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .Publish(
                reserved,
                c =>
                {
                    c.MessageId = messageId;
                    c.CorrelationId = reserved.CorrelationId;
                },
                TestContext.Current.CancellationToken);
    }

    private async Task PublishReservationFailedAsync(Guid orderId, Guid messageId)
    {
        StockReservationFailed failed = new()
        {
            MessageId = messageId,
            CorrelationId = orderId,
            OccurredAt = DateTimeOffset.UtcNow,
            OrderId = orderId,
            UnavailableProductIds = [Guid.CreateVersion7()]
        };

        // The saga alone — nothing in this service consumes a failed
        // reservation a second time.
        _published.Add((messageId, DependencyInjection.FulfilmentSagaQueue));

        await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .Publish(
                failed,
                c =>
                {
                    c.MessageId = messageId;
                    c.CorrelationId = failed.CorrelationId;
                },
                TestContext.Current.CancellationToken);
    }

    private static Task Eventually(Func<Task<int>> read, int expected, string because) =>
        Eventually<int>(read, expected, because);

    // Generic since #143 needed the state COLUMN as well as a row count — the
    // int overload above is kept so no existing call site had to move, which
    // is what keeps this change out of the diff of tests it is not about.
    private static async Task Eventually<T>(Func<Task<T>> read, T expected, string because)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + DeliveryBudget;
        T actual = default!;

        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = await read();

            if (EqualityComparer<T>.Default.Equals(actual, expected))
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        actual.ShouldBe(expected, because);
    }
}
