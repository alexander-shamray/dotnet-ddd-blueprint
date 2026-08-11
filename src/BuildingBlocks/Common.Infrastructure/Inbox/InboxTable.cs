namespace Common.Infrastructure.Inbox;

/// <summary>
/// Where this service's inbox lives (§9.5) — the outbox's sibling, in the same
/// schema and the same database, because database-per-service (§7.1) applies to
/// technical tables as much as business ones.
/// </summary>
/// <remarks>
/// <b>A second type rather than a second property on <see cref="Outbox.OutboxTable"/>,
/// and one literal rather than two registrations.</b> Folding the inbox name
/// into the outbox's type would leave a type whose name covers half its
/// contents; the risk that separating them introduces — two schema strings that
/// could drift apart — is closed by each service constructing both from one
/// local, not by there being one type.
/// <para>
/// Only <see cref="Messaging.RetentionPurgeService"/> reads this. The filter
/// (§9.5) writes through EF and never composes SQL, so it needs no schema: the
/// service's own <c>DbContext</c> already knows where the entity is mapped.
/// </para>
/// </remarks>
public sealed class InboxTable
{
    public InboxTable(string schema)
    {
        QualifiedName = SqlSchema.Qualify(schema, "InboxMessages", nameof(schema));
        Schema = schema;
    }

    public string Schema { get; }

    /// <summary>Schema-qualified and delimited, ready to interpolate.</summary>
    public string QualifiedName { get; }
}
