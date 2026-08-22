namespace Common.Contracts.Ordering.V1;

/// <summary>
/// Cancel an order (§3.2's Accepts column). Sent by the fulfilment saga, never
/// published — a command has exactly one owner, and publishing it would deliver
/// to every subscriber that happened to bind the type (§9.6).
/// </summary>
/// <remarks>
/// <b>No envelope, and that is the rule rather than an omission.</b> Commands
/// deliberately do not implement <see cref="IIntegrationEvent"/> (§9.1): they
/// are routed by <c>CommandConsumer</c> (§9.4), they carry no envelope in the
/// body, and their <c>MessageId</c> is the transport's.
/// <para>
/// <c>Reason</c> is a string code from <see cref="CancelReasons"/>, for the
/// reason <see cref="OrderCancelled"/> states. Ordering's handler parses it
/// back into <c>CancellationReason</c>, and an unknown code fails loudly rather
/// than defaulting.
/// </para>
/// </remarks>
public sealed record CancelOrder(Guid OrderId, string Reason);

/// <summary>
/// Confirm an order once payment is authorised (§9.6).
/// </summary>
/// <remarks>
/// <c>PaymentReference</c> is a string: the reference originates in Payments as
/// an opaque provider token, and Ordering's <c>PaymentReference</c> value object
/// is a domain type a contract may not name (§9.1).
/// </remarks>
public sealed record ConfirmOrder(Guid OrderId, string PaymentReference);

/// <summary>
/// Record that an order has been despatched (§9.6).
/// </summary>
/// <remarks>
/// Despatch is Shipping's fact; recording it on the order is Ordering's
/// decision, so the saga sends this command rather than Ordering subscribing to
/// <c>ShipmentDispatched</c> directly. The aggregate still enforces the
/// transition.
/// </remarks>
public sealed record MarkOrderShipped(Guid OrderId, string TrackingNumber);

/// <summary>
/// Escalate an order to a human (§9.6) — the path for a wait with no automatic
/// compensation.
/// </summary>
/// <remarks>
/// This does <b>not</b> touch the <c>Order</c> aggregate: the order's own state
/// has not changed, and "a human should look at this" is a fact about the
/// process rather than about the order. It lands in an operations table.
/// </remarks>
public sealed record FlagOrderForReview(Guid OrderId, string Reason);

/// <summary>
/// The wire vocabulary for <see cref="CancelOrder.Reason"/> and
/// <see cref="OrderCancelled.Reason"/>. One mapping back to
/// <c>CancellationReason</c>, in one place.
/// </summary>
/// <remarks>
/// A <c>static class</c> compiles to <c>abstract sealed</c>, which is what keeps
/// this out of §12.6's contract suite without a special case: that filter asks
/// for concrete types, and a vocabulary is not one.
/// </remarks>
public static class CancelReasons
{
    public const string OutOfStock = "out_of_stock";

    public const string StockTimeout = "stock_timeout";

    public const string PaymentDeclined = "payment_declined";

    /// <summary>
    /// Deliberately distinct from <see cref="PaymentDeclined"/>. The two
    /// compensate identically and mean opposite things — the first is the
    /// customer's bank saying no, the second is the PSP saying nothing. They
    /// are one dimension value apart on <c>orders.cancelled</c> (§13.3) and a
    /// different incident.
    /// </summary>
    public const string PaymentTimeout = "payment_timeout";

    public const string CustomerRequest = "customer_request";
}

/// <summary>
/// The wire vocabulary for <see cref="FlagOrderForReview.Reason"/> (§9.6).
/// </summary>
public static class ReviewReasons
{
    public const string NotDespatched = "not_despatched";

    public const string StockNotReleased = "stock_not_released";

    /// <summary>
    /// A customer cancelled an order whose payment was already authorised.
    /// </summary>
    /// <remarks>
    /// <b>The one cancellation the saga cannot compensate.</b> Undoing an
    /// authorisation is a refund, and §3.2 closes Payments' Accepts column at
    /// <c>AuthorisePayment</c> — so the workflow escalates instead of
    /// compensating, on the same argument the despatch timeout makes: a wait
    /// with no automatic answer still ends, and a human owns what follows.
    /// This is a <see cref="ReviewReasons"/> code and not a
    /// <see cref="CancelReasons"/> one, because the order is already cancelled
    /// — what needs a person is the money, not the order.
    /// </remarks>
    public const string CancelledAfterPayment = "cancelled_after_payment";
}
