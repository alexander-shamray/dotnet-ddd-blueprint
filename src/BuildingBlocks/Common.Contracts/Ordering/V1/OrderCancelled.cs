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
/// correlates to no instance and is discarded in silence.
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
/// </remarks>
public sealed record OrderCancelled : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required string Reason { get; init; }
}
