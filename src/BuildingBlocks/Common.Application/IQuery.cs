namespace Common.Application;

/// <summary>
/// A request that reads. Separate from <see cref="ICommand{TResult}"/> so the
/// behaviours that belong to the write path can be constrained away from the
/// read path (§6.3) — a query must never open a transaction.
/// </summary>
public interface IQuery<out TResult>;

/// <summary>Handles exactly one query type.</summary>
public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken ct);
}
