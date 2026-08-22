using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;

namespace Ordering.Application.Tests;

/// <summary>
/// The eight events §9.6's saga reacts to, built for a test.
/// </summary>
/// <remarks>
/// <b>A builder rather than object initialisers at each call site, and §12.5
/// says why.</b> Every member of every V1 contract is <c>required</c> — the
/// §9.1 envelope included — so <c>new StockReserved { OrderId = orderId }</c>
/// does not compile and there is no partial construction to elide. Written out
/// per test, the three envelope members would be most of every saga test.
/// <para>
/// <c>OccurredAt</c> is a fixed instant rather than <c>UtcNow</c>: the saga
/// copies it onto <c>StartedAt</c>, and a test asserting on that field must not
/// be asserting on when it ran.
/// </para>
/// </remarks>
internal static class SagaContracts
{
    internal static readonly DateTimeOffset Occurred = new(2026, 8, 19, 9, 0, 0, TimeSpan.Zero);

    internal static readonly Guid Product = Guid.Parse("6f0b1f7e-0d4f-4c53-9a2f-0b6a1e2d3c40");

    internal const decimal Total = 129.98m;

    internal const string Currency = "EUR";

    internal static OrderPlaced OrderPlaced(Guid orderId, Guid customerId) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = orderId,
        OccurredAt = Occurred,
        OrderId = orderId,
        CustomerId = customerId,
        TotalAmount = Total,
        Currency = Currency,
        Lines = [new PlacedLine(Product, 2, 64.99m)]
    };

    internal static StockReserved StockReserved(Guid orderId) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = orderId,
        OccurredAt = Occurred,
        OrderId = orderId
    };

    internal static StockReservationFailed StockReservationFailed(Guid orderId) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = orderId,
        OccurredAt = Occurred,
        OrderId = orderId,
        UnavailableProductIds = [Product]
    };

    internal static PaymentAuthorised PaymentAuthorised(Guid orderId, string reference) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = orderId,
        OccurredAt = Occurred,
        OrderId = orderId,
        Reference = reference,
        Amount = Total,
        Currency = Currency
    };

    internal static PaymentDeclined PaymentDeclined(Guid orderId, string reason) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = orderId,
        OccurredAt = Occurred,
        OrderId = orderId,
        Reason = reason
    };

    internal static StockReleased StockReleased(Guid orderId) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = orderId,
        OccurredAt = Occurred,
        OrderId = orderId
    };

    /// <summary>
    /// The eighth, and the only one Ordering publishes itself (§3.2).
    /// </summary>
    /// <remarks>
    /// The reason is a parameter because both origins reach the saga through
    /// this one type: <c>customer_request</c> from §11.4's endpoint, and every
    /// <see cref="CancelReasons"/> code the saga itself sent on
    /// <c>CancelOrder</c>, echoed back by the aggregate it cancelled.
    /// </remarks>
    internal static OrderCancelled OrderCancelled(Guid orderId, Guid customerId, string reason) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = orderId,
        OccurredAt = Occurred,
        OrderId = orderId,
        CustomerId = customerId,
        Reason = reason
    };

    internal static ShipmentDispatched ShipmentDispatched(Guid orderId, string tracking) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = orderId,
        OccurredAt = Occurred,
        OrderId = orderId,
        TrackingNumber = tracking
    };
}
