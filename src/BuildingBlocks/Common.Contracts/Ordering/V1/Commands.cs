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
/// Escalate an order to a human (§9.6) — the path for anything the workflow
/// cannot resolve itself. <b>Two of its four reasons are a wait with no
/// automatic compensation and two are not</b>, and this summary said only the
/// first until the second arrived: a cancellation landing after an
/// authorisation is not a timeout, and §3.2 gives <i>Ordering</i> no refund
/// command to answer it with — which is not the same as no automatic refund,
/// since §3.2 has Payments consume <c>OrderCancelled</c> and void an
/// authorisation already taken. The two codes differ on whether that path
/// could have reached the authorisation at all; <see cref="ReviewReasons"/>
/// carries the distinction.
/// </summary>
/// <remarks>
/// This does <b>not</b> touch the <c>Order</c> aggregate, and the reason is
/// that "a human should look at this" is a fact about operations rather than
/// about the order — <b>not</b> that the order is unchanged, and not that it
/// changed either. An earlier revision of this paragraph claimed the second
/// for the two cancellation codes and it does not hold for
/// <c>cancelled_after_payment</c>: when compensation began from a decline or
/// a payment timeout the saga is in <c>Compensating</c> and has not yet sent
/// <c>CancelOrder</c> — that goes on the state's exit — so a late
/// authorisation raises this row while the order is still uncancelled.
/// The aggregate's state is simply not what decides where the row lives.
/// It lands in an operations table either way.
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
    /// An authorisation landed while the saga was already compensating.
    /// </summary>
    /// <remarks>
    /// <b>Not "a customer cancelled", which is what this summary said.</b>
    /// §9.6 reaches <c>Compensating</c> three ways — a customer's
    /// <c>OrderCancelled</c>, a <c>PaymentDeclined</c>, and a
    /// <c>PaymentTimeout</c> — and the escalation fires from all of them,
    /// because its condition is the money arriving, not the reason
    /// compensation started. The timeout case is the one that matters
    /// operationally: a PSP slower than fifteen minutes that then
    /// authorises produces this row with no customer involved at all, so a
    /// spike is a dependency signal and not a product one.
    /// </remarks>
    /// <remarks>
    /// <b>The cancellation the saga cannot compensate, raised from
    /// <c>Compensating</c>.</b> It was the only one until
    /// <see cref="CancelledAfterConfirmation"/> was split out of it, and that
    /// one is the same money problem from <c>Confirmed</c> — so neither is
    /// unique in what it cannot undo, and the state each is raised from is the
    /// whole of the difference — and the difference is bigger than "which
    /// state", because it decides whether the automatic refund could have
    /// applied. Undoing an authorisation is a refund, and §3.2 closes
    /// <i>Ordering's</i> outbound options at <c>AuthorisePayment</c>: there is
    /// no refund command. Payments none the less refunds off
    /// <c>OrderCancelled</c>, which it consumes (§3.2) and which the event's
    /// own contract says voids an authorisation already taken.
    /// <para>
    /// <b>This code is the case that path cannot reach.</b> It is raised when
    /// an authorisation lands while the saga is already compensating, so the
    /// authorisation is <i>later</i> than the <c>OrderCancelled</c> Payments
    /// would have voided against — nothing automatic is coming, and a human
    /// owns the money. <see cref="CancelledAfterConfirmation"/> is the
    /// opposite: it is raised by the very publication Payments consumes, so
    /// there the void is already on its way and what needs a person is
    /// Shipping. An earlier revision of this paragraph called them "the same
    /// money problem", which is true of the symptom and false of the remedy.
    /// </para>
    /// This is a <see cref="ReviewReasons"/> code and not a
    /// <see cref="CancelReasons"/> one because what needs a person is the
    /// money — <b>not</b> because the order is already cancelled, which this
    /// line used to say and which is only true on one of the three paths
    /// into <c>Compensating</c>. From a decline or a payment timeout the
    /// <c>CancelOrder</c> is still owed at the state's exit, so this row can
    /// precede the cancellation it is named after.
    /// </remarks>
    public const string CancelledAfterPayment = "cancelled_after_payment";

    /// <summary>
    /// A customer cancelled an order the saga had confirmed and was waiting
    /// on Shipping for.
    /// </summary>
    /// <remarks>
    /// <b>"The saga had confirmed" is weaker than "the order is confirmed",
    /// and the difference is a filed race.</b> §9.6 enters <c>Confirmed</c>
    /// the moment it SENDS <c>ConfirmOrder</c>, not when that command
    /// commits — so a cancellation can beat it to the aggregate, and this
    /// code is then raised for an order that was never confirmed and that
    /// Shipping was never told about. See
    /// <see href="https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/126">#126</see>.
    /// </remarks>
    /// <remarks>
    /// <b>Distinct from <see cref="CancelledAfterPayment"/> because the
    /// procedure is different, and the row is the only thing an operator
    /// has.</b> Both are raised by §9.6's saga on a cancellation arriving after
    /// an authorisation, from two different states — and
    /// <c>ordering.OrderReviews</c> persists <c>(OrderId, Reason, RaisedAt)</c>
    /// and nothing else, so a single code makes the two indistinguishable by
    /// the time anyone reads the queue. The saga has usually finalised by then;
    /// its state is gone.
    /// <para>
    /// What separates them: this one means the order reached <c>Confirmed</c>,
    /// so <b>Shipping may still despatch it</b> and stopping that is the first
    /// step. <see cref="CancelledAfterPayment"/> means compensation was already
    /// under way — there is no despatch to stop and a <c>ReleaseStock</c> is in
    /// flight. The runbook has always described these as two procedures; until
    /// this code existed it keyed them on a saga state nothing recorded.
    /// </para>
    /// </remarks>
    public const string CancelledAfterConfirmation = "cancelled_after_confirmation";
}
