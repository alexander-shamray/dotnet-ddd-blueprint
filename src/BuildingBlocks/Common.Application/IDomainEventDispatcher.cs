namespace Common.Application;

public interface IDomainEventDispatcher
{
    /// <summary>
    /// Collects raised domain events and stages outbox rows for them — the
    /// allow-listed ones on the Broker lane, those with projection handlers on
    /// the Local lane. Runs no handlers. Called by TransactionBehavior inside
    /// the transaction, before SaveChanges.
    /// </summary>
    /// <remarks>
    /// The port arrived with PR-09 because §6.3's behaviour could not compile
    /// without it; everything §7.5 builds behind it — the collector, the
    /// registry, the real dispatcher — arrived with the outbox. No member of
    /// <em>this</em> interface names a domain type, which is why it drew no
    /// edge to <c>Common.Domain</c> on its own; the edge is
    /// <see cref="IDomainEventCollector"/>'s.
    /// </remarks>
    Task DispatchAsync(CancellationToken ct);
}
