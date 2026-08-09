using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Common.Infrastructure.Redis;

/// <summary>
/// The coordination connection, never the cache one: a lock on an
/// allkeys-lru instance is evicted under exactly the memory pressure that
/// makes the failure hardest to reproduce (§8.1).
/// </summary>
internal sealed class RedisDistributedLockFactory(
    [FromKeyedServices(RedisConnections.Coordination)] IConnectionMultiplexer redis,
    RedisKeys keys)
    : IDistributedLockFactory
{
    public async Task<IDistributedLock?> TryAcquireAsync(string name, TimeSpan duration, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        ct.ThrowIfCancellationRequested();

        string key = keys.Lock(name);
        string token = Guid.CreateVersion7().ToString("N");

        bool acquired = await redis.GetDatabase().StringSetAsync(key, token, duration, When.NotExists);
        return acquired ? new RedisDistributedLock(redis, key, name, token) : null;
    }
}
