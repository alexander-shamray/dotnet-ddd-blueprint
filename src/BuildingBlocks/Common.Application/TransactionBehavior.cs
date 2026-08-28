namespace Common.Application;

public sealed class TransactionBehavior<TCommand, TResult>(
    IUnitOfWork unitOfWork,
    IDomainEventDispatcher domainEvents,
    IIdempotencyMarkerStore markers,
    IdempotencyContext idempotency)
    : IPipelineBehavior<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<TResult> HandleAsync(TCommand command, NextDelegate<TResult> next, CancellationToken ct)
    {
        // Already inside a transaction (nested dispatch) — do not open another.
        if (unitOfWork.HasActiveTransaction)
            return await next();

        // Read ONCE, here, and never again inside the unit below. A nested
        // dispatch would run its own IdempotencyBehavior and overwrite the
        // context while this transaction is open, so re-reading after next()
        // would mark the inner command's key against the outer command's rows.
        // Nothing here dispatches a command from a command handler and a gate
        // per service says so (§8.5); this capture is what keeps the day that
        // gate fails a stopped build rather than a wrong marker.
        string? key = idempotency.Key;

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                // §8.5's durable half, and the reason it is read BEFORE the
                // handler rather than after: an attempt whose commit landed and
                // whose acknowledgement was lost released its Redis claim on
                // the way out, so this retry holds a fresh claim over work that
                // is already done. The marker is the only thing that knows, and
                // knowing before next() means the duplicate costs a lookup
                // instead of a rolled-back handler.
                if (key is not null && await markers.ExistsAsync(key, token))
                    throw new CommandAlreadyCommittedException(key);

                TResult result = await next();

                // A handler that returns a failed Result has rejected the command.
                // Returning here skips both the staging and the save, so the
                // transaction commits nothing and no outbox row announces a state
                // change that did not happen. Result<T> derives from Result, so one
                // pattern covers every command shape without reflection.
                //
                // The marker is not written on this path either, and that is the
                // same decision rather than a second one: a refusal commits
                // nothing, so there is nothing for a later attempt to be refused
                // over.
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
                {
                    throw new InvariantViolationException(
                        $"{typeof(TCommand).Name} modified {unitOfWork.ModifiedAggregateCount} " +
                        "aggregate roots. One transaction, one aggregate (§2.3 principle 3) — " +
                        "the second aggregate should react to a domain event after commit (§7.5).");
                }

                // Staged last and committed with everything else, which is what
                // makes the marker exactly as durable as the rows it guards. It
                // is deliberately after the aggregate-count check: a command
                // this transaction is about to refuse must leave nothing behind
                // that would refuse its retry.
                if (key is not null)
                    await markers.MarkAsync(key, token);

                await unitOfWork.SaveChangesAsync(token);

                return result;
            },
            ct);
    }
}
