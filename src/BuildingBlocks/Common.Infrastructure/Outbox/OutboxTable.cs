namespace Common.Infrastructure.Outbox;

/// <summary>
/// Where this service's outbox lives. §9.4 writes its three statements against
/// <c>ordering.OutboxMessages</c>, which is right for a chapter about Ordering
/// and wrong for the common code every service shares — so the schema is a
/// registered value and the dispatcher composes its SQL from it.
/// </summary>
/// <remarks>
/// <b>The alternative is a dispatcher per service, which is §9.3's prohibition
/// on a second outbox table set arriving by the back door</b> — two
/// dispatchers, two retention policies, two sets of ordering guarantees, and
/// one of them being the one nobody monitors.
/// <para>
/// The table name is fixed and the schema is shape-checked by
/// <see cref="SqlSchema"/>, which <see cref="Inbox.InboxTable"/> shares: the
/// check, the 128-character bound and the bracket-quoting are one answer, not
/// two that agree today.
/// </para>
/// </remarks>
public sealed class OutboxTable
{
    public OutboxTable(string schema)
    {
        QualifiedName = SqlSchema.Qualify(schema, "OutboxMessages", nameof(schema));
        Schema = schema;
    }

    public string Schema { get; }

    /// <summary>Schema-qualified and delimited, ready to interpolate.</summary>
    public string QualifiedName { get; }
}
