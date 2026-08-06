namespace Common.Domain;

/// <summary>
/// A record that something meaningful happened, in past tense (§5.1). Scoped to
/// one service and free to carry domain types — an integration event is a
/// different thing under a different set of rules (§5.5, §9.1).
/// </summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}

/// <summary>
/// Non-generic marker. Infrastructure filters EF's change tracker by it —
/// <c>Entries&lt;IHasDomainEvents&gt;()</c> in §7.5 — and the tracker holds
/// objects, not <c>AggregateRoot&lt;TId&gt;</c> for a known TId. Without a
/// non-generic interface to test against, those queries would have to know
/// every key type in the model.
/// </summary>
/// <remarks>
/// Missing from the base class, this fails silently and completely: the query
/// matches nothing, the collector returns empty, the dispatcher exits early,
/// and the command commits having staged no outbox rows at all — no projection,
/// no integration event, no saga start (§5.5).
/// </remarks>
public interface IHasDomainEvents
{
    IReadOnlyList<IDomainEvent> DomainEvents { get; }

    void ClearDomainEvents();
}
