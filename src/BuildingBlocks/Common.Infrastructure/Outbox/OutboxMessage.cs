using System.Text.Json;
using Common.Application;
using Common.Contracts;

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
        DateTimeOffset now,
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
        return new OutboxMessage
        {
            MessageId = message is IIntegrationEvent e ? e.MessageId : Guid.CreateVersion7(),
            CorrelationId = message is IIntegrationEvent c ? c.CorrelationId : correlationId,
            MessageType = types.NameOf(message.GetType()),
            Payload = JsonSerializer.Serialize(message, message.GetType(), json.Options),
            Lane = lane,
            OccurredAt = now
        };
    }
}
