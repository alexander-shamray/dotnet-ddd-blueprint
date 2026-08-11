namespace Common.Contracts.Payments.V1;

/// <summary>
/// A payment was authorised (§3.2). The saga confirms the order on it (§9.6),
/// carrying the reference through to <c>ConfirmOrder</c>.
/// </summary>
/// <remarks>
/// <see cref="Reference"/> is an opaque provider token and stays a string
/// everywhere it travels — Ordering's <c>PaymentReference</c> value object is a
/// domain type, and a contract may not name one (§9.1).
/// </remarks>
public sealed record PaymentAuthorised : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }

    public required string Reference { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }
}

/// <summary>
/// A payment was refused (§3.2). The saga compensates on it — stock is released
/// and the order is cancelled with <c>CancelReasons.PaymentDeclined</c> (§9.6).
/// </summary>
/// <remarks>
/// <b><see cref="Reason"/> is the provider's, and it is deliberately not a
/// closed vocabulary.</b> The cancellation codes are this platform's and are
/// enumerated (<c>CancelReasons</c>); a decline reason comes from a PSP whose
/// set neither Payments nor this document controls, so pinning one here would
/// be a contract that a provider's release can break. It is carried for a human
/// reading the payment, never branched on — and never used as a metric
/// dimension, which is the tag-explosion rule §9.8 states for
/// <c>command.domain_rejected</c>.
/// </remarks>
public sealed record PaymentDeclined : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }

    public required string Reason { get; init; }
}

/// <summary>
/// A payment was refunded (§3.2). Notifications is its only consumer, and that
/// is the whole reason it is a published contract rather than an internal
/// event — §3.2 closes in both directions, and a published event nobody reads
/// looks identical to one whose consumer was forgotten.
/// </summary>
public sealed record PaymentRefunded : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }

    public required string Reference { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }
}
