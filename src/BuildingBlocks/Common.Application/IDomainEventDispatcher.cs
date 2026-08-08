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
    /// The port arrives with PR-09 because §6.3's behaviour cannot compile
    /// without it; everything §7.5 builds behind it — the collector, the
    /// registry, the real dispatcher — arrives with the outbox. No member
    /// names a domain type, which is why this file draws no edge to
    /// <c>Common.Domain</c> (see <see cref="IUnitOfWork"/> for the argument).
    /// </remarks>
    Task DispatchAsync(CancellationToken ct);
}
