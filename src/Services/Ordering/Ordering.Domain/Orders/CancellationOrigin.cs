namespace Ordering.Domain.Orders;

/// <summary>
/// Who asked for a cancellation. Carried on
/// <c>OrderCancelledDomainEvent</c> beside <see cref="CancellationReason"/>,
/// which says <em>why</em> and deliberately does not say <em>who</em>.
/// </summary>
/// <remarks>
/// <b>The two are independent, and reading one off the other is the defect
/// this type exists to close.</b> §11.4's endpoint parses the whole
/// <c>CancellationReasons</c> map, so a caller may cancel with
/// <c>payment_declined</c> as readily as with <c>customer_request</c> — the
/// reason is what the caller asserts, not where the request came from. §9.6's
/// saga needs the second question answered and had no field to read it from.
/// <para>
/// <b>Recorded rather than enforced.</b> No invariant in <c>Order</c> turns on
/// this value; it is provenance the aggregate is told and passes on, exactly
/// as <see cref="CancellationReason"/> is. What consumes it is
/// <c>OrderCancelled.Origin</c> (§9.1), and the one consumer that acts on it
/// is the saga's missing-instance branch.
/// </para>
/// </remarks>
public enum CancellationOrigin
{
    /// <summary>
    /// A request with a principal behind it — §11.4's endpoint, whether the
    /// caller is the customer or an operator holding <c>orders:admin</c>.
    /// </summary>
    /// <remarks>
    /// <b>The zero value, on <c>CommandOrigin.User</c>'s argument.</b> An
    /// origin nobody set therefore means "someone outside this workflow asked",
    /// which is the reading that makes the saga's missing-instance branch fail
    /// loudly rather than discard. Declaring <see cref="Workflow"/> first would
    /// make the silent answer the accident of declaration order.
    /// </remarks>
    User,

    /// <summary>
    /// §9.6's fulfilment saga compensating, over the broker, with no principal
    /// at all — the <c>CancelOrder</c> one of its own branches sent.
    /// </summary>
    /// <remarks>
    /// <b>Named for the actor and not for the ingress.</b>
    /// <c>CommandOrigin.System</c> one layer out names the same path by what it
    /// lacks — a principal — which is the right name for an authorisation
    /// decision and the wrong one on a contract three services read. What a
    /// consumer wants to know is that the fulfilment workflow asked, and this
    /// says so.
    /// </remarks>
    Workflow
}
