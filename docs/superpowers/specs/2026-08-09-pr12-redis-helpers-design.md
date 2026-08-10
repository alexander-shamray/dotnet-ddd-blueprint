# PR-12 — Redis helpers: HybridCache, key namespaces, distributed locks

Design spec, frozen at write time. Appendix C names the PR
(`feat(common): Redis helpers — HybridCache, key namespaces, distributed
locks`, depends 06 and 08) and its deliverables: key-naming helper, mandatory
TTL enforced in code, the `{service}:cache|lock|idem|denylist:` namespaces,
§8.1's eviction-policy isolation, and Testcontainers Redis tests. §8 is the
chapter this PR implements.

## What this PR is

The shared Redis mechanism every later consumer builds on: the two-connection
registration of §8.1/§8.2, the key-naming authority of §8.3, and the
distributed lock of §8.1's third row. No service consumes any of it yet — the
first consumers arrive with the outbox's cache invalidation (PR-14/§8.4) and
§8.5's idempotency store — and that is the same shape as PR-04, which shipped
the dispatcher three PRs before the first service existed. The helpers are
proven by their own test suite against a real Redis, not by a service.

## Where it lives — `Common.Infrastructure` is created here

**Decision.** This PR creates `src/BuildingBlocks/Common.Infrastructure`, the
fourth of §4.1's five building blocks, holding a single `Redis/` folder.
PR-14's outbox joins it later.

**Why.** The PR is `feat(common)`; §4.1's tree row already reads
`Common.Infrastructure/ Outbox, inbox, EF conventions, Redis`; and the
scaffold argument decides the rest — a per-service copy of `AddRedisConnections`
would be N copies of wiring that drift, in a repo whose scaffold exists to keep
wiring in one place. §8.2's sample currently annotates the helper
`// Ordering.Infrastructure`, which contradicts §4.1's row; the chapter is
amended, not the tree.

**Consequences.** `Common.Infrastructure` takes **no project references**. No
member of the Redis helpers names a domain or application type, and an unused
project reference is a claim about the dependency graph that nothing makes
true — the same argument that keeps `Common.Application ↛ Common.Domain` today.
PR-14's `IDomainEventCollector` work is what draws edges here.

## The API

All types in `namespace Common.Infrastructure.Redis`.

### `RedisConnections`

Static class holding the two keyed-service names Appendix D already reserves:

```csharp
public static class RedisConnections
{
    public const string Cache = "RedisCache";
    public const string Coordination = "RedisCoordination";
}
```

The values are spelled identically to the configuration keys
(`ConnectionStrings:RedisCache`, `ConnectionStrings:RedisCoordination` — §14.1
sets them, Aspire derives them) so one name means one connection everywhere.

### `RedisKeys`

The key-naming authority — §8.3's rule that a call site writes only half the
key, applied to all four namespaces. Prefix comes from
`IHostEnvironment.ApplicationName` **verbatim**: one source, no normalisation,
the same source §8.5's store and §13.2's `service.name` already use. §8.3's
lowercase examples are illustrative — they show a service whose
`ApplicationName` is `catalog`.

```csharp
public sealed class RedisKeys(IHostEnvironment environment)
{
    public string CacheInstanceName { get; }          // "{app}:cache:" — consumed by RedisCacheOptions only
    public string Lock(string name);                  // "{app}:lock:{name}"
    public string Idempotency(string suffix);         // "{app}:idem:{suffix}"
    public string Denylist(string suffix);            // "{app}:denylist:{suffix}"
}
```

There is deliberately **no `Cache(string)` method**: cache keys get their
prefix from `RedisCacheOptions.InstanceName`, and a method that built the full
key would produce a double-prefixed key the moment somebody passed it to
`HybridCache`. The type exposes the instance-name string instead, so `:cache:`
has exactly one spelling in the codebase.

Suffixes are validated non-null and non-whitespace. Colons inside a suffix are
legal — `{Command}:{CommandId}` is §8.5's own shape.

### `IDistributedLockFactory` / `IDistributedLock`

§8.1's third row: `SET key NX PX` plus token-checked release.

```csharp
public interface IDistributedLockFactory
{
    /// <summary>Null when the lock is held elsewhere. Throws if Redis is unreachable —
    /// §8.1: fail the operation, do not proceed unlocked.</summary>
    Task<IDistributedLock?> TryAcquireAsync(string name, TimeSpan duration, CancellationToken ct = default);
}

public interface IDistributedLock : IAsyncDisposable
{
    string Name { get; }
}
```

