namespace Common.Contracts.Ordering.V1;

/// <summary>
/// An order was cancelled (§3.2). Inventory and Payments both consume it —
/// stock that was held is released, an authorisation that was taken is voided.
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
