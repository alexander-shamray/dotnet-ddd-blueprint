namespace Common.Domain;

/// <summary>
/// Non-generic marker, so the change tracker can count aggregate roots without
/// knowing their key type — <c>is IAggregateRoot</c> in §6.3's one-aggregate
/// assertion.
/// </summary>
public interface IAggregateRoot;

/// <summary>
/// The only entity outside code may hold a reference to, and the consistency
/// boundary a transaction is allowed to span (§5.1).
/// </summary>
/// <remarks>
/// Raising an event does not publish it. §7.5 is normative: the dispatcher runs
/// inside the transaction, before <c>SaveChanges</c>, and only stages outbox
/// rows. Everything that reacts runs after commit (ADR-018).
/// </remarks>
public abstract class AggregateRoot<TId>
    : Entity<TId>, IAggregateRoot, IHasDomainEvents
    where TId : struct
{
    private readonly List<IDomainEvent> _domainEvents = [];

    /// <summary>
    /// A read-only view, never the backing list. Handing out the list itself
    /// would let a caller stage an event the aggregate never raised, past every
    /// invariant the aggregate exists to hold.
    /// </summary>
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();

    /// <summary>
    /// Optimistic concurrency token, mapped to SQL Server <c>rowversion</c>.
    /// Empty rather than null on a new aggregate: EF assigns the real value on
    /// insert, and a null here would fault on the first update instead — a
    /// failure one write further from its cause.
    /// </summary>
    public byte[] Version { get; private set; } = [];
}
