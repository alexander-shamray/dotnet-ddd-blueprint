using System.Data;
using System.Text.Json;
using Common.Application;
using Dapper;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Common.Infrastructure.Outbox;

/// <summary>
/// §9.4's dispatcher: an atomic <b>claim</b> that leases a batch of rows, then
/// per-row <b>delivery</b> where each message succeeds or fails on its own.
/// </summary>
/// <remarks>
/// <b>Every row is delivered and accounted for independently.</b> Wrapping a
/// whole batch in one transaction is the obvious implementation and is wrong:
/// a single failing projection would roll back the batch and block every
/// healthy <c>Broker</c> row behind it, so a read-model bug in this service
/// would stop publishing to every other service. The lanes can only be alerted
/// on separately (§13.6) if they can actually fail separately.
/// </remarks>
public sealed class OutboxDispatcher : BackgroundService
{
    private const int MaxAttempts = 10;

    // Compiled once rather than parsed per call. CA1848 is enforced by ADR-019
    // and this loop runs twice a second — see §13.3's LoggingBehavior, which
    // takes the same shape for the same reason.
    private static readonly Action<ILogger, Exception?> ClaimFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1, nameof(ClaimFailed)),
            "Outbox claim failed; retrying next tick.");

    private static readonly Action<ILogger, Guid, string, int, int, Exception?> DeliveryFailed =
        LoggerMessage.Define<Guid, string, int, int>(
            LogLevel.Error,
            new EventId(2, nameof(DeliveryFailed)),
            "Outbox message {MessageId} on lane {Lane} failed, attempt {Attempt} of {Max}.");

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<OutboxDispatcher> _log;

    // Composed once from the registered table (§9.4 writes them against a
    // literal schema, which common code cannot). Instance fields rather than
    // consts for that reason and no other.
    private readonly string _claimSql;
    private readonly string _completeSql;
    private readonly string _failSql;

    public OutboxDispatcher(IServiceScopeFactory scopes, OutboxTable table, ILogger<OutboxDispatcher> log)
    {
        _scopes = scopes;
        _log = log;

        // Atomic claim: selects and leases in one statement, so two replicas
        // cannot take the same row. READPAST skips rows another replica holds.
        _claimSql =
            $"""
            WITH claimable AS (
                SELECT TOP (100) *
                FROM {table.QualifiedName} WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE ProcessedAt IS NULL
                    AND Attempts < @MaxAttempts
                    AND (LockedUntil IS NULL OR LockedUntil < SYSDATETIMEOFFSET())
                ORDER BY OccurredAt
            )
            UPDATE claimable
            SET LockedUntil = DATEADD(second, 60, SYSDATETIMEOFFSET())
            OUTPUT
                inserted.Id,
                inserted.MessageId,
                inserted.CorrelationId,
                inserted.MessageType,
                inserted.Payload,
                inserted.Lane,
                inserted.Attempts,
                inserted.OccurredAt;
            """;

        _completeSql =
            $"""
            UPDATE {table.QualifiedName}
            SET ProcessedAt = SYSDATETIMEOFFSET(), LockedUntil = NULL
            WHERE Id = @Id;
            """;

        // Increments the attempt counter and backs off exponentially by
        // pushing the lease forward. This is what makes the cap — and the
        // abandoned-row alert in §13.6 — reachable.
        _failSql =
            $"""
            UPDATE {table.QualifiedName}
            SET
                Attempts    = Attempts + 1,
                LastError   = LEFT(@Error, 2000),
                LockedUntil = DATEADD(
                    second,
                    POWER(2, CASE WHEN Attempts > 8 THEN 8 ELSE Attempts END) * 5,
                    SYSDATETIMEOFFSET())
            WHERE Id = @Id;
            """;
    }

    // stoppingToken, not ct: CA1725 requires an override to keep the base's
    // parameter name, and a reader consulting BackgroundService's
    // documentation is reading about that one (§7.2's ConfigureConventions
    // took the same correction rather than a suppression).
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(500));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The claim itself failed — database unreachable. Next tick.
                ClaimFailed(_log, ex);
            }
        }
    }

    /// <summary>
    /// One claim-and-deliver pass. Returns the number of rows completed.
    /// Public so tests drive it directly instead of racing a timer — §12.4.
    /// </summary>
    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        // The claim's own scope, holding nothing but the connection. Delivery
        // gets a scope per row below, so this one exists only to resolve the
        // factory — and the connection deliberately outlives those scopes,
        // because the claim, the completes and the fails are one row-keeping
        // conversation with the database rather than part of any delivery.
        await using AsyncServiceScope claimScope = _scopes.CreateAsyncScope();

        // Disposed every pass — the loop runs twice a second, so a leaked
        // connection here exhausts the pool within a minute.
        using IDbConnection connection =
            claimScope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().Create();

        // OutboxClaim, not OutboxMessage — the claim projects only the columns
        // the OUTPUT clause returns. See Appendix D.
        //
        // CommandDefinition, so the token reaches the database command: with
        // the plain overload a shutdown cannot interrupt a blocked claim, and
        // the host waits out the SQL command timeout before ExecuteAsync
        // returns. §6.5's read handlers pass it the same way.
        List<OutboxClaim> claimed =
        [
            .. await connection.QueryAsync<OutboxClaim>(
                new CommandDefinition(_claimSql, new { MaxAttempts }, cancellationToken: ct))
        ];

        int completed = 0;

        foreach (OutboxClaim message in claimed)
        {
            try
            {
                // A scope per row, not per batch, and this is what makes the
                // per-row isolation above true rather than merely intended.
                // Projection handlers are scoped and so is anything they
                // inject — a DbContext most of all — so one scope for a
                // hundred rows means a handler that throws mid-write hands
                // the next row its own tracked, half-mutated state. The row
                // that failed is then not the only row that fails, and the
                // §13.6 lane alerts stop meaning what they say.
                await using AsyncServiceScope delivery = _scopes.CreateAsyncScope();

                await DeliverAsync(delivery.ServiceProvider, message, ct);

                await connection.ExecuteAsync(
                    new CommandDefinition(_completeSql, new { message.Id }, cancellationToken: ct));
                completed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad message does not affect the other 99.
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        _failSql, new { message.Id, Error = ex.ToString() }, cancellationToken: ct));

                DeliveryFailed(_log, message.MessageId, message.Lane, message.Attempts + 1, MaxAttempts, ex);
            }
        }

        return completed;
    }

    private static async Task DeliverAsync(IServiceProvider sp, OutboxClaim message, CancellationToken ct)
    {
        // Through the map, not Type.GetType: the column holds a name this code
        // chose, and it has to survive the version bump of the assembly that
        // wrote it.
        Type type = sp.GetRequiredService<MessageTypeMap>().Resolve(message.MessageType);

        // The same registered instance Stage wrote through, converters
        // included — which is what "both sides must agree" means now that a
        // value object's shape depends on one (OutboxJson argues why).
        object payload = JsonSerializer.Deserialize(
            message.Payload,
            type,
            sp.GetRequiredService<OutboxJson>().Options)!;

        if (message.Lane == nameof(OutboxLane.Broker))
        {
            await sp.GetRequiredService<IPublishEndpoint>().Publish(payload, type, c =>
            {
                c.MessageId = message.MessageId;
                c.CorrelationId = message.CorrelationId;
            }, ct);
            return;
        }

        if (message.Lane != nameof(OutboxLane.Local))
            throw new InvalidOperationException(
                $"Outbox row {message.MessageId} carries lane '{message.Lane}', which is neither " +
                "Broker nor Local. The row is left for the §13.6 abandoned-row alert rather than " +
                "guessed at.");

        // Local lane: this service's own projection handlers, running safely
        // outside the write transaction that produced the event (§7.5).
        // OccurredAt comes from the row, not the payload: the invoker is
        // generic and unconstrained, so it has no typed access to a member the
        // payload may or may not have (§13.3). It is the time the aggregate
        // raised the event — Stage() is called inside the write transaction —
        // so the lag §13.7 measures includes the commit, which is the honest
        // reading of "how stale is this read model".
        await ProjectionInvoker.InvokeAllAsync(sp, payload, type, message.OccurredAt, ct);
    }
}
