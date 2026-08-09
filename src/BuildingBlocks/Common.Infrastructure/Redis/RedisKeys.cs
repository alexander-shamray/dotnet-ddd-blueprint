using Microsoft.Extensions.Hosting;

namespace Common.Infrastructure.Redis;

/// <summary>
/// §8.3's rule that a call site writes only half the key, applied to every
/// namespace in §8.1's table. The prefix is
/// <see cref="IHostEnvironment.ApplicationName"/> verbatim — the single
/// source §8.5's store and §13.2's <c>service.name</c> already use. Two
/// sources would let the Redis prefix and the telemetry label disagree,
/// which breaks correlation exactly when it is needed — and a wrong prefix
/// fails §8.1's ACL silently.
/// </summary>
/// <remarks>
/// There is deliberately no <c>Cache(string)</c> method: cache keys get
/// their prefix from <c>RedisCacheOptions.InstanceName</c>, and a method
/// building the full key would double-prefix the moment somebody passed its
/// result to <c>HybridCache</c>. The instance-name string is exposed
/// instead, so <c>:cache:</c> is spelled in exactly one place.
/// </remarks>
public sealed class RedisKeys(IHostEnvironment environment)
{
    private readonly string _service = environment.ApplicationName;

    /// <summary>"{service}:cache:" — consumed by <c>RedisCacheOptions</c> only.</summary>
    public string CacheInstanceName => $"{_service}:cache:";

    /// <summary>"{service}:lock:{name}" — the noeviction keyspace (§8.1).</summary>
    public string Lock(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return $"{_service}:lock:{name}";
    }

    /// <summary>"{service}:idem:{suffix}" — the noeviction keyspace (§8.1, §8.5).</summary>
    public string Idempotency(string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix);
        return $"{_service}:idem:{suffix}";
    }

    /// <summary>"{service}:denylist:{suffix}" — the noeviction keyspace (§8.1, §11.3).</summary>
    public string Denylist(string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix);
        return $"{_service}:denylist:{suffix}";
    }
}