`RedisDistributedLockFactory` (sealed, registered by `AddRedisConnections`,
holds the **coordination** connection and `RedisKeys`):

- Acquire: `StringSetAsync(key, token, duration, When.NotExists)`; the token is
  `Guid.CreateVersion7().ToString("N")`.
- **Mandatory TTL enforced in code**: `duration` must be greater than zero or
  the call throws `ArgumentOutOfRangeException` before any I/O. There is no
  overload without a duration.
- Release (`DisposeAsync`): a Lua compare-and-delete — delete only if the
  stored value equals this handle's token. A handle whose key has expired and
  been re-acquired by another holder releases nothing; that is the whole point
  of the token. Dispose is idempotent.
- Redis unavailability propagates as the client's exception on both acquire
  and release. The lock never fails open.

No auto-renewal, no waiting/retry loop, no reentrancy — none is specified in
§8, and each is easy to add behind the same interface if a consumer ever
argues for it.

### `AddRedisConnections(this IServiceCollection, IConfiguration)`

One registration helper, everything in it:

1. Reads `ConnectionStrings:RedisCache` and `ConnectionStrings:RedisCoordination`
   **eagerly** and throws `InvalidOperationException` naming the missing key —
   PR-08's precedent: a host missing its connection string must not start.
2. Registers two **keyed singleton** `IConnectionMultiplexer`s (lazy connect,
   `AbortOnConnectFail = false` so a Redis that is down at startup degrades per
   §8.1 rather than killing the host — readiness checks are what report it).
3. `AddStackExchangeRedisCache` whose `ConnectionMultiplexerFactory` returns
   the keyed **cache** connection — one connection per instance, not a private
   third one inside `IDistributedCache`; the traced connection is then the one
   the cache actually uses. `InstanceName` is set from
   `RedisKeys.CacheInstanceName` via an options `Configure` dependency.
4. `AddHybridCache` with §8.2's defaults: L2 ten minutes, L1 one minute,
   `MaximumPayloadBytes = 1024 * 1024`.
5. Registers `RedisKeys` and `IDistributedLockFactory` as singletons.
6. Registers the Redis **tracing instrumentation** (next section).

The helper requires a host environment (`IHostEnvironment` in the container),
which every Api and Worker host has. Migrators never call it — §4.1's narrowest
row.

## Instrumentation moves here — §13.2 is amended

**Decision.** `AddRedisInstrumentation()` is registered inside
`AddRedisConnections`, via `ConfigureRedisInstrumentation` handing both keyed
connections to the instrumentation. It does not go into `Common.Web`'s
`AddObservability`, where §13.2's sample currently sketches it.

**Why.** Two reasons, and the chapter's own rule is the first: "an
instrumentation lands with the package it instruments", and the package that
owns the connections is `Common.Infrastructure`. Putting the call in
`Common.Web` would hand `StackExchange.Redis` transitively to every host —
including Catalog.Api, which has no Redis — exactly the "claim about the
dependency graph that is not yet true" §13.2 warns about. The second reason is
mechanical: the connections are **keyed** services, and the parameterless
`AddRedisInstrumentation()` can only discover an unkeyed `IConnectionMultiplexer`
— in `Common.Web` it would silently instrument nothing.

**Consequences.** §13.2's sample block loses the `.AddRedisInstrumentation()`
line; its prose gains where the call landed and why; the matching comment in
`ObservabilityExtensions.cs` is updated. `AddOpenTelemetry().WithTracing(...)`
called from `AddRedisConnections` merges with the host's own configuration; in
a host that never calls `AddObservability` there is no exporter and the
registration is inert.

## What this PR does not do

- **No service wiring.** Catalog does not call `AddRedisConnections`, gains no
  Redis env vars, no Redis readiness checks, and no cached query. Caching a
  read before ADR-018's invalidation machinery exists (PR-14) would teach the
  defect §8.4 exists to prevent. Consequence: the compose comment on
  `catalog-api` claiming "HybridCache … arrive[s] with PR-13" is corrected —
  Redis keys join with the PR whose code first reads them.
- **No `IIdempotencyStore` implementation** — §8.5 keeps its seat comment; the
  store and behaviour land together in §8.5's PR, now with `RedisKeys` ready
  for it.
- **No denylist consumer** — `RedisKeys.Denylist` exists because the namespace
  table names it; §11's PR consumes it.
- **No rate-limit keyspace** — §8.1 reserves it deliberately; reserving is
  prose, not code.

## Tests — `tests/Common.Infrastructure.Tests`

New xunit.v3 + Shouldly project, the third that needs Docker. Same policy as
PR-08: no skip and no category when the daemon is absent — a broken Docker must
fail, not pass.

