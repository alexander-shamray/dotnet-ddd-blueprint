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

    private readonly Lock _gate = new();
    private Task? _release;

    public string Name { get; } = name;

    public async ValueTask DisposeAsync()
    {
        // Every disposer awaits the SAME in-flight release: a flag would let
        // a second caller report success while the delete is still on the
        // wire — or after it failed. The task is the state, and it is only
        // put back to null after a failure, so a caller may retry: the
        // script is token-checked and idempotent, where a handle stuck on
        // "released" holds the lock to its TTL.
        Task release;
        lock (_gate)
        {
            _release ??= ReleaseAsync();
            release = _release;
        }

        try
        {
            await release;
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_release, release))
                    _release = null;
            }

            throw;
        }
    }

    private async Task ReleaseAsync() =>
        await redis.GetDatabase().ScriptEvaluateAsync(ReleaseScript, [(RedisKey)key], [(RedisValue)token]);
}
