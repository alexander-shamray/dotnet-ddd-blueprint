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
/// <para>
/// <b>It is stamped by the <em>database</em>, and the default parameter is how
/// the caller says so.</b> The column carries a
/// <c>SYSDATETIMEOFFSET()</c> default and EF omits it from the insert while the
/// property holds its sentinel — so
/// <c>new IdempotencyMarker(key)</c> ages the row on the same clock the purge
/// reads its cutoff from, and no pod's <c>TimeProvider</c> is party to it
/// (<see href="https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/167">#167</see>,
/// ADR-038). Passing a value explicitly still writes it, which is what lets a
/// fixture stage a marker at a controlled age; that is the only caller that
/// should.
/// </para>
/// </remarks>
public sealed class IdempotencyMarker(string key, DateTimeOffset committedAt = default)
{
    public string Key { get; private set; } = key;

    public DateTimeOffset CommittedAt { get; private set; } = committedAt;
}
