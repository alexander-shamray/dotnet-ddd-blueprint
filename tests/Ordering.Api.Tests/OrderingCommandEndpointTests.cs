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

    private readonly List<Guid> _published = [];

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    /// <summary>
    /// Drains the deliveries this test started, so the next test's
    /// <c>ResetAsync</c> cannot truncate the schema underneath a consumer that
    /// is still committing — the flake
    /// <see cref="CatalogEventEndpointTests"/> records in full.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (Guid messageId in _published)
            await Eventually(
                async () => (await InboxRowsAsync(messageId)).Count,
                expected: 1,
                because: "a delivery still running when the next test resets is a flake in that test");
    }

    [Fact]
    public async Task Inventorys_reservation_advances_the_order_over_the_broker()
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
        // §9.8 requires the inbox on every endpoint but the saga's, and
        // ConfirmStock is NOT idempotent — the aggregate throws on the second
        // call. So without the filter a redelivery is a domain rejection
        // counted against a healthy order, and with it the message is dropped
        // before the handler runs.
        Guid orderId = await fixture.SeedOrderAsync(Customer);
        var messageId = Guid.CreateVersion7();

        await PublishStockReservedAsync(orderId, messageId);
        await EventuallyStatus(orderId, "AwaitingPayment", because: "the first delivery must land");

        await Eventually(
            async () => (await InboxRowsAsync(messageId)).Count,
            expected: 1,
            because: "a delivery that leaves no inbox row reached an endpoint with no filter on it");

        IReadOnlyList<InboxMessage> rows = await InboxRowsAsync(messageId);
        rows[0].Endpoint.ShouldBe(
            DependencyInjection.StockEventsQueue,
            "the saga binds the same event on its own queue — a row from that endpoint would mean the " +
            "consumer was bound where §9.8's exemption applies");

        // The same id again. The filter drops it, so the order stays where the
        // first delivery left it rather than being refused by the aggregate.
        await PublishStockReservedAsync(orderId, messageId, drain: false);
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        (await StatusAsync(orderId)).ShouldBe("AwaitingPayment");
        (await InboxRowsAsync(messageId)).Count.ShouldBe(1, "a suppressed duplicate writes no second row");
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
    public async Task A_command_this_service_cannot_parse_changes_nothing()
    {
        // §9.8's ContractMappingException path: a peer sending a code this
        // deployment does not know is not a domain rejection to be acked, and
        // no amount of backoff parses it. What matters here is that the
        // aggregate is untouched — the message's onward journey to the error
        // queue is the endpoint's retry policy, which MessagingRegistrationTests
        // cannot see and this test does not claim.
        Guid orderId = await fixture.SeedOrderAsync(Customer);

        await SendAsync(new CancelOrder(orderId, "reason_from_a_newer_deployment"));
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

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
            _published.Add(messageId);

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
    private async Task SendAsync<T>(T command)
        where T : class
    {
        ISendEndpoint endpoint = await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .GetSendEndpoint(new Uri($"queue:{DependencyInjection.CommandsQueue}"));

        await endpoint.Send(command, TestContext.Current.CancellationToken);
    }

    private async Task<IReadOnlyList<InboxMessage>> InboxRowsAsync(Guid messageId) =>
        [.. (await fixture.InboxAsync()).Where(r => r.MessageId == messageId)];

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
