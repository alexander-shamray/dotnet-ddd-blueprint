namespace Common.Application;

public sealed class TransactionBehavior<TCommand, TResult>(IUnitOfWork unitOfWork, IDomainEventDispatcher domainEvents)
    : IPipelineBehavior<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<TResult> HandleAsync(TCommand command, NextDelegate<TResult> next, CancellationToken ct)
    {
        // Already inside a transaction (nested dispatch) — do not open another.
        if (unitOfWork.HasActiveTransaction)
            return await next();

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                TResult result = await next();

                // A handler that returns a failed Result has rejected the command.
                // Returning here skips both the staging and the save, so the
                // transaction commits nothing and no outbox row announces a state
                // change that did not happen. Result<T> derives from Result, so one
                // pattern covers every command shape without reflection.
                if (result is Result { IsFailure: true })
                    return result;

                // Stages outbox rows only — no handler runs here (§7.5).
                // Reactions happen after commit, driven by the outbox.
                await domainEvents.DispatchAsync(token);

                // Principle 3 (§2.3), asserted rather than trusted — §6.3 argues
                // why this is a runtime check and not an architecture test. After
                // dispatch, so the staged rows of a legitimate single-root command
                // are already in the tracker and not miscounted.
                if (unitOfWork.ModifiedAggregateCount > 1)
                    throw new InvariantViolationException(
                        $"{typeof(TCommand).Name} modified {unitOfWork.ModifiedAggregateCount} " +
                        "aggregate roots. One transaction, one aggregate (§2.3 principle 3) — " +
                        "the second aggregate should react to a domain event after commit (§7.5).");

                await unitOfWork.SaveChangesAsync(token);

                return result;
            },
            ct);
    }
}
