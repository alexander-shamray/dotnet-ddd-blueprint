namespace Common.Infrastructure.Redis;

/// <summary>
/// §8.1's third row: <c>SET key NX PX</c> with a token-checked release. The
/// TTL is mandatory and enforced here — a lock key without one is a memory
/// leak on a noeviction instance, which eventually stops writes entirely
/// (§8.1), so there is no overload without a duration.
/// </summary>
public interface IDistributedLockFactory
{
    /// <summary>
    /// Null when the lock is held elsewhere. Throws if Redis is unreachable —
    /// §8.1: fail the operation, never proceed unlocked.
    /// </summary>
    Task<IDistributedLock?> TryAcquireAsync(string name, TimeSpan duration, CancellationToken ct = default);
}

/// <summary>
/// A held lock. Disposing releases it, token-checked: a handle whose key has
/// expired and been re-acquired elsewhere releases nothing.
/// </summary>
public interface IDistributedLock : IAsyncDisposable
{
    string Name { get; }
}
