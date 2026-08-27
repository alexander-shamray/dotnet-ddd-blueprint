using Microsoft.Extensions.Hosting;

namespace Common.Infrastructure.Redis;

/// <summary>
/// §8.3's rule that a call site writes only half the key, applied to the
/// cache and coordination namespaces of §8.1's table — the ratelimit and
/// denylist keyspaces stay reservations there, so no member spells either.
/// The prefix is
/// <see cref="IHostEnvironment.ApplicationName"/> verbatim — the single
/// source §8.5's store and §13.2's <c>service.name</c> already use. Two
/// sources would let the Redis prefix and the telemetry label disagree,
/// which breaks correlation exactly when it is needed — and a wrong prefix
/// fails §8.1's ACL silently, which is why §8.1 provisions the ACL pattern
/// from the same value: a host deployed as <c>Ordering.Api</c> takes
/// <c>~Ordering.Api:*</c>.
/// </summary>
/// <remarks>
/// There is deliberately no <c>Cache(string)</c> method: cache keys get
/// their prefix from <c>RedisCacheOptions.InstanceName</c>, and a method
/// building the full key would double-prefix the moment somebody passed its
/// result to <c>HybridCache</c>. The instance-name string is exposed
/// instead, so <c>:cache:</c> is spelled in exactly one place.
/// <para>
/// <b><c>Denylist</c> used to be a member and is not one now.</b> It was the
/// one reservation this type spelled, and the summary above stated the rule it
/// broke — a contradiction inside one file. Nothing ever read the keyspace:
/// §11.3's <c>AddJwtBearer</c> validates a token locally and consults no
/// revocation list, so the member's only effect was to make a control that
/// does not exist read as one that does.
/// <c>ADR-033</c> supersedes ADR-006 on this point and records the bounded
/// revocation window the platform accepts instead. The keyspace keeps its row
/// in §8.1's table, on the terms <c>ratelimit</c> already has: reserved, so
/// that a denylist built later lands on the noeviction instance rather than
/// somewhere a value can be evicted out from under it.
/// </para>
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
}
