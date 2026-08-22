using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Common.Infrastructure.Redis;

/// <summary>
/// §8.5's store, over the <b>coordination</b> connection rather than the cache
/// one. The distinction is the point: an idempotency claim on an
/// <c>allkeys-lru</c> instance is evicted under exactly the memory pressure
/// that makes a duplicate write hardest to reproduce (§8.1).
/// </summary>
/// <remarks>
/// <see cref="RedisKeys.Idempotency"/> supplies the <c>{service}:idem:</c>
/// prefix the ACL requires, from <c>ApplicationName</c> — the same single
/// source §13.2 stamps on every trace, so the Redis prefix and the telemetry
/// label cannot disagree (§8.3). The behaviour passes a key <em>shape</em> and
/// this class owns the keyspace; prefixing on both sides would double it.
/// </remarks>
internal sealed class RedisIdempotencyStore(
    [FromKeyedServices(RedisConnections.Coordination)] IConnectionMultiplexer redis,
    RedisKeys keys,
    ILogger<RedisIdempotencyStore> log)
    : IIdempotencyStore
{
    /// <summary>
    /// The value written on a claim, and what tells an unfinished attempt from
    /// a recorded outcome.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not valid JSON, which is what makes the test
    /// unambiguous.</b> Every payload this store holds is
    /// <c>JsonSerializer</c> output: a string arrives quoted, a number is
    /// digits, an object is braced, and the void case is the four characters
    /// <c>null</c>. No serialised value can spell this, so a payload can never
    /// be misread as an in-progress marker or the reverse. A sentinel that
    /// happened to be valid JSON would put that collision one unlucky result
    /// away.
    /// </remarks>
    private const string InProgressMarker = "in-progress";

    private static readonly Action<ILogger, string, Exception?> ReleaseFailed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(1, nameof(ReleaseFailed)),
            "Idempotency claim {Key} could not be released; it will expire with its retention.");

    public async Task<bool> TryClaimAsync(string key, TimeSpan retention, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);
        ct.ThrowIfCancellationRequested();

        // SET NX — one round trip, and the atomicity the port's contract names.
        // A read-then-write here would admit both callers of the race this
        // exists to let exactly one caller win.
        return await redis
            .GetDatabase()
            .StringSetAsync(keys.Idempotency(key), InProgressMarker, retention, When.NotExists);
    }

    public async Task<IdempotencyEntry?> GetAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        RedisValue value = await redis.GetDatabase().StringGetAsync(keys.Idempotency(key));

        if (!value.HasValue)
            return null;

        string stored = value.ToString();

        return stored == InProgressMarker
            ? new IdempotencyEntry(true, null)
            : new IdempotencyEntry(false, stored);
    }

    public async Task CompleteAsync(string key, string payload, TimeSpan retention, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);
        ct.ThrowIfCancellationRequested();

        // Unconditional, and re-arming the retention rather than preserving
        // what the claim had left. The claim's window measures how long an
        // attempt may run; this one measures how long the answer stays
        // replayable, and starting it at the commit is what makes the stated
        // 24 hours the retention a caller actually gets.
        //
        // **Unconditional also means UNOWNED, and that is #127.** Every claim
        // writes the same InProgressMarker, so this write cannot prove the
        // key it overwrites is still the one this attempt claimed. An attempt
        // that outlived its own claim clobbers whatever its successor put
        // there. Not reachable as shipped — the only caller passes a 24-hour
        // TTL and SET NX blocks a second claim while any value is present, so
        // it needs a handler running longer than a day — but nothing in the
        // port's contract says the retention must outlast a handler, and a
        // caller passing seconds gets the race with no diagnostic.
        //
        // RedisDistributedLock, one file over on this same connection, is
        // token-checked for exactly this reason. The asymmetry is the part
        // worth naming: a reader who has read that script will assume this
        // class does the same.
        await redis
            .GetDatabase()
            .StringSetAsync(keys.Idempotency(key), payload, retention);
    }

    public async Task ReleaseAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        // No ThrowIfCancellationRequested, and that is the one asymmetry in
        // this class. The behaviour already passes CancellationToken.None here
        // — the commonest reason to be releasing at all is the caller's own
        // cancellation, and honouring the token would abandon the release and
        // leak the claim for a day.
        // Unowned in the same way CompleteAsync is, and worse in kind: this
        // one DELETES. See #127 and the comment above — same reachability,
        // same remedy, which is a compare-and-delete on a per-claim token
        // exactly as RedisDistributedLock.ReleaseScript does it.
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(keys.Idempotency(key));
        }
        catch (RedisException e)
        {
            // Best-effort, and this is the site §8.5's callout names: the
            // behaviour calls this from a catch block before `throw;`, so an
            // exception raised here would replace the fault the caller was
            // already reporting with a Redis one — the original destroyed
            // rather than wrapped. Swallowing it in the behaviour would be a
            // silence with nothing to report it; here there is a logger.
            //
            // The cost of swallowing is bounded and worth stating: the claim
            // stays in progress, so every retry of this CommandId meets
            // ConcurrentRequestException until the retention expires.
            ReleaseFailed(log, key, e);
        }
    }
}
