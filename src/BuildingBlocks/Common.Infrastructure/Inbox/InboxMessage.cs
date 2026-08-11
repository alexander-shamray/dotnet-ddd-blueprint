namespace Common.Infrastructure.Inbox;

/// <summary>
/// §9.5's inbox row: one message, on one receive endpoint, already handled.
/// Mapped by each service's own <c>IEntityTypeConfiguration</c>, for the reason
/// <c>OutboxMessage</c> is — the entity is a building block and knows no
/// schema, and the schema is the service's.
/// </summary>
/// <remarks>
/// <b>The second key column is the receive endpoint, and that choice is the
/// whole point of the composite key.</b> One service can legitimately bind the
/// same message type on more than one endpoint — a normal-priority queue and a
/// bulk/replay queue, say — and each must process the message independently.
/// Keying on <c>MessageId</c> alone would let whichever finished first suppress
/// the other.
/// <para>
/// It is deliberately <em>not</em> the message type: a message has exactly one,
/// so <c>(MessageId, MessageType)</c> is functionally <c>(MessageId)</c> — a
/// composite key that looks meaningful and distinguishes nothing. Nor is it the
/// handler. <c>IntegrationEventConsumer&lt;T&gt;</c> (§9.4) runs every
/// registered handler for the message and one row covers them all, which is
/// correct because they succeed or fail together and are retried together.
/// </para>
/// </remarks>
public sealed class InboxMessage(Guid messageId, string endpoint, DateTimeOffset handledAt)
{
    public Guid MessageId { get; private set; } = messageId;

    public string Endpoint { get; private set; } = endpoint;

    public DateTimeOffset HandledAt { get; private set; } = handledAt;
}
