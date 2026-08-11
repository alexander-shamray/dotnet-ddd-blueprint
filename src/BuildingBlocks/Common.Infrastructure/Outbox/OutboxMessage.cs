using System.Text.Json;
using Common.Application;
using Common.Contracts;
using Common.Domain;

namespace Common.Infrastructure.Outbox;

/// <summary>
/// The staging path's whole row (§9.4). Mapped by EF Core through a
/// configuration in the service's own Infrastructure assembly, where the
/// schema is already decided — nothing here names an EF type.
/// </summary>
/// <remarks>
/// <b>Two types map to this table, deliberately.</b> This one is written whole
/// through EF; <see cref="OutboxClaim"/> is the narrow projection the
/// dispatcher's <c>OUTPUT</c> clause returns. Collapsing them produces a class
/// whose <see cref="ProcessedAt"/> is always null on the read path and whose
/// <see cref="LastError"/> is never populated on the write path.
/// </remarks>
public sealed class OutboxMessage
{
    public long Id { get; private set; }

    public Guid MessageId { get; private set; }

    public Guid CorrelationId { get; private set; }

    public string MessageType { get; private set; } = null!;

    public string Payload { get; private set; } = null!;

    public OutboxLane Lane { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public int Attempts { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public static OutboxMessage Stage(
        object message,
        OutboxLane lane,
        Guid correlationId,
        MessageTypeMap types,
        OutboxJson json)
    {
        // One identity, not two. An integration event already carries its
        // MessageId and CorrelationId in the envelope the mapper filled in
        // (§9.3), and DeliverAsync copies the row's values onto the transport —
        // so minting a second GUID here would give the body one id and the
        // broker header another. The inbox dedupes on the transport id (§9.5),
        // which would then disagree with the id a support tool reads out of the
        // payload, and the only way to notice is to compare two logs.
        //
        // A Local-lane row carries a domain event, which has no envelope and
        // never reaches a broker, so the row mints its own id and takes the
        // caller's correlation.
        //
        // The lane decides which interface the payload has to satisfy, and
        // this is what makes §9.3's allow-list structural rather than a
        // convention. `Map` returns `object`, and the type map admits domain
        // events and contracts alike — so a mapper that returned the domain
        // event it was handed would stage it on the Broker lane and the
        // dispatcher would publish it. That is precisely the leak §5.5 forbids
        // and §12.4 asserts against, and until this guard existed the only
        // thing preventing it was the mapper being written correctly.
        if (lane is OutboxLane.Broker && message is not IIntegrationEvent)
            throw new InvalidOperationException(
                $"{message.GetType().Name} is not an {nameof(IIntegrationEvent)} and cannot be " +
                "staged on the Broker lane. A domain event reaching the broker is the leak the " +
                "§9.3 allow-list exists to prevent — map it to a contract first.");

        if (lane is OutboxLane.Local && message is not IDomainEvent)
            throw new InvalidOperationException(
                $"{message.GetType().Name} is not an {nameof(IDomainEvent)} and cannot be staged " +
                "on the Local lane, which carries this service's own events to its projection " +
                "handlers (§7.5).");

        return new OutboxMessage
        {
            MessageId = message is IIntegrationEvent e ? e.MessageId : Guid.CreateVersion7(),
            CorrelationId = message is IIntegrationEvent c ? c.CorrelationId : correlationId,
            MessageType = types.NameOf(message.GetType()),
            Payload = JsonSerializer.Serialize(message, message.GetType(), json.Options),
            Lane = lane,

            // The message's own timestamp, never the staging clock. §13.7
            // defines projection.lag as "event raised to projection applied",
            // and a row stamped at staging time silently drops the interval
            // between the two — small, but measured by the one metric whose
            // name says it is included.
            //
            // No fallback is needed and none is written: NameOf has already
            // thrown for anything the map does not hold, and the map admits
            // only these two interfaces (§9.4), so one of the two arms always
            // matches. A `now` parameter here would be dead weight the caller
            // still had to find a clock for.
            OccurredAt = message is IIntegrationEvent o
                ? o.OccurredAt
                : ((IDomainEvent)message).OccurredAt
        };
    }
}
