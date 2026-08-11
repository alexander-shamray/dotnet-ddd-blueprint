using Common.Domain;

namespace Common.Application;

/// <summary>
/// §7.5's port over the change tracker. The dispatcher needs to know which
/// aggregates changed, which is EF Core's concern; Application sees only this.
/// </summary>
/// <remarks>
/// <b>This is the type that draws <c>Common.Application → Common.Domain</c>.</b>
/// PR-08's <see cref="IUnitOfWork"/> and PR-09's
/// <see cref="IDomainEventDispatcher"/> both declined to, because no member of
/// either names a domain type — <c>ModifiedAggregateCount</c> is an
/// <c>int</c> and the <c>is IAggregateRoot</c> test it comes from lives in
/// Infrastructure. <see cref="CollectAndClear"/> returns
/// <c>IReadOnlyList&lt;IDomainEvent&gt;</c> and cannot be written without the
/// reference, which is exactly the condition those two files predicted.
/// </remarks>
public interface IDomainEventCollector
{
    /// <summary>
    /// Returns the domain events raised by every tracked aggregate and clears
    /// them, so a second call after re-entrant work returns only new events.
    /// </summary>
    IReadOnlyList<IDomainEvent> CollectAndClear();
}
