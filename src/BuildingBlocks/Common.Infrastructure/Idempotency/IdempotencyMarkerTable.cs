namespace Common.Infrastructure.Idempotency;

/// <summary>
/// Where this service's idempotency markers live (§8.5) — the outbox's and the
/// inbox's sibling, in the same schema and the same database, because
/// database-per-service (§7.1) applies to technical tables as much as business
/// ones.
/// </summary>
/// <remarks>
/// <b>A third type rather than a third property on one of the other two, on
/// <see cref="Inbox.InboxTable"/>'s own argument</b>: folding a table name into
/// a type named for a different table leaves a type whose name covers part of
/// its contents. What keeps the three schemas from drifting is that each
/// service builds all three from one local, not that there is one type.
/// <para>
/// Only <see cref="Messaging.RetentionPurgeService"/> reads this.
/// <see cref="EfIdempotencyMarkerStore"/> writes through EF and never composes
/// SQL, so it needs no schema — the service's own <c>DbContext</c> already
/// knows where the entity is mapped, which is also what puts its write inside
/// the command's transaction.
/// </para>
/// </remarks>
public sealed class IdempotencyMarkerTable
{
    public IdempotencyMarkerTable(string schema)
    {
        QualifiedName = SqlSchema.Qualify(schema, "IdempotencyMarkers", nameof(schema));
        Schema = schema;
    }

    public string Schema { get; }

    /// <summary>Schema-qualified and delimited, ready to interpolate.</summary>
    public string QualifiedName { get; }
}
