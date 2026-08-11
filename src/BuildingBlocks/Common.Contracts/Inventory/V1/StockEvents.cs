namespace Common.Contracts.Inventory.V1;

/// <summary>
/// Stock was held for an order (§3.2). The saga's cue to authorise payment
/// (§9.6).
/// </summary>
public sealed record StockReserved : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }
}

/// <summary>
/// Stock could not be held for an order (§3.2). The saga cancels on it (§9.6).
/// </summary>
/// <remarks>
/// <b><see cref="UnavailableProductIds"/> is the fact this step decided</b>, and
/// the reason it is here rather than left to a support query: the saga cancels
/// with <c>CancelReasons.OutOfStock</c> and finalises, so by the time anyone
/// asks *which* lines failed, the instance is gone
/// (<c>SetCompletedWhenFinalized</c>). Ids rather than a message — a consumer
/// that wants names has a product read model, and a sentence on a contract is a
/// sentence every consumer must parse.
/// </remarks>
public sealed record StockReservationFailed : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }

    public required IReadOnlyList<Guid> UnavailableProductIds { get; init; }
}

/// <summary>
/// A reservation was released (§3.2) — the compensation for a failed payment,
/// and a new business fact rather than an undo (§9.6).
/// </summary>
public sealed record StockReleased : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }
}

/// <summary>
/// The available quantity for a product moved (§3.2). Catalog's one Consumes
/// cell — the whole of what it subscribes to.
/// </summary>
/// <remarks>
/// <b><see cref="QuantityAvailable"/> is a level, not a delta</b>, and the
/// difference is what makes the consumer idempotent by construction: a
/// redelivered delta double-counts and a redelivered level does not, which is
/// the out-of-order guard §6.6 asks every projection for rather than a
/// property this event happens to have.
/// </remarks>
public sealed record StockLevelChanged : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid ProductId { get; init; }

    public required int QuantityAvailable { get; init; }
}
