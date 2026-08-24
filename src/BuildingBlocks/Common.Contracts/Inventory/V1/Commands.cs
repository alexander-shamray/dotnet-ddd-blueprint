namespace Common.Contracts.Inventory.V1;

/// <summary>
/// Hold stock for an order (§3.2's Accepts column), sent by the saga to
/// <c>inventory-commands</c> (§9.6).
/// </summary>
public sealed record ReserveStock(Guid OrderId, IReadOnlyList<StockLine> Lines);

/// <summary>
/// Establish that no stock is held for this order (§3.2), sent by the saga to
/// <c>inventory-commands</c> from every compensating transition it has (§9.6).
/// </summary>
/// <remarks>
/// <b>It names a postcondition rather than an object to undo, and that wording
/// is the contract.</b> This read "release a held reservation … the
/// compensating step for a declined or timed-out payment", which is narrower
/// than what the saga sends and than what Inventory owes. A customer's
/// cancellation reaches the same command, and ADR-024 requires Inventory to
/// honour it for an order whose <see cref="ReserveStock"/> has not arrived —
/// so "a held reservation" is exactly the premise the answer may not depend on.
/// <para>
/// Two guarantees follow and neither is visible in the record: it always
/// publishes <c>StockReleased</c>, and a release for an order Inventory has
/// seen no reserve for is <b>remembered</b>, so the reserve that follows is
/// refused with <c>StockReservationFailed</c>. §9.6's saga leans on the first
/// in the three states that send this command; the fourth state that absorbs
/// an early release sends none and leans on the event's other producer
/// instead.
/// </para>
/// </remarks>
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
