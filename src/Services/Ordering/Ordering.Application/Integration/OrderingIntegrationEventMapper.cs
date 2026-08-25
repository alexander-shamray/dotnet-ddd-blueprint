using Common.Application;
using Common.Contracts.Ordering.V1;
using Common.Domain;
using Ordering.Application.Orders;
using Ordering.Domain.Common;
using Ordering.Domain.Orders.Events;

namespace Ordering.Application.Integration;

/// <summary>
/// §9.3's allow-list for this service. §5.5 states the principle — never publish a
/// domain event to the bus — and this is the mechanism that makes it
/// structural rather than aspirational: a domain event absent from
/// <see cref="Registry"/> never reaches the bus, by construction, not by
/// review.
/// </summary>
internal sealed class OrderingIntegrationEventMapper : IIntegrationEventMapper
{
    /// <summary>
    /// §3.2's Publishes column for Ordering, and exactly it. Three of the five
    /// domain events <c>Order</c> raises are here; the other two are
    /// deliberately not.
    /// </summary>
    /// <remarks>
    /// <c>OrderStockConfirmedDomainEvent</c> is internal bookkeeping — §3.2
    /// gives no service a subscription to it, and Inventory already knows,
    /// because it is the thing that told us. <c>OrderShippedDomainEvent</c> is
    /// the sharper case and the one worth stating: despatch is <em>Shipping's</em>
    /// fact, published by Shipping as <c>ShipmentDispatched</c> and consumed
    /// by three services (§3.2). Republishing it under Ordering's name would
    /// put the same event on the bus twice with two owners, which is the
    /// versioning problem §9.2 exists to avoid, arriving as a duplication
    /// rather than as a change.
    /// </remarks>
    private static readonly Dictionary<Type, Func<IDomainEvent, object>> Registry = new()
    {
        [typeof(OrderPlacedDomainEvent)] = e => ToContract((OrderPlacedDomainEvent)e),
        [typeof(OrderConfirmedDomainEvent)] = e => ToContract((OrderConfirmedDomainEvent)e),
        [typeof(OrderCancelledDomainEvent)] = e => ToContract((OrderCancelledDomainEvent)e)
    };

    public IReadOnlyList<object> Map(IReadOnlyList<IDomainEvent> domainEvents)
    {
        List<object> mapped = [];

        foreach (IDomainEvent domainEvent in domainEvents)
        {
            if (!Registry.TryGetValue(domainEvent.GetType(), out Func<IDomainEvent, object>? map))
                continue;                       // Unregistered → local-only. Not an error.

            mapped.Add(map(domainEvent));       // Registered and throwing → fails the command.
        }

        return mapped;
    }

    // Minted here and nowhere else, on Catalog's terms: Stage copies both onto
    // the row and DeliverAsync copies them onto the transport, so the body, the
    // row, the broker header and the inbox key are one GUID (§9.1). The
    // correlation is the ORDER in all three, which is also what §9.6's saga
    // correlates its instance on — so a support tool following one id sees the
    // whole workflow rather than three unrelated traces.
    private static OrderPlaced ToContract(OrderPlacedDomainEvent e) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = e.OrderId.Value,
        OccurredAt = e.OccurredAt,
        OrderId = e.OrderId.Value,
        CustomerId = e.CustomerId.Value,
        TotalAmount = e.Total.Amount,
        Currency = e.Total.Currency,
        Lines = [.. e.Lines.Select(l => new PlacedLine(l.ProductId.Value, l.Quantity, l.UnitPrice.Amount))]
    };

    private static OrderConfirmed ToContract(OrderConfirmedDomainEvent e) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = e.OrderId.Value,
        OccurredAt = e.OccurredAt,
        OrderId = e.OrderId.Value,
        CustomerId = e.CustomerId.Value,
        TotalAmount = e.Total.Amount,
        Currency = e.Total.Currency,
        Lines = [.. e.Lines.Select(l => new ConfirmedLine(l.ProductId.Value, l.Quantity, l.UnitPrice.Amount))],
        ShippingAddress = ToContract(e.ShippingAddress)
    };

    private static OrderCancelled ToContract(OrderCancelledDomainEvent e) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = e.OrderId.Value,
        OccurredAt = e.OccurredAt,
        OrderId = e.OrderId.Value,
        CustomerId = e.CustomerId.Value,
        // The wire vocabulary, through the one map that owns it (§9.6). The
        // enum's member names are not the contract and must never become it —
        // CancellationReasons.ToCode is what keeps the two spellings apart.
        Reason = CancellationReasons.ToCode(e.Reason),
        // Populated from this release on. §9.6's saga reads an ABSENT origin
        // as its own echo and discards it, which is what holds the pre-#123
        // behaviour for the length of a rolling deploy — reading absent as User
        // would fault every cancellation an older instance published.
        Origin = CancellationOrigins.ToCode(e.Origin)
    };

    // A value object decomposed into primitives, which is the whole of what
    // §9.1 asks of a contract. ShippingAddressV1 is Common.Contracts' own type
    // and unrelated to Ordering.Domain's Address beyond the resemblance the
    // two happen to have today — which this PR made closer by adding Line2,
    // and which is allowed to end at the next version of either.
    private static ShippingAddressV1 ToContract(Address address) =>
        new(address.Line1, address.Line2, address.City, address.PostalCode, address.Country);
}
