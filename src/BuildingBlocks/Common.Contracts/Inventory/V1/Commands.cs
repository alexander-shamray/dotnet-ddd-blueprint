namespace Common.Contracts.Inventory.V1;

/// <summary>
/// Hold stock for an order (§3.2's Accepts column), sent by the saga to
/// <c>inventory-commands</c> (§9.6).
/// </summary>
public sealed record ReserveStock(Guid OrderId, IReadOnlyList<StockLine> Lines);

/// <summary>
/// Release a held reservation (§3.2). The compensating step for a declined or
/// timed-out payment.
/// </summary>
public sealed record ReleaseStock(Guid OrderId);

/// <summary>
/// A line as <see cref="ReserveStock"/> carries it.
/// </summary>
/// <remarks>
/// <b>Not <c>PlacedLine</c>.</b> Reserving stock needs no price, and Inventory's
/// command must not have to change because Ordering versioned an event (§9.6).
/// The saga maps one to the other rather than forwarding.
/// </remarks>
public sealed record StockLine(Guid ProductId, int Quantity);
