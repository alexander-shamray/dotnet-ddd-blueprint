using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;

namespace Ordering.Application.Tests;

/// <summary>
/// The events §9.6's saga reacts to, built for a test.
/// </summary>
/// <remarks>
/// <b>A builder rather than object initialisers at each call site, and §12.5
/// says why.</b> Every member of every V1 contract is <c>required</c> unless
/// §12.6's additive-member list names it — the §9.1 envelope included, and none
/// of these — so <c>new StockReserved { OrderId = orderId }</c>
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
    /// One of the events Ordering publishes to itself — <c>OrderPlaced</c> and
    /// <c>OrderConfirmed</c> are the others (§3.2).
    /// </summary>
    /// <remarks>
    /// The reason is a parameter because both origins reach the saga through
    /// this one type: §11.4's endpoint, and the code the saga itself sent on
    /// <c>CancelOrder</c>, echoed back by the aggregate it cancelled.
    /// <para>
    /// <b>The code does not say which origin it came from, and an earlier
    /// revision of these remarks taught that it did.</b> It paired
    /// <c>customer_request</c> with the endpoint and everything else with the
    /// saga; <c>CancellationReasons.TryParse</c> accepts all five
    /// <see cref="CancelReasons"/> codes, so the endpoint may send any of
    /// them, and the saga sends whichever its own transition recorded. A
    /// double that teaches a discriminator the real contract does not have is
    /// the failure PR-26 named — so this is stated rather than left to be
    /// inferred from the parameter.
    /// </para>
    /// <para>
    /// <b><c>origin</c> defaults to <see cref="CancelOrigins.User"/>, and the
    /// direction of that default is the decision (#123).</b> It is the only
    /// origin the saga's missing-instance branch faults on, so a test that
    /// forgets to state one gets the loud behaviour rather than the silent
    /// discard — the reverse default would let a test pass by being ignored.
    /// It is also the commoner case in this file: most of these publishes are
    /// a customer cancelling mid-workflow, where the instance exists and the
    /// origin is not read at all.
    /// </para>
    /// <para>
    /// Pass <see cref="CancelOrigins.Workflow"/> for the saga's own echo, and
    /// <c>null</c> for a publisher predating the field.
    /// </para>
    /// </remarks>
    internal static OrderCancelled OrderCancelled(
        Guid orderId,
        Guid customerId,
        string reason,
        string? origin = CancelOrigins.User) => new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = orderId,
            OccurredAt = Occurred,
            OrderId = orderId,
            CustomerId = customerId,
            Reason = reason,
            Origin = origin
        };

    /// <summary>
    /// The acknowledgement §9.6's <c>AwaitingConfirmation</c> waits for
    /// (#126) — the aggregate's own confirmation, published in the
    /// transaction that set the status.
    /// </summary>
    /// <remarks>
    /// <b>The saga reads only <c>OrderId</c> off it, and the rest is built
    /// anyway.</b> Every member of THIS contract is <c>required</c>, so there
    /// is no partial construction to elide — but the more useful reason is
    /// that a double which fills only the fields today's consumer happens to
    /// read teaches the next reader that the others are optional. §3.2 gives
    /// this event to Shipping, which needs the address and the lines.
    /// </remarks>
    internal static OrderConfirmed OrderConfirmed(Guid orderId, Guid customerId) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = orderId,
        OccurredAt = Occurred,
        OrderId = orderId,
        CustomerId = customerId,
        TotalAmount = Total,
        Currency = Currency,
        Lines = [new ConfirmedLine(Product, 2, 64.99m)],
        ShippingAddress = new ShippingAddressV1(
            "1 Test Street",
            null,
            "Springfield",
            "12345",
            "GB")
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