**Unit half (no container):**

- `RedisKeys`: all four shapes against a fake `IHostEnvironment`; verbatim
  prefix; empty/whitespace suffix throws.
- Lock guards: non-positive duration throws `ArgumentOutOfRangeException`
  before any Redis call (substitute multiplexer proves no I/O).
- `AddRedisConnections` registration surface, asserted on the
  `IServiceCollection` without building a provider: both keyed descriptors,
  `RedisKeys`, `IDistributedLockFactory`, `HybridCache`, `IDistributedCache`.
- Missing connection string: each key's absence throws at **registration**,
  naming the key.

**Container half (Testcontainers.Redis, one `IntegrationCollection`, one
container shared by the assembly):**

- Lock lifecycle: acquire → contend returns null → dispose → re-acquire
  succeeds; expiry frees the lock without a release.
- **Token-checked release**: A acquires with a short TTL, the key expires, B
  acquires; disposing A's stale handle must not release B's lock.
- HybridCache round-trip through the real registration: factory executes once
  for two `GetOrCreateAsync` calls; the key in Redis carries the
  `{app}:cache:` prefix **and a positive TTL** — the §8.3 and mandatory-TTL
  claims asserted against the server, not the code.
- Tag invalidation: `RemoveByTagAsync` re-runs the factory — §8.4's mechanism
  proven on the pinned package now, not at PR-14.
- One tracing test: with an in-memory exporter, a cache operation produces a
  Redis client span — proving the keyed-connection instrumentation wiring
  §13.2 now claims.

## Packages

Already pinned: `StackExchange.Redis`, `Microsoft.Extensions.Caching.Hybrid`,
`OpenTelemetry.Instrumentation.StackExchangeRedis`, `Testcontainers.Redis`.
New pins, added to Appendix B in the same change (all MIT, Microsoft):

- `Microsoft.Extensions.Caching.StackExchangeRedis` — `AddStackExchangeRedisCache`
  and `RedisCacheOptions`.
- `Microsoft.Extensions.Hosting.Abstractions` — `IHostEnvironment` for
  `RedisKeys`, in a project that is neither a web project nor a job host.
- `Microsoft.Extensions.Options` only if the compiler shows a direct use;
  otherwise it stays transitive.

## Blueprint reconciliation, same PR

- **§8.1**: the lock row's placement cell drops "or a separate Redis DB index
  with its own policy" — `maxmemory-policy` is per-instance in Redis; a DB
  index cannot carry its own eviction policy, so the alternative it offers is
  not real. Compose (two instances) and §14.2's Aspire sample already comply.
  The "helper library enforces" sentence gains the enforcing types' names.
- **§8.2**: `AddRedisConnections` home comment → `Common.Infrastructure`; the
  cache registration shown reusing the keyed connection via
  `ConnectionMultiplexerFactory`; `InstanceName` sourced from `RedisKeys`;
  a line for the instrumentation registration.
- **§8.3**: names `RedisKeys` as the coordination-half helper.
- **§8.5**: `RedisIdempotencyStore` sample injects `RedisKeys` instead of
  hand-building the prefix, and its comment adjusts.
- **§13.2**: sample and prose per the instrumentation decision;
  `ObservabilityExtensions.cs`'s comment likewise.
- **Appendix B**: new package rows.
- **Appendix D**: rows for `RedisKeys`, `IDistributedLockFactory`,
  `RedisDistributedLockFactory` and the handle; `RedisConnections` and
  `AddRedisConnections` rows updated if their D.5 descriptions no longer hold.
- **`deploy/compose/docker-compose.yml`**: the catalog-api env comment's PR
  pairing corrected (no service or stanza changes).
- **CLAUDE.md**: phase section — PR-12 landed, PR-13 next; the building blocks
  are four of five; `Common.Infrastructure` in both trees; the Docker-needing
  test projects are three; test counts.
- **`docs/roadmap.md`**: no edit expected — M2's completion claim is PR-11's
  and M3 is not complete; verified rather than assumed at the end.

## Risks

- `ConfigureRedisInstrumentation`'s `(IServiceProvider, instrumentation)`
  overload and `HybridCache` tag removal are both verified against the pinned
  packages by compiling and by the container suite; if either is missing at
  this pin, the design point moves (instrumentation could fall back to
  explicit `AddConnection` at first resolve; the tag test would become the
  finding, not the feature).
- `Microsoft.Extensions.Caching.StackExchangeRedis` 10.0.x version is
  confirmed at restore against the NuGet feed.
