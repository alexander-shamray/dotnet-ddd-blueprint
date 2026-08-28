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
/// not, and why this pass deletes on age alone with no predicate to get wrong:
/// every row here records work that finished.
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

    private readonly IServiceScopeFactory _scopes;
    private readonly RetentionPolicy _policy;
    private readonly ILogger<RetentionPurgeService> _log;

    // Composed once from the registered tables, exactly as the dispatcher
    // composes its three. Instance fields rather than consts for that reason
    // and no other.
    private readonly string _outboxSql;
    private readonly string _inboxSql;
    private readonly string _idempotencySql;

    public RetentionPurgeService(
        IServiceScopeFactory scopes,
        OutboxTable outbox,
        InboxTable inbox,
        IdempotencyMarkerTable markers,
        RetentionPolicy policy,
        ILogger<RetentionPurgeService> log)
    {
        _scopes = scopes;
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

        // Age alone again, and for a stronger version of the inbox's reason:
        // a marker records a command that committed, so there is no unfinished
        // state a predicate could protect. What protects it is the window,
        // which RetentionPolicy refuses to set below the life of the Redis
        // claim it backs up (§8.5).
        _idempotencySql =
            $"""
            DELETE TOP (@BatchSize) FROM {markers.QualifiedName}
            WHERE CommittedAt < @Before;
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

        DateTimeOffset now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();

        int outbox = await DeleteAsync(connection, _outboxSql, now - _policy.OutboxWindow, ct);
        Purged(_log, outbox, "outbox", null);

        int inbox = await DeleteAsync(connection, _inboxSql, now - _policy.InboxWindow, ct);
        Purged(_log, inbox, "inbox", null);

        int idempotency = await DeleteAsync(
            connection,
            _idempotencySql,
            now - _policy.IdempotencyWindow,
            ct);
        Purged(_log, idempotency, "idempotency", null);

        return (outbox, inbox, idempotency);
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
        DateTimeOffset before,
        CancellationToken ct)
    {
        int total = 0;

        for (int batch = 0; batch < _policy.MaxBatchesPerPass; batch++)
        {
            // CommandDefinition, so the token reaches the command: with the
            // plain overload a shutdown cannot interrupt a blocked delete and
            // the host waits out the SQL timeout (§9.4).
            int deleted = await connection.ExecuteAsync(
                new CommandDefinition(
                    sql,
                    new { _policy.BatchSize, Before = before },
                    cancellationToken: ct));

            total += deleted;

            if (deleted < _policy.BatchSize)
                break;
        }

        return total;
    }
}
