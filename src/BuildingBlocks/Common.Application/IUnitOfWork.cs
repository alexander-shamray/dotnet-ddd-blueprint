namespace Common.Application;

/// <summary>
/// The command transaction boundary. Implemented in Infrastructure over the
/// service DbContext; Application never sees EF Core.
/// </summary>
/// <remarks>
/// No member names a domain type, which is why this file does not draw the
/// <c>Common.Application → Common.Domain</c> edge — and neither does §6.3's
/// <c>TransactionBehavior</c>, which reads
/// <see cref="ModifiedAggregateCount"/> as an <c>int</c> and never sees the
/// <c>IAggregateRoot</c> the count is derived from. Deriving it behind the port
/// is what keeps EF's change tracker, and the domain interface it tests for, on
/// Infrastructure's side of §4.2. The edge waits for the first Application type
/// that really does name a domain type — §7.5's <c>IDomainEventCollector</c>,
/// which returns <c>IReadOnlyList&lt;IDomainEvent&gt;</c>.
/// </remarks>
public interface IUnitOfWork
{
    bool HasActiveTransaction { get; }

    /// <summary>Distinct aggregate roots with pending changes.</summary>
    int ModifiedAggregateCount { get; }

    /// <summary>
    /// Runs <paramref name="operation"/> inside one atomic unit, retrying the
    /// whole unit on transient faults. Persists aggregate changes, domain-event
    /// side effects and outbox rows together, or none of them.
    /// </summary>
    Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken ct);

    Task<int> SaveChangesAsync(CancellationToken ct);

    /// <summary>
    /// Raw SQL on the transaction's own connection, for the rare table with no
    /// aggregate behind it (§9.6's OrderReviews). A command handler must not
    /// open its own connection — that write would commit outside this
    /// transaction.
    /// </summary>
    Task ExecuteRawAsync(string sql, object parameters, CancellationToken ct);
}
