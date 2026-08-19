using Common.Application;
using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Infrastructure.Inbox;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Application.Orders.FlagOrderForReview;
using Ordering.Infrastructure.Messaging;
using Ordering.TestSupport;
using Shouldly;
using Xunit;
// Aliased because Common.Application has a DependencyInjection too, and the
// queue names this suite asserts on are the messaging one's.
using MessagingRegistration = Ordering.Infrastructure.Messaging.DependencyInjection;

namespace Ordering.Api.Tests;

/// <summary>
/// The two receive endpoints PR-21 added, driven over the real broker:
/// <c>ordering-commands</c> (§9.4) and <c>ordering-stock-events</c> (§9.8).
/// </summary>
/// <remarks>
/// <b>Nothing else could see either of them, and Copilot said so.</b> The saga
/// suite asserts what the state machine <em>sends</em> and stops at the queue
/// address; <see cref="MessagingRegistrationTests"/> enumerates registrations
/// and states in its own comment that the harness replaces the callback the
/// endpoints live in. So the mappers, the inbox filter, the retry policy and
/// all four command handlers could regress with every suite green — a binding
/// that silently stops arriving, which is this repository's most repeated
/// shape and the one PR-20 already paid for once.
/// <para>
/// <b>Ordering sends these commands to itself, and the topology is identical.</b>
/// MassTransit routes on the address, so the queue this reaches is the one
/// §9.6's saga addresses; what is asserted is Ordering's binding to it, and a
/// second host would add a container and prove nothing further.
/// </para>
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class OrderingCommandEndpointTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly Guid Customer = Guid.Parse("55555555-5555-5555-5555-555555555555");

    /// <summary>
    /// <see cref="CatalogEventEndpointTests"/>'s budget, for its reason: a
    /// broker round trip on a runner holding two other container sets, and
    /// bounded because an endpoint that binds nothing never arrives late — it
    /// never arrives.
    /// </summary>
    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Every delivery this test started, as (message id, the endpoint that
    /// will record it).
    /// </summary>
    /// <remarks>
    /// <b>The endpoint is half the key, and it stopped being optional when the
    /// saga endpoint took an inbox filter.</b> A published <c>StockReserved</c>
    /// reaches <em>two</em> of Ordering's queues — the consumer's and the
    /// saga's — so it now leaves two rows, and a drain counting rows by message
    /// id alone waits for one, sees two, and fails in teardown. Which is how
    /// this list found out.
    /// </remarks>
    private readonly List<(Guid MessageId, string Endpoint)> _published = [];

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    /// <summary>
    /// Drains the deliveries this test started, so the next test's
    /// <c>ResetAsync</c> cannot truncate the schema underneath a consumer that
    /// is still committing — the flake
    /// <see cref="CatalogEventEndpointTests"/> records in full.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach ((Guid messageId, string endpoint) in _published)
        {
            await Eventually(
                async () => (await InboxRowsAsync(messageId, endpoint)).Count,
                expected: 1,
                because: "a delivery still running when the next test resets is a flake in that test");
        }
    }

    [Fact]
    public async Task A_reservation_from_Inventory_advances_the_order_over_the_broker()
    {
        // §9.6's fourth endpoint end to end: StockReserved on the broker →
        // ordering-stock-events → IntegrationEventConsumer → StockReservedHandler
        // → ConfirmStockCommand through the dispatcher → Order.ConfirmStock.
        //
        // Every link in that chain was previously covered only by its own unit,
        // and the two that connect them — the endpoint binding and the handler's
        // dispatch — by nothing at all.
        Guid orderId = await fixture.SeedOrderAsync(Customer);

        (await StatusAsync(orderId)).ShouldBe("AwaitingStock", "the arrange half is a claim too");

        await PublishStockReservedAsync(orderId, Guid.CreateVersion7());

        await EventuallyStatus(
            orderId,
            "AwaitingPayment",
            because: "the binding, the consumer, the handler's dispatch and the aggregate transition are " +
                "four links, and this is the only test that crosses all four");
    }

    [Fact]
    public async Task A_redelivered_reservation_is_suppressed_by_the_inbox()
    {
        // §9.8 requires the inbox on every endpoint — the saga's exemption went
        // with the replay finding — and ConfirmStock is NOT idempotent: the
        // aggregate throws on the second call. So without the filter a
        // redelivery is a domain rejection counted against a healthy order, and
        // with it the message is dropped before the handler runs.
        //
        // Scoped to this endpoint, because the same message id also lands on
        // the saga's queue and leaves a row there. Two readers, two rows, one
        // message — which is what (message id, endpoint) is the inbox key for.
        Guid orderId = await fixture.SeedOrderAsync(Customer);
        var messageId = Guid.CreateVersion7();

        await PublishStockReservedAsync(orderId, messageId);
        await EventuallyStatus(orderId, "AwaitingPayment", because: "the first delivery must land");

        await Eventually(
            async () => (await InboxRowsAsync(messageId, MessagingRegistration.StockEventsQueue)).Count,
            expected: 1,
            because: "a delivery that leaves no inbox row reached an endpoint with no filter on it");

        IReadOnlyList<InboxMessage> rows = await InboxRowsAsync(messageId, MessagingRegistration.StockEventsQueue);
        rows[0].Endpoint.ShouldBe(
            MessagingRegistration.StockEventsQueue,
            "the saga binds the same event on its own queue — a row from that endpoint would mean this " +
            "consumer was bound there instead, under a retry policy written for a state machine");

        // The same id again — followed by a sentinel for an unrelated order,
        // which is what bounds the wait.
        //
        // **The status is not the discriminator, and asserting it alone would
        // prove nothing** — Copilot's finding, and it is exactly right.
        // StockReservedHandler drops the Result on purpose (§9.8), so a
        // duplicate that reached the aggregate would be refused with
        // NotAwaitingStock and leave the order in the same state as one that
        // never arrived. Suppressed and rejected are indistinguishable from
        // ordering.Orders. What does discriminate is the row count above and
        // below: the filter *is* the row, so an endpoint without one leaves
        // zero and fails the first assertion, and the second pins that a
        // redelivery adds none. The status assertion stays as a regression
        // guard on the aggregate — it is not asked to carry this test.
        await PublishStockReservedAsync(orderId, messageId, drain: false);

        // Drained, unlike the duplicate above: this is a fresh id, so it
        // writes a real row on each endpoint and the teardown has to wait for
        // both. The duplicate opts out because a suppressed message writes
        // nothing and its id was registered by the delivery that landed.
        Guid sentinelOrderId = await fixture.SeedOrderAsync(Customer);
        await PublishStockReservedAsync(sentinelOrderId, Guid.CreateVersion7());
        await EventuallyStatus(
            sentinelOrderId,
            "AwaitingPayment",
            because: "a message published after the duplicate has been consumed, so the endpoint has " +
                "had the duplicate in front of it — a wait that scales with the runner rather than a " +
                "fixed three seconds that is generous here and short on a loaded agent");

        (await StatusAsync(orderId)).ShouldBe("AwaitingPayment");
        (await InboxRowsAsync(messageId, MessagingRegistration.StockEventsQueue))
            .Count
            .ShouldBe(1, "a suppressed duplicate writes no second row");

        // **The sentinel bounds this wait; it does not prove delivery order.**
        // Neither endpoint sets ConcurrentMessageLimit, so MassTransit's
        // prefetch lets the two be in flight together and the sentinel can
        // finish first. What it buys is a bound that tracks the machine — on a
        // runner slow enough to make three seconds too few, the sentinel is
        // slow by the same factor. Making it a proof would take
        // ConcurrentMessageLimit = 1 on ordering-stock-events, which is a
        // production topology change to suit a test and is not taken.
    }

    [Fact]
    public async Task A_confirmation_reaches_the_aggregate_through_the_command_queue()
    {
        // ordering-commands, its mapper and CommandConsumer, and
        // ConfirmOrderHandler — none of which any other suite reaches.
        Guid orderId = await fixture.SeedOrderAsync(Customer);

        await PublishStockReservedAsync(orderId, Guid.CreateVersion7());
        await EventuallyStatus(orderId, "AwaitingPayment", because: "ConfirmOrder needs a paid-for order");

        await SendAsync(new ConfirmOrder(orderId, "psp-endpoint-1"));

        await EventuallyStatus(
            orderId,
            "Confirmed",
            because: "the mapper parses the reference into PaymentReference and the handler applies it");

        // The confirmation is published, which is the part that can be
        // asserted. One Broker row, for §3.2's OrderConfirmed.
        (await fixture.OutboxAsync())
            .Count(r => r.MessageType.Contains("OrderConfirmed", StringComparison.Ordinal))
            .ShouldBe(1, "confirming stages §9.3's allow-listed contract on the Broker lane");

        // **The reference itself is asserted nowhere, and that is a real gap
        // rather than a thin test.** Order.ConfirmPayment stores nothing and
        // V1.OrderConfirmed has no field for it, so today the value the saga
        // carried from Payments survives only on OrderConfirmedDomainEvent —
        // which stages a Local row only when a projection handles it, and
        // §6.6's OrderSummaries is not built. So it is written to no table and
        // published in no contract.
        //
        // Found by this test asserting a column that does not exist. It is not
        // PR-21's to close — PaymentReference is §5.4's aggregate and
        // OrderSummaries is §6.6's projection — but PR-21 is what made the
        // path reachable, since nothing called ConfirmPayment before it.
        // Recorded in the decision log beside the other named residual.
    }

    [Fact]
    public async Task A_despatch_marks_the_order_shipped_through_the_command_queue()
    {
        Guid orderId = await fixture.SeedOrderAsync(Customer);

        await PublishStockReservedAsync(orderId, Guid.CreateVersion7());
        await EventuallyStatus(orderId, "AwaitingPayment", because: "the arrange half");

        await SendAsync(new ConfirmOrder(orderId, "psp-endpoint-2"));
        await EventuallyStatus(orderId, "Confirmed", because: "MarkOrderShipped requires it");

        await SendAsync(new MarkOrderShipped(orderId, "TRACK-ENDPOINT-1"));

        await EventuallyStatus(orderId, "Shipped", because: "the last transition the saga asks for");
    }

    [Fact]
    public async Task An_escalation_writes_a_review_row_and_touches_no_aggregate()
    {
        // §9.6's one command that changes no business state. The assertion is
        // both halves: the row appears, and the order does not move.
        Guid orderId = await fixture.SeedOrderAsync(Customer);

        await SendAsync(new FlagOrderForReview(orderId, ReviewReasons.NotDespatched));

        await Eventually(
            () => fixture.ScalarAsync<int>(
                "SELECT Value = COUNT(*) FROM ordering.OrderReviews WHERE OrderId = {0} AND Reason = {1}",
                orderId,
                ReviewReasons.NotDespatched),
            expected: 1,
            because: "the handler writes through IUnitOfWork.ExecuteRawAsync inside the command's own " +
                "transaction (§9.6)");

        (await StatusAsync(orderId)).ShouldBe(
            "AwaitingStock",
            "an escalation is a fact about the process, not about the order");
    }

    [Fact]
    public async Task A_second_escalation_for_one_reason_is_absorbed_rather_than_duplicated()
    {
        // The conditional insert's range lock (§9.6). A redelivery carries a
        // new message id — the inbox does not suppress it — so the statement
        // itself is what must absorb the second write rather than violating
        // the primary key.
        Guid orderId = await fixture.SeedOrderAsync(Customer);

        await SendAsync(new FlagOrderForReview(orderId, ReviewReasons.StockNotReleased));
        await Eventually(
            () => ReviewCountAsync(orderId),
            expected: 1,
            because: "the first escalation must land before a second can be absorbed");

        await SendAsync(new FlagOrderForReview(orderId, ReviewReasons.StockNotReleased));
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        (await ReviewCountAsync(orderId)).ShouldBe(
            1,
            "RaisedAt records when the process first stalled, so a repeat must not insert or move it");
    }

    [Fact]
    public async Task Two_escalations_racing_for_one_reason_both_succeed()
    {
        // The range lock, and the only test that can tell it is there.
        //
        // The sequential test above passes with `WITH (UPDLOCK, HOLDLOCK)`
        // deleted — Copilot's finding — because by the time the second
        // escalation runs the first row is committed and the NOT EXISTS sees
        // it. What the hints buy is the CONCURRENT case: without them both
        // deliveries read no row, both insert, and the loser takes a primary
        // key violation. The end state is still one row, which is why an
        // assertion on the count alone proves nothing; what changes is whether
        // a delivery faults on the way there.
        //
        // Dispatched directly rather than sent, because the endpoint would
        // absorb the fault: §9.8's retry would rerun the loser, the row would
        // exist, and the test would see one row and a green run. In process
        // there is nothing to catch the exception but this test.
        // **Eight racers and a gate, and both were arrived at by measuring
        // rather than by choosing.** Two racers with no gate passed on their
        // own without the hints and failed only alongside another test — a
        // guard reporting the machine's load rather than the defect. Eight with
        // no gate passed too. Eight behind the gate failed 3/3 alone, which is
        // what makes this a test rather than a hope.
        //
        // The gate is the part that matters: starting eight tasks is not eight
        // statements arriving together, because each one resolves a scope and a
        // dispatcher first and that work is enough to serialise them.
        Guid orderId = await fixture.SeedOrderAsync(Customer);

        // A gate, because starting eight tasks is not the same as eight
        // statements reaching SQL Server together — measured: without it they
        // serialised and the hintless build passed every run. Each racer builds
        // its scope, then waits here; releasing the gate is what puts the
        // read-then-write windows on top of each other.
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task[] racers = [.. Enumerable.Range(0, 8).Select(_ => DispatchEscalationAsync(orderId, start.Task))];

        start.SetResult();

        await Should.NotThrowAsync(
            () => Task.WhenAll(racers),
            "a concurrent duplicate must be absorbed by the range lock, not surface as a primary key " +
            "violation — without WITH (UPDLOCK, HOLDLOCK) this throws " +
            "\"Violation of PRIMARY KEY constraint 'PK_OrderReviews'\"");

        (await ReviewCountAsync(orderId)).ShouldBe(
            1,
            "and the lock must absorb them rather than deadlock — eight writers, one row");
    }

    private async Task DispatchEscalationAsync(Guid orderId, Task gate)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        // Everything expensive happens before the gate, so what is released is
        // eight dispatches and not eight container resolutions.
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await gate;

        await dispatcher.SendAsync(
            new FlagOrderForReviewCommand(orderId, ReviewReasons.NotDespatched),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_command_this_service_cannot_parse_changes_nothing()
    {
        // §9.8's ContractMappingException path: a peer sending a code this
        // deployment does not know is not a domain rejection to be acked, and
        // no amount of backoff parses it. What matters here is that the
        // aggregate is untouched — the message's onward journey to the error
        // queue is the endpoint's retry policy, which MessagingRegistrationTests
        // cannot see and this test does not claim.
        Guid orderId = await fixture.SeedOrderAsync(Customer);

        // Undrained on purpose, and for a different reason from the duplicate
        // above: this one faults in the mapper, so the filter never reaches
        // its record step and no row will ever appear. Waiting for one would
        // hang the teardown rather than protect it.
        await SendAsync(new CancelOrder(orderId, "reason_from_a_newer_deployment"), drain: false);

        // The third fixed sleep, converted on the same argument as the other
        // two: a valid command for a second order, waited for by its effect.
        // ContractMappingException is excluded from retry (§9.8), so the
        // unmappable message is on its way to the error queue and not being
        // redelivered behind this.
        Guid sentinelOrderId = await fixture.SeedOrderAsync(Customer);
        await SendAsync(new CancelOrder(sentinelOrderId, CancelReasons.OutOfStock));
        await EventuallyStatus(
            sentinelOrderId,
            "Cancelled",
            because: "a command sent after the unmappable one has been handled, so the endpoint has " +
                "had the unmappable one in front of it");

        (await StatusAsync(orderId)).ShouldBe(
            "AwaitingStock",
            "an unmappable reason must not reach Order.Cancel with a defaulted value");
    }

    private Task<int> ReviewCountAsync(Guid orderId) =>
        fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM ordering.OrderReviews WHERE OrderId = {0}",
            orderId);

    private async Task<string> StatusAsync(Guid orderId) =>
        await fixture.ScalarAsync<string>(
            "SELECT Value = Status FROM ordering.Orders WHERE Id = {0}",
            orderId);

    private async Task EventuallyStatus(Guid orderId, string expected, string because)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + DeliveryBudget;
        string actual = "";

        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = await StatusAsync(orderId);

            if (actual == expected)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        actual.ShouldBe(expected, because);
    }

    /// <summary>
    /// Publishes as Inventory would. <paramref name="drain"/> is false for a
    /// deliberate duplicate: the inbox drops it, so no second row will ever
    /// appear and <see cref="DisposeAsync"/> would wait out its whole budget.
    /// </summary>
    private async Task PublishStockReservedAsync(Guid orderId, Guid messageId, bool drain = true)
    {
        StockReserved reserved = new()
        {
            MessageId = messageId,
            CorrelationId = orderId,
            OccurredAt = DateTimeOffset.UtcNow,
            OrderId = orderId
        };

        if (drain)
        {
            // Both readers of §3.2's StockReserved, because both now record it:
            // the consumer on ordering-stock-events, and the saga on its own
            // queue since that endpoint took an inbox filter.
            _published.Add((messageId, MessagingRegistration.StockEventsQueue));
            _published.Add((messageId, MessagingRegistration.FulfilmentSagaQueue));
        }

        await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .Publish(reserved, c => c.MessageId = messageId, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Sends to <c>ordering-commands</c> by address, exactly as §9.6's saga
    /// does — <c>Endpoints.OrderingQueue</c> is internal to Infrastructure, so
    /// the literal here is the same string a queue name test would assert and
    /// a rename that missed one site fails these tests rather than production.
    /// </summary>
    /// <param name="drain">
    /// False only where no inbox row will ever be written, which is the
    /// malformed-contract case: <c>InboxFilter</c> commits its row <em>after</em>
    /// the consumer returns, and a mapper that throws means it never does.
    /// Waiting for that row would spend the whole delivery budget proving
    /// something the test already asserts.
    /// </param>
    /// <remarks>
    /// <b>Every other send is drained, and it was not until Copilot counted.</b>
    /// These tests observe the handler's own transaction — a status column, a
    /// review row — which commits <em>before</em> the inbox row does. So a test
    /// could see what it came for, return, and let the next test's
    /// <c>ResetAsync</c> truncate the schema underneath a <c>SaveChangesAsync</c>
    /// still in flight. That is precisely the flake
    /// <see cref="CatalogEventEndpointTests"/> records, arriving in the suite
    /// written to copy its teardown.
    /// </remarks>
    private async Task SendAsync<T>(T command, bool drain = true)
        where T : class
    {
        ISendEndpoint endpoint = await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .GetSendEndpoint(new Uri($"queue:{MessagingRegistration.CommandsQueue}"));

        var messageId = Guid.CreateVersion7();

        if (drain)
            _published.Add((messageId, MessagingRegistration.CommandsQueue));

        await endpoint.Send(command, c => c.MessageId = messageId, TestContext.Current.CancellationToken);
    }

    private async Task<IReadOnlyList<InboxMessage>> InboxRowsAsync(Guid messageId, string endpoint) =>
        [.. (await fixture.InboxAsync()).Where(r => r.MessageId == messageId && r.Endpoint == endpoint)];

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
