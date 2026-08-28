namespace Common.Application;

/// <summary>
/// The durable half of §8.5, as a port. <see cref="IIdempotencyStore"/> claims
/// a key in Redis before any work starts; this one records — <i>inside the
/// command's own transaction</i> — that the work committed, so a later attempt
/// can tell a fault that rolled back from one that did not.
/// </summary>
/// <remarks>
/// <b>Both members run on the transaction's connection, and that is the whole
/// contract rather than an implementation note.</b> A marker read or written
/// anywhere else answers a different question: outside the transaction it is
/// another Redis claim in different clothes, and no ordering of two systems is
/// atomic with a SQL commit. §6.3's <c>TransactionBehavior</c> is therefore the
/// only caller, because it is the only code that holds the transaction open —
/// and an implementation that opens its own connection breaks this port's
/// guarantee silently, exactly as <see cref="IUnitOfWork.ExecuteRawAsync"/>'s
/// own comment describes one file over.
/// <para>
/// <b>The key is the one <see cref="IdempotencyBehavior{TCommand,TResult}"/>
/// already built</b>, carried across on <see cref="IdempotencyContext"/> rather
/// than rebuilt here. Rebuilding it would be a second specification of a value
/// whose shape §8.5 spends three callouts arguing — the subject segment, the
/// declared operation name — and the two copies would agree until one of those
/// arguments moved.
/// </para>
/// <para>
/// <b>No retention is passed, and the asymmetry with the Redis port is the
/// point.</b> Every entry in that store has a TTL, which is what bounds §8.5's
/// guarantee to the retention and lets a retry arriving after expiry run the
/// command a second time. A marker is a row: it survives until something
/// deletes it, and what deletes it is
/// <c>RetentionPurgeService</c> on a window the service chooses.
/// </para>
/// </remarks>
public interface IIdempotencyMarkerStore
{
    /// <summary>
    /// Whether a previous attempt under this key committed.
    /// </summary>
    /// <remarks>
    /// Read at the top of the transaction, before the handler runs, so a
    /// command that has already committed does no work at all rather than
    /// doing it and losing it to a constraint violation on the way out.
    /// </remarks>
    Task<bool> ExistsAsync(string key, CancellationToken ct);

    /// <summary>
    /// Records that the work under this key committed.
    /// </summary>
    /// <remarks>
    /// <b>Staged, not committed.</b> The row lands when the transaction does,
    /// which is what makes the marker exactly as durable as the aggregate it
    /// guards — and what makes a rolled-back attempt leave nothing behind.
    /// <para>
    /// The key is unique in the store, so two attempts that somehow reach this
    /// point concurrently produce a constraint violation rather than two rows.
    /// That is a backstop and not the mechanism: the Redis claim is what makes
    /// the concurrent case rare, and this is what stops it committing twice.
    /// </para>
    /// <para>
    /// <b>What that backstop is not is a good answer, and the residual is
    /// stated rather than dressed up.</b> The violation surfaces from
    /// <c>SaveChangesAsync</c> as a <c>DbUpdateException</c>, which §10.5's
    /// concurrency handler does not select — it takes the derived
    /// <c>DbUpdateConcurrencyException</c> — so the caller gets a 500 for a
    /// duplicate the platform correctly refused. It takes the atomic claim to
    /// have admitted two callers under one key before this is reachable at all,
    /// which is why it is left as a residual rather than given a handler: a
    /// third translation for a state §8.5 says cannot arise would be a claim
    /// about the mechanism that the mechanism denies.
    /// </para>
    /// </remarks>
    Task MarkAsync(string key, CancellationToken ct);
}
