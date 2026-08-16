namespace Ordering.Domain.Orders;

/// <summary>
/// §5.4's state machine, as a closed set. <see cref="Order"/> is the only
/// thing that assigns one, and every transition between them is a named
/// method on the aggregate rather than a setter.
/// </summary>
/// <remarks>
/// Persisted by name and never by number (§7.2), so the numeric values are
/// not a contract and a member may be inserted without rewriting rows.
/// <c>Delivered</c> has no transition yet — §9.6's saga is what sets it, and
/// <see cref="Order.Cancel"/> already refuses to cancel out of it, which is
/// the one rule the member carries before then.
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
