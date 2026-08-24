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

    private static async Task Eventually(Func<Task<int>> read, int expected, string because)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + DeliveryBudget;
        int actual = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = await read();

            if (actual == expected)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        actual.ShouldBe(expected, because);
    }
}
