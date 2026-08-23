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
/// first until the second arrived.
/// <para>
/// The shared condition of the other two is <b>authorised money in a workflow
/// whose outcome is cancellation</b> — deliberately not "a cancellation landing
/// after an authorisation", which this used to say and which describes only
/// <see cref="ReviewReasons.CancelledAfterConfirmation"/>. On
/// <see cref="ReviewReasons.PaymentAuthorisedDuringCompensation"/> the authorisation is the
/// <i>later</i> event, and on two of that code's four doors — a decline and a
/// payment timeout — no cancellation exists at all when it is raised.
/// </para>
/// <para>
/// §3.2 gives <i>Ordering</i> no refund command to answer that with, which is
/// not the same as no automatic refund: §3.2 has Payments consume
/// <c>OrderCancelled</c> and void an authorisation already taken. <b>Which of
/// the two codes gets that refund is not predictable</b>, and this summary
/// carried the claim that it is until §9.4's ordering was read — so
/// <see cref="ReviewReasons"/> distinguishes them by the state each is raised
/// from, and the runbook checks for a refund on both.
/// </para>
/// </summary>
/// <remarks>
/// This does <b>not</b> touch the <c>Order</c> aggregate, and the reason is
/// that "a human should look at this" is a fact about operations rather than
/// about the order — <b>not</b> that the order is unchanged, and not that it
/// changed either. An earlier revision of this paragraph claimed the second
/// for the two cancellation codes and it does not hold for
/// <c>payment_authorised_during_compensation</c>: when compensation began from a decline or
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
    /// <b>Whether that void has happened is not knowable from the code, and
    /// two revisions of this paragraph guessed in opposite directions.</b> The
    /// first called both codes "the same money problem"; the second said this
    /// one is beyond the automatic path while
    /// <see cref="CancelledAfterConfirmation"/> has its refund on the way.
    /// Neither holds. §9.4 orders nothing between two independent consumers, so
    /// this saga's view of a cancellation says nothing about Payments' — and on
    /// the decline and payment-timeout doors <b>no <c>OrderCancelled</c> exists
    /// yet</b> when this row is raised, since <c>CancelOrder</c> goes on
    /// <c>Compensating</c>'s exit. The cancellation, and the void after it, are
    /// still to come. So the money is what the two codes have in common and
    /// <b>Shipping</b> is what tells them apart: the sibling is raised from a
    /// state that may still despatch and this one is not. The runbook checks
    /// for a refund on both and predicts the answer on neither.
    /// </para>
    /// This is a <see cref="ReviewReasons"/> code and not a
    /// <see cref="CancelReasons"/> one because what needs a person is the
    /// money — <b>not</b> because the order is already cancelled, which this
    /// line used to say and which is only true on one of the three paths
    /// into <c>Compensating</c>. From a decline or a payment timeout the
    /// <c>CancelOrder</c> is still owed at the state's exit, so this row can
    /// precede the cancellation entirely.
    /// <para>
    /// <b>It was <c>cancelled_after_payment</c>, and the name asserted that
    /// order.</b> True on the two cancellation doors, false on the other two,
    /// and the prose around it spent several revisions saying so — which is
    /// the tell that the label rather than the paragraph was wrong. Renamed
    /// while <c>ordering.OrderReviews</c> had never held a row with it: a
    /// persisted vocabulary has exactly one cheap moment, the same rule this
    /// repository already states about a contract with no consumers, and the
    /// same edit after the first row is a migration. <b>A code named for one
    /// of its causes survives review precisely because it reads as an
    /// explanation.</b>
    /// </para>
    /// <para>
    /// <see cref="CancelledAfterConfirmation"/> keeps its name deliberately:
    /// it is raised only when an <c>OrderCancelled</c> reaches the saga in
    /// <c>Confirmed</c>, so the order it asserts holds on its one path.
    /// </para>
    /// </remarks>
    public const string PaymentAuthorisedDuringCompensation = "payment_authorised_during_compensation";

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
    /// <b>Distinct from <see cref="PaymentAuthorisedDuringCompensation"/> because the
    /// procedure is different, and the row is the only thing an operator
    /// has.</b> Both are raised by §9.6's saga with an authorisation standing
    /// and the workflow ending in cancellation — <b>not</b> both "on a
    /// cancellation arriving after an authorisation", which this said and
    /// which describes only this code. <see cref="PaymentAuthorisedDuringCompensation"/> is
    /// the authorisation arriving after compensation began, and on its decline
    /// and payment-timeout doors no cancellation has been sent at all. Two
    /// state-and-event pairs, one shared condition — and
    /// <c>ordering.OrderReviews</c> persists <c>(OrderId, Reason, RaisedAt)</c>
    /// and nothing else, so a single code makes the two indistinguishable by
    /// the time anyone reads the queue. The saga has usually finalised by then;
    /// its state is gone.
    /// <para>
    /// What separates them: this one means the order reached <c>Confirmed</c>,
    /// so <b>Shipping may still despatch it</b> and stopping that is the first
    /// step. <see cref="PaymentAuthorisedDuringCompensation"/> means compensation was already
    /// under way — there is no despatch to stop and a <c>ReleaseStock</c> is in
    /// flight. The runbook has always described these as two procedures; until
    /// this code existed it keyed them on a saga state nothing recorded.
    /// </para>
    /// </remarks>
    public const string CancelledAfterConfirmation = "cancelled_after_confirmation";
}
