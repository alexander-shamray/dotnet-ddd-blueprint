namespace Common.Contracts.Ordering.V1;

/// <summary>
/// An order was confirmed — payment authorised, stock held (§3.2). Shipping
/// consumes it, and what it consumes is identifiers.
/// </summary>
/// <remarks>
/// <b>Identifiers, not personal data (§11.7).</b> This contract used to carry
/// the delivery address, on §9.1's "fat enough" argument that Shipping cannot
/// act without one and must not call back to Ordering to get it. That argument
/// was settled by the wrong chapter. An address on the wire is copied into the
/// broker, into every consumer's inbox and into any log or trace that recorded
/// a payload, so an erasure request would have to reach all three and §11.7's
/// choreography reaches none of them — which is why that section names an
/// <c>OrderConfirmed</c> carrying personal data as its counter-example, and
/// why this one no longer is it.
/// <para>
/// <b>Removed rather than guarded, and removed in one change.</b>
/// <c>ADR-028</c> took <c>CustomerId</c> off <c>AuthorisePayment</c> on the
/// reasoning that removing the field removes the possibility rather than
/// guarding against it, and the same reasoning applies here with more force,
/// because no guard reaches a payload already sitting in a queue. §9.2's own
/// carve-out is why there is no deprecation window: where the point of a change
/// is that a value must not be on the wire, a window that keeps publishing it
/// is not merely useless but harmful.
/// </para>
/// <para>
/// <b>How Shipping obtains an address is open, and is Shipping's PR to
/// decide.</b> The field had no consumer when it left — Shipping and
/// Notifications do not exist, and §9.6's saga never read it — so nothing is
/// owed a migration and this was the cheapest moment the removal will ever
/// have. What replaces it is a choice between an explicit, auditable read back
/// to Ordering, recorded as the ADR-017 exception such a hop has to be, and a
/// despatch-time lookup against whatever store owns the address by then.
/// Choosing before a consumer exists to state its needs is exactly the guessing
/// §9.1's "ask the consumers" rule refuses.
/// </para>
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
}

/// <summary>
/// A line as <see cref="OrderConfirmed"/> carries it — its own type, for the
/// reason <see cref="PlacedLine"/> states.
/// </summary>
public sealed record ConfirmedLine(Guid ProductId, int Quantity, decimal UnitPrice);
