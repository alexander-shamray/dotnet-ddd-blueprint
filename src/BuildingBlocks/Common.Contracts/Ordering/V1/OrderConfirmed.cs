namespace Common.Contracts.Ordering.V1;

/// <summary>
/// An order was confirmed — payment authorised, stock held (§3.2). Shipping
/// consumes it, which is why the address travels with it.
/// </summary>
/// <remarks>
/// <b>Fat enough, not fat.</b> §9.1's guidance is to carry the data consumers
/// actually need to act, established by asking them: Shipping cannot function
/// without the delivery address and must not call back to Ordering to get one,
/// so the address is here and the customer's name is not.
/// </remarks>
public sealed record OrderConfirmed : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required decimal TotalAmount { get; init; }

    public required string Currency { get; init; }

    public required IReadOnlyList<ConfirmedLine> Lines { get; init; }

    public required ShippingAddressV1 ShippingAddress { get; init; }
}

/// <summary>
/// A line as <see cref="OrderConfirmed"/> carries it — its own type, for the
/// reason <see cref="PlacedLine"/> states.
/// </summary>
public sealed record ConfirmedLine(Guid ProductId, int Quantity, decimal UnitPrice);

/// <summary>
/// The delivery address, primitives only, versioned with the contract that owns
/// it exactly as the line types are.
/// </summary>
/// <remarks>
/// <b>Unrelated to the application's <c>AddressDto</c>, and to the domain's
/// <c>Address</c>.</b> A wire contract and a command payload version on
/// different schedules (§4.3), and the value object may not appear here at all
/// — a contract naming a domain type drags <c>Ordering.Domain</c> into every
/// service that references this assembly (§9.1).
/// <para>
/// The <c>V1</c> suffix is on the type here and nowhere else in this assembly,
/// and it is not a versioning scheme — §9.2 puts the version in the namespace.
/// It is a disambiguator against the three other <c>Address</c>-shaped types
/// this platform has, and the name Appendix D.5 already gives it.
/// </para>
/// </remarks>
public sealed record ShippingAddressV1(string Line1, string City, string PostCode, string Country);
