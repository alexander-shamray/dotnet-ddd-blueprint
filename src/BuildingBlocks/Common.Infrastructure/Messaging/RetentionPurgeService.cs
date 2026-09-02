using System.Data;
using Common.Application;
using Common.Infrastructure.Idempotency;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Outbox;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Common.Infrastructure.Messaging;

/// <summary>
/// §9.4's, §9.5's and §8.5's retention purges, in one hosted service. An outbox
/// nobody prunes grows without bound and eventually degrades the filtered index
/// the dispatcher's claim depends on; an inbox nobody prunes grows for the life
/// of the service and its composite-key index degrades with it; and idempotency
/// markers accumulate one row per protected command, for ever.
/// </summary>
/// <remarks>
/// <b>One service covering every table, which is §9.5's shape</b> — "both purges
/// run from the same hosted service on a slow schedule, batched so neither holds
/// a long lock". The alternative is a hosted service per table with a schedule
/// each and one of them being the one nobody notices has stopped, which is
/// §9.3's argument against a second outbox mechanism wearing different clothes.
/// <para>
/// <b>The third table is not like the other two, and the difference is worth
/// carrying.</b> A purged outbox row loses a debugging record and a purged
/// inbox row loses a duplicate suppression the broker will not exercise again;
/// a purged idempotency marker loses a <em>correctness</em> property, because
/// it is what refuses a retry of a command that already committed. That is why
/// <see cref="RetentionPolicy.IdempotencyWindow"/> has a floor the other two do
/// not, and why its pass is the only one that asks something before deleting.
/// </para>
/// <para>
/// <b>Age is necessary there and no longer sufficient.</b> The other two
/// compare a column against a cutoff and are done; the marker's compares a
/// column against a cutoff to find <em>candidates</em>, asks
/// <see cref="Common.Application.IIdempotencyStore"/> which of them it has
/// already let go of, and then deletes under <em>two</em> predicates that
/// cover each other — no row newer than the newest it selected, and none
/// inside its own window. A key names a command rather than a row, so a retry
/// can commit a fresh marker under one this pass already chose, and no single
/// predicate excludes that in every direction the clock can move. Deleting on
/// age alone put the claim's window and the
/// marker's on two servers' clocks with nothing coupling their rates, so a
/// forward step of the database's deleted the row while the claim it backs up
/// was still live
/// (<see href="https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/171">#171</see>,
/// ADR-039). What replaces the comparison is the fact it stood in for.
/// </para>
/// </remarks>
public sealed class RetentionPurgeService : BackgroundService
{
    // Compiled once rather than parsed per call — CA1848 (ADR-019), and the
    // same shape §9.4's dispatcher takes for the same reason.
    // The generic arguments bind to the placeholders BY POSITION, not by the
    // delegate's parameter names — so `Define<string, int>` against
    // "{Rows} … {Table}" logged Rows="outbox" and Table=5, rendering "deleted
    // outbox row(s) from 5". Nothing fails, the structured fields are simply
    // transposed, and only reading the output shows it.
    private static readonly Action<ILogger, int, string, Exception?> Purged =
        LoggerMessage.Define<int, string>(
            LogLevel.Information,
            new EventId(1, nameof(Purged)),
            "Retention purge deleted {Rows} row(s) from {Table}.");

