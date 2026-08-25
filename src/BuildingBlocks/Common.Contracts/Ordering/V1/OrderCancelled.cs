namespace Common.Contracts.Ordering.V1;

/// <summary>
/// An order was cancelled (§3.2). <b>Three services consume it</b> — Inventory
/// releases stock that was held, Payments voids an authorisation that was
/// taken, and Ordering's own fulfilment saga stops.
/// <para>
/// <b>Ordering consuming its own event is the entry that reads like a mistake
/// and is not.</b> §11.4's customer endpoint cancels the <c>Order</c>
/// AGGREGATE, and until §9.6's machine bound this event the saga went on
/// reserving stock and authorising a card for an order the customer had
/// already cancelled — the endpoint is the only way a customer's cancellation
/// reaches the workflow. A copy arriving after the saga has finalised
/// correlates to no instance, and what happens then depends on
/// <see cref="Origin"/>: the workflow's own echo is discarded in silence,
/// and anything else faults onto §13.6's pager rather than vanishing.
/// </para>
/// <para>
/// <b>This summary named two consumers until a review counted three.</b> §3.2
/// and <c>appendix-d-type-inventory.md</c> had both already recorded the
/// third; the type's own documentation — the one a consumer reads first — was
/// the site the reconciliation missed.
/// </para>
/// </summary>
/// <remarks>
/// <b><see cref="Reason"/> is a string code from <see cref="CancelReasons"/>,
/// never Ordering's <c>CancellationReason</c> enum.</b> An enum looks like a
/// primitive and is not: it would drag <c>Ordering.Domain</c> into every
/// consumer (§9.1) and pin its member names as wire format, so renaming one
/// becomes a breaking change to everybody. This is the same decision
/// <see cref="CancelOrder"/> takes on the way in, and the two vocabularies are
/// deliberately one — a cancellation caused by a timeout is reported with the
/// code the saga sent.
/// <para>
/// <b><see cref="Reason"/> does not identify the origin, which is what
/// <see cref="Origin"/> is for.</b> §11.4's endpoint parses the whole
/// <c>CancellationReasons</c> map, so a caller may send <c>payment_declined</c>
/// as readily as <c>customer_request</c>: the reason is what somebody
/// asserted, not where the request came from. §9.6's saga has to tell its own
/// echo from a cancellation it did not cause, and read <see cref="Reason"/>
/// for that until a review established the two are independent.
/// </para>
/// <para>
/// <b><see cref="Origin"/> is optional, and absent means "published before
/// this field existed" rather than "unknown origin".</b> §9.2 makes a new
/// optional field additive, so this is not a V2; ADR-026's ordering still
/// applies inside the one service, because a rolling deploy has instances
/// publishing this event before they populate it. A consumer must therefore
/// hold whatever it did before the field for the length of that deploy —
/// §9.6's saga discards on absent for exactly that reason — and that tolerance
/// is §15.5's expand phase with a contract phase owed, not a permanent
/// reading.
/// </para>
/// </remarks>
public sealed record OrderCancelled : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required string Reason { get; init; }

    /// <summary>
    /// Who asked — a <see cref="CancelOrigins"/> code. Not
    /// <c>required</c>, because an instance running the release before this
    /// field was populated publishes without it; see the remarks on this type
    /// for what a consumer owes the absent case.
    /// </summary>
    public string? Origin { get; init; }
}
