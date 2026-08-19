using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
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

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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
        await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        (await SagaRowsAsync(orderId)).ShouldBe(
            0,
            "a replayed OrderPlaced must not start fulfilment again — a second ReserveStock and a second " +
            "AuthorisePayment for one order is a double charge, and the row is the observable half of it");
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

        await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .Publish(
                new Ordering.Infrastructure.Messaging.StockReservationExpired(orderId),
                TestContext.Current.CancellationToken);

        await Eventually(
            () => SagaRowsAsync(orderId),
            expected: 0,
            because: "the stock timeout cancels the order and finalises — if the filter had rejected the " +
                "message for want of a MessageId, the row would still be there");
    }

    private Task<int> SagaRowsAsync(Guid orderId) =>
        fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM ordering.OrderFulfilmentStates WHERE CorrelationId = {0}",
            orderId);

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

        await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .Publish(placed, c => c.MessageId = messageId, TestContext.Current.CancellationToken);
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

        await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .Publish(failed, c => c.MessageId = messageId, TestContext.Current.CancellationToken);
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