    private static readonly Action<ILogger, Exception?> PurgeFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(2, nameof(PurgeFailed)),
            "Retention purge failed; retrying next pass.");

    // Keys per DELETE, and the number is SQL Server's rather than this
    // platform's. Dapper expands `IN @Keys` into one parameter per element and
    // the server refuses a statement carrying more than 2,100 of them, so a
    // pass at the default BatchSize of 5,000 would fail on the batch rather
    // than on the configuration. Chunking here keeps BatchSize meaning what it
    // says — rows considered per batch — instead of quietly capping it at a
    // limit belonging to a different layer.
    //
    // Private because it is not a knob, and coupled to a test that says so.
    // `A_batch_spanning_more_than_one_delete_chunk_is_deleted_whole` stages one
    // more marker than this, in both service suites, and is the only case that
    // reaches the second chunk at all. RAISING THIS NUMBER ABOVE 1,001 MAKES
    // THAT TEST PASS WHILE COVERING NOTHING, so move it in the same change —
    // a gate that silently stops covering its surface is this repository's
    // most-repeated failure, and this comment is the half of the couple that
    // lives here.
    private const int KeysPerDelete = 1000;

    private readonly IServiceScopeFactory _scopes;
    private readonly IIdempotencyStore _claims;
    private readonly RetentionPolicy _policy;
    private readonly ILogger<RetentionPurgeService> _log;

    // Composed once from the registered tables, exactly as the dispatcher
    // composes its three. Instance fields rather than consts for that reason
    // and no other.
    private readonly string _outboxSql;
    private readonly string _inboxSql;
    private readonly string _idempotencyCandidateSql;
    private readonly string _idempotencyDeleteSql;

    public RetentionPurgeService(
        IServiceScopeFactory scopes,
        OutboxTable outbox,
        InboxTable inbox,
        IdempotencyMarkerTable markers,
        IIdempotencyStore claims,
        RetentionPolicy policy,
        ILogger<RetentionPurgeService> log)
    {
        _scopes = scopes;
        _claims = claims;
        _policy = policy;
        _log = log;

        // ProcessedAt IS NOT NULL is load-bearing, not defensive. Purging on
        // age alone would delete the abandoned rows — Attempts at the cap,
        // never processed — that §13.6's alert exists to surface, turning
        // permanent data loss into a clean, empty table. A container test
        // asserts an abandoned row survives a purge that removes a processed
        // one of the same age.
        //
        // The window is a parameter and only the table name is interpolated,
        // which is what OutboxTable's shape check is for.
        _outboxSql =
            $"""
            DELETE TOP (@BatchSize) FROM {outbox.QualifiedName}
            WHERE ProcessedAt IS NOT NULL
                AND ProcessedAt < @Before;
            """;

        // Age alone here, and the asymmetry is the point: an inbox row records
        // that a message was handled, so there is no unfinished state for a
        // predicate to protect. What protects it instead is the window itself,
        // which must outlast the broker's longest redelivery (§9.5).
        _inboxSql =
            $"""
            DELETE TOP (@BatchSize) FROM {inbox.QualifiedName}
            WHERE HandledAt < @Before;
            """;

        // The marker is TWO statements where the other two are one, and the
        // split is this pass's whole subject rather than a batching detail.
        // Age is still necessary on both of them and is no longer SUFFICIENT
        // on either: what decides is IIdempotencyStore.UnheldAsync agreeing
        // that the claim behind the row is gone (#171, ADR-039).
        //
        // The window alone used to decide it, and that put two clocks either
        // side of one comparison. Redis expires the claim after
        // IdempotencyRetention.Window elapsed by REDIS'S clock; this statement
        // deleted after IdempotencyWindow elapsed by SQL SERVER'S. Nothing
        // couples the two rates, so a forward step of the database's — an NTP
        // correction, a host migration, a resumed snapshot — carried the
        // cutoff past a marker whose claim was still live, and the retry after
        // that claimed a free key and ran a committed command a second time.
        // Two attempts to bound that with a margin are why there is no third:
        // a step is bounded by nothing this repository can assert, so the
        // purge asks the store that owns the claim instead of racing it.
        //
        // The cutoff is still computed HERE rather than by the caller, which
        // is where this pass departs from the two above and is #167's fix.
        // CommittedAt is written by a SYSDATETIMEOFFSET() column default, so
        // the row's own age is one clock's arithmetic whichever of §15.3's
        // three replicas wrote it — and that is now an ordering over ROWS
        // rather than against the claim: it decides which markers have served
        // their window, and the store decides which of those may go.
        //
        // The outbox and the inbox deliberately keep the parameterised form:
        // their windows are housekeeping, and a substitutable TimeProvider is
        // worth more there than a clock nothing can move (§9.5).
        //
        // Oldest first, so a batch that cannot be fully deleted leaves the
        // rows likeliest to still hold a claim — the newest — at the tail
        // where the pass stops rather than at the head where it would block.
        _idempotencyCandidateSql =
            $"""
            SELECT TOP (@BatchSize) [Key], CommittedAt FROM {markers.QualifiedName}
            WHERE CommittedAt < DATEADD(second, -@WindowSeconds, SYSDATETIMEOFFSET())
            ORDER BY CommittedAt;
            """;

        // Delimited, because Key is a reserved word in T-SQL and the column is
        // named for what it holds rather than around the parser.
        //
        // THE VERSION BOUND IS WHAT MAKES THIS SAFE, and neither a key alone
        // nor a re-evaluated age is. A key names a command, not a row: past the
        // guarantee the key is claimable again, so a retry can re-run the
        // command and commit a FRESH marker under the same key between this
        // pass's SELECT and its DELETE. §15.3 ships three replicas, so two
        // purgers can select the same row and the second one's delete arrives
        // after the first has removed it and after the replacement exists.
        //
        // A key-only delete removes that replacement — a row inside its window
        // with a live claim behind it — and the retry after that runs the
        // command a third time. **Repeating the age cutoff does not fix it
        // either**, which is the correction worth carrying: that predicate
        // re-reads SYSDATETIMEOFFSET(), so a forward step of the database's
        // clock before the stale delete makes the replacement look old enough
        // and it goes anyway. An age against a moving clock is not an ABA
        // guard, and this pull request exists because that clock moves.
        //
        // SO BOTH PREDICATES ARE HERE, and neither is redundant: each covers
        // the other's blind spot, and a replacement escapes only if it escapes
        // both.
        //
        // @SelectedThrough is the newest CommittedAt the SELECT actually
        // returned — captured from the rows in hand, so no later clock movement
        // changes it. A replacement committed after this pass selected is
        // stamped above it and is excluded, which is what covers a FORWARD
        // step: the age cutoff would have re-read a clock that had moved on and
        // let the row through.
        //
        // The age cutoff covers the other direction, and the reason is that a
        // row cannot be older than the window at the instant the same clock
        // stamps it. After a BACKWARD step the replacement's CommittedAt can
        // fall at or below @SelectedThrough — the bound alone would admit it —
        // but the cutoff moves back with the clock that stamped it, so the row
        // is never past its own window and survives. Under no step at all the
        // cutoff excludes it for the same reason.
        //
        // What that leaves is nothing this repository can name: a replacement
        // reaches this statement only by being both newer than every row the
        // SELECT returned and older than a window measured on the clock that
        // stamped it, and those cannot hold together for one row.
        _idempotencyDeleteSql =
            $"""
            DELETE FROM {markers.QualifiedName}
            WHERE [Key] IN @Keys
                AND CommittedAt <= @SelectedThrough
                AND CommittedAt < DATEADD(second, -@WindowSeconds, SYSDATETIMEOFFSET());
            """;
    }

    // stoppingToken, not ct: CA1725 requires an override to keep the base's
    // parameter name (ADR-019 makes it an error), and a reader consulting
    // BackgroundService's documentation is reading about that one.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(_policy.Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await PurgeAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Logged and swallowed, because an exception out of
                // ExecuteAsync stops the host: a database blip during
                // housekeeping must not take the service down. The token
                // rather than the type, for §9.4's reason — a cancellation
                // raised while the token is still live is a failure, not a
                // shutdown.
                PurgeFailed(_log, ex);
            }
        }
    }

    /// <summary>
    /// One purge pass over every table. Returns the rows deleted from each.
    /// Public so tests drive it directly instead of racing a timer — the same
    /// seam <c>OutboxDispatcher.ProcessBatchAsync</c> offers, for the same
    /// reason (§12.4).
    /// </summary>
    public async Task<(int Outbox, int Inbox, int Idempotency)> PurgeAsync(CancellationToken ct)
    {
        // One scope and one connection for the pass, disposed at the end of it.
        // The purge is housekeeping on this service's own database and shares
        // nothing with a command's transaction, which is why it goes through
        // the query-side port rather than through IUnitOfWork (§6.5, §9.6).
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();

        using IDbConnection connection =
            scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().Create();

        // Two of the three, and the third is named below. The registered clock
        // rather than DateTimeOffset.UtcNow, for §9.5's reason: a test host
        // substitutes it, and a row written on one clock and aged on another is
        // one no substituted clock can reason about.
        DateTimeOffset now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();

        int outbox = await DeleteAsync(
            connection,
            _outboxSql,
            new { _policy.BatchSize, Before = now - _policy.OutboxWindow },
            ct);
        Purged(_log, outbox, "outbox", null);

        int inbox = await DeleteAsync(
            connection,
            _inboxSql,
            new { _policy.BatchSize, Before = now - _policy.InboxWindow },
            ct);
        Purged(_log, inbox, "inbox", null);

        // The third takes no `now` at all — neither this pod's nor the
        // server's is compared against the claim any more. Its own method,
        // because selecting, asking and deleting is three steps where the two
        // above are one (#171, ADR-039).
        int idempotency = await PurgeMarkersAsync(connection, ct);
        Purged(_log, idempotency, "idempotency", null);

        return (outbox, inbox, idempotency);
    }

    /// <summary>
    /// §8.5's markers: the rows past their window whose claim the store has
    /// already let go. Selects, asks, deletes — and deletes nothing the store
    /// still holds a claim for.
    /// </summary>
    /// <remarks>
    /// <b>The store is asked rather than out-counted, and that is the whole
    /// of ADR-039.</b> A window compared against a window put Redis's clock on
    /// one side and SQL Server's on the other with nothing coupling their
    /// rates; asking the store that owns the claim replaces the comparison
    /// with the fact it was standing in for.
    /// </remarks>
    private async Task<int> PurgeMarkersAsync(IDbConnection connection, CancellationToken ct)
    {
        // The window as a duration and not a cutoff, because the statement
        // computes the cutoff from the server's own clock (#167, ADR-038). An
        // int rather than a long: RetentionPolicy caps a window at ten years,
        // which is 315,360,000 seconds, and DATEADD's argument is an int.
        //
        // Rounded UP, and a cast would have rounded down. The window is a
        // caller-supplied TimeSpan with sub-second resolution, so a cast sends
        // 24 hours for a configured 24 hours and 500 milliseconds — selecting
        // the marker fractionally before the window the operator asked for,
        // which is the one direction this setting may not be wrong in. Ceiling
        // keeps the row slightly longer than asked instead, which costs
        // nothing: the floor is a lower bound, so exceeding it is always
        // admissible.
        int windowSeconds = (int)Math.Ceiling(_policy.IdempotencyWindow.TotalSeconds);

        int total = 0;

        for (int batch = 0; batch < _policy.MaxBatchesPerPass; batch++)
        {
            MarkerCandidate[] candidates = [.. await connection.QueryAsync<MarkerCandidate>(
                new CommandDefinition(
                    _idempotencyCandidateSql,
                    new { _policy.BatchSize, WindowSeconds = windowSeconds },
                    cancellationToken: ct))];

            if (candidates.Length == 0)
                break;

            // The version bound, taken from the rows rather than from a clock.
            // ORDER BY CommittedAt makes the last one the newest, and the
            // delete below refuses anything above it — which is what stops a
            // replacement committed after this SELECT being deleted by it.
            DateTimeOffset selectedThrough = candidates[^1].CommittedAt;
            string[] keys = [.. candidates.Select(candidate => candidate.Key)];

            // Not caught, and the caller's `catch` is why that is safe: an
            // unreachable store leaves every marker in place and the pass is
            // logged and retried next interval. Treating a failed lookup as
            // "no claim" would delete the row that refuses a duplicate, which
            // is the failure this call exists to remove.
            IReadOnlyCollection<string> unheld = await _claims.UnheldAsync(keys, ct);

            int deleted =
                await DeleteKeysAsync(connection, unheld, selectedThrough, windowSeconds, ct);
            total += deleted;

            // Two ways to stop and both are needed. A short SELECT means the
            // table is drained of candidates. A batch the store would not let
            // go of entirely means the next SELECT returns those same rows —
            // they are ordered oldest first and nothing about them has changed
            // — so continuing would re-read and re-ask for no deletions. The
            // ceiling above bounds the pass either way, and an hour later the
            // claim these rows are waiting on has usually gone.
            if (candidates.Length < _policy.BatchSize || deleted < candidates.Length)
                break;
        }

        return total;
    }

    /// <summary>
    /// Deletes the given keys that are still past their window, chunked to stay
    /// inside SQL Server's parameter limit. Returns the rows actually removed.
    /// </summary>
    /// <remarks>
    /// <b>Two predicates bound it and each covers the other's blind spot.</b>
    /// A key names a command and not a row, so a retry can commit a fresh
    /// marker under a key this pass has already selected — and neither a
    /// key-only delete, nor the selection's own timestamp bound, nor a re-read
    /// age cutoff excludes it alone. See the statement's own comment; this is
    /// why the count returned can be lower than the number of keys handed in.
    /// </remarks>
    private async Task<int> DeleteKeysAsync(
        IDbConnection connection,
        IReadOnlyCollection<string> keys,
        DateTimeOffset selectedThrough,
        int windowSeconds,
        CancellationToken ct)
    {
        int deleted = 0;

        foreach (string[] chunk in keys.Chunk(KeysPerDelete))
        {
            deleted += await connection.ExecuteAsync(
                new CommandDefinition(
                    _idempotencyDeleteSql,
                    new
                    {
                        Keys = chunk,
                        SelectedThrough = selectedThrough,
                        WindowSeconds = windowSeconds,
                    },
                    cancellationToken: ct));
        }

        return deleted;
    }

    /// <summary>
    /// Deletes in batches until one comes back short or the pass's ceiling is
    /// reached. A short batch means the table is drained; the ceiling means the
    /// backlog is larger than one pass should hold a lock for, and the next
    /// pass continues it.
    /// </summary>
    private async Task<int> DeleteAsync(
        IDbConnection connection,
        string sql,
        object parameters,
        CancellationToken ct)
    {
        int total = 0;

        for (int batch = 0; batch < _policy.MaxBatchesPerPass; batch++)
        {
            // CommandDefinition, so the token reaches the command: with the
            // plain overload a shutdown cannot interrupt a blocked delete and
            // the host waits out the SQL timeout (§9.4).
            int deleted = await connection.ExecuteAsync(
                new CommandDefinition(sql, parameters, cancellationToken: ct));

            total += deleted;

            if (deleted < _policy.BatchSize)
                break;
        }

        return total;
    }

    /// <summary>
    /// One row of the marker pass's selection: the key to delete by, and the
    /// <c>CommittedAt</c> that identifies the row rather than the command.
    /// </summary>
    /// <remarks>
    /// A record rather than the bare key, because the key alone cannot tell a
    /// marker from its own replacement — which is the whole of the delete's
    /// version bound.
    /// </remarks>
    private sealed record MarkerCandidate(string Key, DateTimeOffset CommittedAt);
}
