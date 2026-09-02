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
/// it is in the inbox. It is the column <c>RetentionPurgeService</c> compares
/// against its window — <b>to select candidates, and no longer to decide
/// them</b>. Since ADR-039 a row goes only when that age has passed
/// <em>and</em> <c>IIdempotencyStore</c> reports the claim behind its key gone,
/// so what bounds §8.5's guarantee unconditionally is the Redis claim's own
/// life and the configured window is a target above it. This paragraph said
/// the window bounded the guarantee "rather than by a Redis TTL", which was
/// true of the mechanism ADR-037 shipped and is the wrong way round for this
/// one.
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
/// <para>
/// <b>The row also carries a <c>rowversion</c>, and it is a shadow property on
/// purpose.</b> <c>RetentionPurgeService</c>'s delete names the rows its select
/// returned rather than describing them, and <see cref="RowVersionColumn"/> is
/// what identifies one: unique and monotonic per database, immutable for the
/// life of a row nothing updates, and reading no clock at all
/// (<see href="https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/173">#173</see>,
/// ADR-041). <c>CommittedAt</c> went on doing the job until that issue, and it
/// held the row's identity <em>by construction</em> — nothing enforces
/// uniqueness on a <c>datetimeoffset(7)</c>, so a replacement stamped at the
/// selected row's exact tick was matched and deleted with its claim live.
/// </para>
/// <para>
/// <b>No CLR property backs it, because nothing in C# reads it through EF:</b>
/// the one reader is the purge's own SQL, over Dapper, which does not consult
/// this model. A property here would be a mutable array on a public type that
/// exists to be ignored — and <see cref="RowVersionColumn"/> is what keeps the
/// mapping and that SQL from drifting apart, since each service declares the
/// shadow property itself the way it declares its schema.
/// </para>
/// </remarks>
public sealed class IdempotencyMarker(string key, DateTimeOffset committedAt = default)
{
    /// <summary>
    /// The <c>rowversion</c> column's name, and the shadow property's — they
    /// are the same string because EF names the column after the property, and
    /// naming it once is what lets a service's
    /// <c>IEntityTypeConfiguration</c> and <c>RetentionPurgeService</c>'s
    /// statements agree without either restating the other.
    /// </summary>
    public const string RowVersionColumn = "RowVersion";

    public string Key { get; private set; } = key;

    public DateTimeOffset CommittedAt { get; private set; } = committedAt;
}
