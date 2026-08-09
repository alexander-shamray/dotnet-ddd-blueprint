using StackExchange.Redis;

namespace Common.Infrastructure.Redis;

internal sealed class RedisDistributedLock(IConnectionMultiplexer redis, string key, string name, string token)
    : IDistributedLock
{
    // Delete only what this handle wrote. GET-compare-DEL as one script: the
    // check and the delete must be atomic, or a lock that expires between
    // them deletes the next holder's key — §8.1's failure with no error, no
    // log line, and two workers believing they own it.
    private const string ReleaseScript =
        """
        if redis.call('get', KEYS[1]) == ARGV[1] then
            return redis.call('del', KEYS[1])
        end
        return 0
        """;

    private int _released;

    public string Name { get; } = name;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) == 1)
            return;

        try
        {
            await redis.GetDatabase().ScriptEvaluateAsync(ReleaseScript, [(RedisKey)key], [(RedisValue)token]);
        }
        catch
        {
            // The release did not happen, or its outcome is unknown. Put the
            // handle back so the caller may retry: the script is token-checked
            // and idempotent, so retrying an unknown outcome is safe — where a
            // handle that stays "released" turns every later attempt into a
            // successful no-op and holds the lock to its TTL.
            Interlocked.Exchange(ref _released, 0);
            throw;
        }
    }
}
