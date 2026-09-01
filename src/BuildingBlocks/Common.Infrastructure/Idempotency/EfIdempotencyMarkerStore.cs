using Common.Application;
using Microsoft.EntityFrameworkCore;

namespace Common.Infrastructure.Idempotency;

/// <summary>
/// §8.5's marker store over the service's own <c>DbContext</c>, which is what
/// puts both members inside the command's transaction.
/// </summary>
/// <remarks>
/// <b>Common, not per-service, for <c>InboxFilter</c>'s reason</b>: nothing
/// here is a service's, so six copies would be six places for the one thing
/// that matters — which connection the write lands on — to be got wrong.
/// <para>
/// <b>The <c>DbContext</c> must be the service's own, resolved and not
/// constructed</b>, exactly as §9.5's filter requires: each service registers
/// <c>AddScoped&lt;DbContext&gt;(sp =&gt; sp.GetRequiredService&lt;XDbContext&gt;())</c>,
/// and that alias is what makes this type see the transaction §6.3 opened.
/// <c>AddScoped&lt;DbContext, XDbContext&gt;()</c> compiles and resolves and is
/// wrong — it builds a second context in the same scope, and a marker
/// committed in its own transaction is a marker that survives the rollback of
/// the work it claims to record. That failure is worse here than in the inbox:
/// there a stray row suppresses one redelivery, here it refuses every later
/// attempt at a command that never committed.
/// </para>
/// <para>
/// <b>Dapper on <see cref="IUnitOfWork.ExecuteRawAsync"/> was the alternative
/// and is not better.</b> That port is the sanctioned in-transaction raw write
/// and would have kept the table out of the EF model — but it has no read, so
/// closing the loop would have meant widening §6.3's port for one caller, and
/// the table would then need hand-written DDL in a migration EF had no reason
/// to emit. §7.2's rule is that an entity is mapped by an
/// <c>IEntityTypeConfiguration</c>, and §7.4 already classifies the inbox and
/// the outbox as technical tables mapped that way for exactly this reason.
/// </para>
/// </remarks>
public sealed class EfIdempotencyMarkerStore(DbContext db) : IIdempotencyMarkerStore
{
    public Task<bool> ExistsAsync(string key, CancellationToken ct) =>
        // A query and never the change tracker, and the reason is what a
        // tracker read would MEAN rather than a hazard it would hit today. This
        // attempt stages its own marker under the key it reads here, so an
        // implementation consulting local state first would be answering
        // "has this scope decided to write one" instead of "did an earlier
        // attempt commit one" — a different question that happens to give the
        // right answer.
        //
        // Two things keep the wrong question from biting, and both are
        // somebody else's to keep: §6.3 reads before it stages, and
        // EfUnitOfWork clears the tracker at the top of every retry. Neither is
        // this type's invariant, which is the whole argument for not depending
        // on them. AnyAsync against a DbSet always goes to the database.
        db.Set<IdempotencyMarker>().AnyAsync(marker => marker.Key == key, ct);

    public async Task MarkAsync(string key, CancellationToken ct)
    {
        // Staged, not saved. §6.3 calls SaveChangesAsync one line later inside
        // the same transaction, so the row lands with the aggregate or not at
        // all — which is the whole property this type exists for.
        //
        // No timestamp, and this type took a TimeProvider to supply one until
        // #167. The row's age was then the purging pod's clock minus a
        // timestamp the writing pod stamped, across three replicas (§15.3), so
        // the marker's retention floor had to bound the skew between them
        // rather than remove it. The column now defaults to
        // SYSDATETIMEOFFSET() and the purge computes its cutoff in SQL, so
        // both ends of the comparison are the database's clock and there is no
        // skew term left to bound (ADR-038). Constructing the marker without a
        // timestamp is what leaves the column to that default: EF omits a
        // property still holding its sentinel from the insert.
        await db.Set<IdempotencyMarker>().AddAsync(new IdempotencyMarker(key), ct);
    }
}
