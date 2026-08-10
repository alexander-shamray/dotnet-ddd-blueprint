namespace Common.Infrastructure.Outbox;

/// <summary>
/// Dapper projection of the claim's <c>OUTPUT</c> clause. Read-only, and its
/// members must match that clause exactly — Dapper binds by name and leaves an
/// unmatched member at its default, so a column added here and not there is a
/// <c>DateTimeOffset.MinValue</c> nobody notices until a metric reads 55 years.
/// </summary>
/// <remarks>
/// <c>Lane</c> is a string rather than <c>OutboxLane</c> because it is what
/// the column holds and what §9.4's delivery branches on. Dapper would in fact
/// convert it, and that is the reason to be explicit: the conversion throws on
/// a value the enum has no member for, on the dispatcher, once per row, where
/// a lane column that has drifted deserves the row's own error field rather
/// than a claim that fails the whole batch.
/// </remarks>
public sealed record OutboxClaim(
    long Id,
    Guid MessageId,
    Guid CorrelationId,
    string MessageType,
    string Payload,
    string Lane,
    int Attempts,
    DateTimeOffset OccurredAt);
