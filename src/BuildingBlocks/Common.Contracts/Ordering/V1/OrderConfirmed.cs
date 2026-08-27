namespace Common.Contracts.Ordering.V1;

/// <summary>
/// An order was confirmed — payment authorised, stock held (§3.2). Shipping
/// consumes it, and what it consumes is order facts and identifiers: the
/// lines, the total and the currency, keyed by <c>OrderId</c> and
/// <c>CustomerId</c>. No delivery address, and no directly identifying or
/// free-text personal data.
/// </summary>
/// <remarks>
/// <b>Identifiers, not personal data (§11.7).</b> This contract used to carry
/// the delivery address, on §9.1's "fat enough" argument that Shipping cannot
/// act without one and must not call back to Ordering to get it. That argument
/// was settled by the wrong chapter. An address on the wire reaches the broker,
/// survives in outbox rows §9.4's purge deliberately spares, and is copied into
/// whatever a consumer persists from what it received — a projection, a read
/// model, a log or a trace. An erasure request would have to reach all of
/// those, and §11.7's choreography reaches none of them, which is why that
/// section names an <c>OrderConfirmed</c> carrying personal data as its
/// counter-example and why this one no longer is it.
/// <para>
/// <b>The inbox is not one of those paths, and saying so is the point.</b>
/// <c>InboxMessage</c> records a message id, an endpoint and a handling time
/// and no payload at all, so a consumer stores an address only where its own
/// code chose to. The distinction matters because it is where the remedy
/// lives: a storage path nobody wrote is not a leak, and naming one that does
/// not exist would make the argument easier to dismiss than it deserves.
/// </para>
/// <para>
/// <b><c>CustomerId</c> is still personal data, and this contract does not
/// claim otherwise.</b> A resolvable pseudonymous identifier is personal data
/// under GDPR Art. 4; what makes it tractable is that only one service can
/// resolve it, so severing that link there de-identifies every copy downstream.
/// <c>ADR-035</c> states the rule and its residual in full.
/// </para>
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
/// decide.</b> No consumer ever <i>read</i> the field — Shipping and
/// Notifications do not exist, and §9.6's saga never touched it — so nothing
/// downstream is owed a migration, and with no cluster having run this
/// platform there was no old replica to hand a reduced payload to either.
/// That second half is the one doing the work: the saga <i>binds</i> this
/// contract, so it deserialises the whole payload whatever it reads, and
/// §9.2's "no service consumes the version" condition was therefore not met.
/// <c>ADR-035</c> records what a comparable removal owes once something is
/// deployed. What replaces it is a choice between an explicit, auditable read back
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
