namespace Common.Infrastructure.Idempotency;

/// <summary>
/// §8.5's durable half: one command, under one idempotency key, whose work
/// committed. Mapped by each service's own <c>IEntityTypeConfiguration</c>, for
/// the reason <c>InboxMessage</c> is — the entity is a building block and knows
/// no schema, and the schema is the service's.
/// </summary>
/// <remarks>
/// <b>The key is the whole primary key, and it is already scoped by
/// construction.</b> §8.5 builds <c>{subject}:{operation}:{commandId}</c>, so
/// the caller, the operation and the attempt are all inside the one column —
/// there is no second column that would distinguish anything, which is the test
/// <c>InboxMessage</c>'s composite key passes and this one does not.
/// <para>
/// <b><c>CommittedAt</c> exists for the purge and for nothing else.</b> Nothing
/// reads it to decide whether a command ran: presence is the answer, exactly as
/// it is in the inbox. It is the column
/// <c>RetentionPurgeService</c> compares against its window, and it is why the
/// guarantee §8.5 opens with is now bounded by that window rather than by a
/// Redis TTL.
/// </para>
/// </remarks>
public sealed class IdempotencyMarker(string key, DateTimeOffset committedAt)
{
    public string Key { get; private set; } = key;

    public DateTimeOffset CommittedAt { get; private set; } = committedAt;
}
