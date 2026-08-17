namespace Ordering.Domain.Orders;

/// <summary>
/// §5.4's state machine, as a closed set. <see cref="Order"/> is the only
/// thing that assigns one, and every transition between them is a named
/// method on the aggregate rather than a setter.
/// </summary>
/// <remarks>
/// Persisted by name and never by number (§7.2), so the numeric values are
/// not a contract and a member may be inserted without rewriting rows.
/// <c>Delivered</c> has no transition, and no chapter gives it one: §9.6's
/// saga finalises on <c>ShipmentDispatched</c> — sending <c>MarkOrderShipped</c>
/// and then deleting its own instance — and §3.2 gives Ordering no consumer
/// for <c>ShipmentDelivered</c> at all, which Notifications alone accepts. So
/// the member exists for the guard rather than for a transition:
/// <see cref="Order.Cancel"/> refuses to cancel out of it, and §5.4 writes
/// that refusal over both terminal states.
/// </remarks>
public enum OrderStatus
{
    Draft,
    AwaitingStock,
    AwaitingPayment,
    Confirmed,
    Shipped,
    Delivered,
    Cancelled
}
