# PR-12 Redis Helpers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `Common.Infrastructure` with §8's Redis mechanism — two keyed
connections, HybridCache, the `RedisKeys` naming authority and a token-checked
distributed lock — proven by a Testcontainers suite, with the blueprint
reconciled in the same PR.

**Architecture:** A new building block with no project references, one `Redis/`
folder, one registration entry point (`AddRedisConnections`). The Redis tracing
instrumentation registers there, not in `Common.Web` (keyed connections are
invisible to the parameterless overload). No service is wired.

**Tech Stack:** .NET 10 / C# 14, StackExchange.Redis 2.9.11,
Microsoft.Extensions.Caching.Hybrid 10.0.0, xunit.v3 + Shouldly + NSubstitute,
Testcontainers.Redis.

## Global Constraints

- House C# dialect (CLAUDE.md): explicit types unless the RHS names the type;
  spread over `.ToArray()`; file-scoped namespaces; blank line after
  `namespace X;`; four-space indent, CRLF, newline at EOF; British spelling in
  comments, identifiers keep their real spelling; no `#pragma`.
- `TreatWarningsAsErrors` — the build is the style gate; a warning is a stop.
- Central package management: never a `Version=` on a `PackageReference`; a new
  pin reaches `appendix-b-licences.md` in the same change or the licence gate
  fails.
- Blueprint reconciliation is part of the PR, not a follow-up: every chapter
  claim the code contradicts is amended in the same task that contradicts it.
- Docker required for the container tests; no skip, no category (PR-08 policy).
- Commits are semantic with arguing bodies; end with the Co-Authored-By /
  Claude-Session trailers used by this session's earlier commits.

---

### Task 1: Common.Infrastructure project, `RedisConnections`, `RedisKeys`

**Files:**
- Modify: `Directory.Packages.props` (three pins in the Runtime group, one in Test)
- Modify: `docs/backend-architecture/appendix-b-licences.md` (register the new identities)
- Create: `src/BuildingBlocks/Common.Infrastructure/Common.Infrastructure.csproj`
- Create: `src/BuildingBlocks/Common.Infrastructure/Redis/RedisConnections.cs`
- Create: `src/BuildingBlocks/Common.Infrastructure/Redis/RedisKeys.cs`
- Create: `tests/Common.Infrastructure.Tests/Common.Infrastructure.Tests.csproj`
- Create: `tests/Common.Infrastructure.Tests/RedisKeysTests.cs`
- Modify: `Platform.slnx` (both projects, alphabetical within their folders)

**Interfaces:**
- Produces: `Common.Infrastructure.Redis.RedisConnections.Cache = "RedisCache"`,
  `.Coordination = "RedisCoordination"`; `RedisKeys(IHostEnvironment)` with
  `string CacheInstanceName { get; }`, `string Lock(string name)`,
  `string Idempotency(string suffix)`, `string Denylist(string suffix)`.

- [ ] **Step 1: Pins.** In `Directory.Packages.props` Runtime group add (versions
  confirmed by restore; 10.0.0 expected):

```xml
<PackageVersion Include="Microsoft.Extensions.Caching.StackExchangeRedis" Version="10.0.0" />
<PackageVersion Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.0" />
<PackageVersion Include="Microsoft.Extensions.Options" Version="10.0.0" />
```

  and in the Test group (`ConfigurationBuilder` + `AddInMemoryCollection` for
  the suite's composition helper):

```xml
<PackageVersion Include="Microsoft.Extensions.Configuration" Version="10.0.0" />
```

  Each pin carries a short comment in the file's established voice saying what
  names it (e.g. Options: `AddOptions<RedisCacheOptions>().Configure` is called
  directly, so the assembly is referenced directly). Add the four backticked
  identities to Appendix B — extend the framework row that already carries
  `Microsoft.Extensions.Caching.Hybrid`, or the row grouping the other
  `Microsoft.Extensions.*` identities; run the gate's tests later to prove it.

- [ ] **Step 2: Project files.**

`src/BuildingBlocks/Common.Infrastructure/Common.Infrastructure.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    Settings come from Directory.Build.props (§4.4).

    No project references, deliberately: nothing in the Redis helpers names a
    domain or an application type, and an unused project reference is a claim
    about the dependency graph that nothing makes true — the same argument
    that keeps Common.Application ↛ Common.Domain today. PR-14's outbox is
    what draws edges here.
  -->

  <ItemGroup>
    <PackageReference Include="StackExchange.Redis" />
    <PackageReference Include="Microsoft.Extensions.Caching.Hybrid" />
    <PackageReference Include="Microsoft.Extensions.Caching.StackExchangeRedis" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Options" />
    <!-- §13.2's rule: an instrumentation lands with the package it
         instruments. The Redis one lands here, not in Common.Web — the
         connections are keyed services, invisible to the parameterless
         overload, and a host with no Redis must not carry the client. -->
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" />
    <PackageReference Include="OpenTelemetry.Instrumentation.StackExchangeRedis" />
  </ItemGroup>

</Project>
```

`tests/Common.Infrastructure.Tests/Common.Infrastructure.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <!-- xUnit v3 test projects are self-executing: the runner is compiled in
         rather than loaded by a host, so the assembly has to be an Exe. -->
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="Shouldly" />
    <PackageReference Include="NSubstitute" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Configuration" />
    <PackageReference Include="Testcontainers.Redis" />
    <PackageReference Include="OpenTelemetry.Exporter.InMemory" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\BuildingBlocks\Common.Infrastructure\Common.Infrastructure.csproj" />
  </ItemGroup>

</Project>
```

Solution entries: `Common.Infrastructure` between `Common.Domain` and
`Common.Web` in `/src/BuildingBlocks/`; `Common.Infrastructure.Tests` between
`Common.Domain.Tests` and `Common.Web.Tests` in `/tests/`.

- [ ] **Step 3: `RedisConnections`.**

```csharp
namespace Common.Infrastructure.Redis;

/// <summary>
/// The keyed-service names for §8.1's two connections — cache and
/// coordination, separate because the eviction policies cannot be shared.
/// The values are spelled exactly like the configuration keys
/// (<c>ConnectionStrings:RedisCache</c>, §14.1) so that one name means one
/// connection in the container, the configuration and the Compose file alike.
/// </summary>
public static class RedisConnections
{
    public const string Cache = "RedisCache";
    public const string Coordination = "RedisCoordination";
}
```

- [ ] **Step 4: Failing tests for `RedisKeys`.** `RedisKeysTests.cs` — a fake
  `IHostEnvironment` lives in the test file for now (moved to its own file in
  Task 3 when a second class needs it):

```csharp
using Common.Infrastructure.Redis;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

public sealed class RedisKeysTests
{
    private static readonly RedisKeys Keys = new(new TestEnvironment("catalog"));

    [Fact]
    public void Lock_key_carries_the_service_prefix_and_the_lock_namespace() =>
        Keys.Lock("reprice").ShouldBe("catalog:lock:reprice");

    [Fact]
    public void Idempotency_key_uses_the_idem_namespace() =>
        Keys.Idempotency("PlaceOrderCommand:0195e4b2").ShouldBe("catalog:idem:PlaceOrderCommand:0195e4b2");

    [Fact]
    public void Denylist_key_uses_the_denylist_namespace() =>
        Keys.Denylist("jti:abc").ShouldBe("catalog:denylist:jti:abc");

    [Fact]
    public void Cache_prefix_is_exposed_as_an_instance_name_not_a_key_builder() =>
        Keys.CacheInstanceName.ShouldBe("catalog:cache:");

    [Fact]
    public void The_prefix_is_ApplicationName_verbatim_with_no_normalisation() =>
        new RedisKeys(new TestEnvironment("Catalog.Api")).Lock("x").ShouldBe("Catalog.Api:lock:x");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_suffix_is_rejected(string suffix)
    {
        Should.Throw<ArgumentException>(() => Keys.Lock(suffix));
        Should.Throw<ArgumentException>(() => Keys.Idempotency(suffix));
        Should.Throw<ArgumentException>(() => Keys.Denylist(suffix));
    }
}
```

`TestEnvironment` (same file until Task 3):

```csharp
internal sealed class TestEnvironment(string applicationName) : IHostEnvironment
{
    public string ApplicationName { get; set; } = applicationName;
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ContentRootPath { get; set; } = string.Empty;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
```

- [ ] **Step 5: Run and watch them fail** (`RedisKeys` does not exist):
  `dotnet test tests/Common.Infrastructure.Tests --filter RedisKeysTests`

- [ ] **Step 6: Implement `RedisKeys`.**

```csharp
using Microsoft.Extensions.Hosting;

namespace Common.Infrastructure.Redis;

/// <summary>
/// §8.3's rule that a call site writes only half the key, applied to every
/// namespace of §8.1's table. The prefix is <see cref="IHostEnvironment.ApplicationName"/>
/// verbatim — the single source §8.5's store and §13.2's <c>service.name</c>
/// already use; two sources would let the Redis prefix and the telemetry
/// label disagree, which breaks correlation exactly when it is needed.
/// </summary>
/// <remarks>
/// There is deliberately no <c>Cache(string)</c> method: cache keys get their
/// prefix from <c>RedisCacheOptions.InstanceName</c>, and a method building
/// the full key would double-prefix the moment somebody passed its result to
/// <c>HybridCache</c>. The instance-name string is exposed instead, so
/// <c>:cache:</c> is spelled in exactly one place.
/// </remarks>
public sealed class RedisKeys(IHostEnvironment environment)
{
    private readonly string _service = environment.ApplicationName;

    /// <summary>"{service}:cache:" — consumed by RedisCacheOptions only.</summary>
    public string CacheInstanceName => $"{_service}:cache:";

    /// <summary>"{service}:lock:{name}" — noeviction keyspace (§8.1).</summary>
    public string Lock(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return $"{_service}:lock:{name}";
    }

    /// <summary>"{service}:idem:{suffix}" — noeviction keyspace (§8.1, §8.5).</summary>
    public string Idempotency(string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix);
        return $"{_service}:idem:{suffix}";
    }

    /// <summary>"{service}:denylist:{suffix}" — noeviction keyspace (§8.1, §11.3).</summary>
    public string Denylist(string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix);
        return $"{_service}:denylist:{suffix}";
    }
}
```

- [ ] **Step 7: Full build + the new tests green.**
  `dotnet build Platform.slnx` then the filter run above.

- [ ] **Step 8: Commit** — `feat(common): Common.Infrastructure and the Redis key namespaces`
  with a body arguing the no-project-references decision and the verbatim
  ApplicationName prefix.

---

### Task 2: The distributed lock

**Files:**
- Create: `src/BuildingBlocks/Common.Infrastructure/Redis/IDistributedLockFactory.cs`
  (interface + `IDistributedLock` in one file — one contract, two views)
- Create: `src/BuildingBlocks/Common.Infrastructure/Redis/RedisDistributedLockFactory.cs`
- Create: `src/BuildingBlocks/Common.Infrastructure/Redis/RedisDistributedLock.cs`
- Create: `tests/Common.Infrastructure.Tests/DistributedLockTests.cs` (unit half)

**Interfaces:**
- Consumes: `RedisKeys.Lock(string)`, `RedisConnections.Coordination` (Task 1).
- Produces: `IDistributedLockFactory.TryAcquireAsync(string name, TimeSpan duration, CancellationToken ct = default)`
  → `Task<IDistributedLock?>`; `IDistributedLock : IAsyncDisposable` with
  `string Name { get; }`. Implementations are `internal`; consumers resolve the
  interface.

- [ ] **Step 1: Failing unit tests** (NSubstitute stands in for Redis — the
  guards and the token-checked-release call shape need no server):

```csharp
using Common.Infrastructure.Redis;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace Common.Infrastructure.Tests;

public sealed class DistributedLockTests
{
    private readonly IConnectionMultiplexer _redis = Substitute.For<IConnectionMultiplexer>();
    private readonly IDatabase _database = Substitute.For<IDatabase>();

    private IDistributedLockFactory Factory()
    {
        _redis.GetDatabase().Returns(_database);

        ServiceCollection services = new();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment("catalog"));
        services.AddKeyedSingleton(RedisConnections.Coordination, _redis);
        services.AddSingleton<RedisKeys>();
        services.AddSingleton<IDistributedLockFactory, RedisDistributedLockFactory>();

        return services.BuildServiceProvider().GetRequiredService<IDistributedLockFactory>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_lock_without_a_positive_TTL_is_refused_before_any_IO(int seconds)
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            Factory().TryAcquireAsync("reprice", TimeSpan.FromSeconds(seconds)));

        _redis.DidNotReceive().GetDatabase();
    }

    [Fact]
    public async Task Contention_returns_null_rather_than_throwing()
    {
        _database.StringSetAsync(default, default, null, When.Always).ReturnsForAnyArgs(false);

        IDistributedLock? held = await Factory().TryAcquireAsync("reprice", TimeSpan.FromSeconds(30));

        held.ShouldBeNull();
    }

    [Fact]
    public async Task Acquisition_writes_the_lock_key_with_NX_and_the_TTL()
    {
        _database.StringSetAsync(default, default, null, When.Always).ReturnsForAnyArgs(true);

        IDistributedLock? held = await Factory().TryAcquireAsync("reprice", TimeSpan.FromSeconds(30));

        held.ShouldNotBeNull();
        held.Name.ShouldBe("reprice");
        await _database.Received(1).StringSetAsync(
            "catalog:lock:reprice",
            Arg.Any<RedisValue>(),
            TimeSpan.FromSeconds(30),
            When.NotExists);
    }

    [Fact]
    public async Task Dispose_releases_once_and_only_once()
    {
        _database.StringSetAsync(default, default, null, When.Always).ReturnsForAnyArgs(true);

        IDistributedLock held = (await Factory().TryAcquireAsync("reprice", TimeSpan.FromSeconds(30)))!;
        await held.DisposeAsync();
        await held.DisposeAsync();

        await _database.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>());
    }
}
```

  NSubstitute overload note: match the exact `StringSetAsync` /
  `ScriptEvaluateAsync` overloads the implementation calls — if the compiler
  picks a different one, adjust the test's `ReturnsForAnyArgs` anchor to that
  overload rather than loosening the assertion.

- [ ] **Step 2: Run, watch them fail** (types missing).

- [ ] **Step 3: Implement.** `IDistributedLockFactory.cs`:

```csharp
namespace Common.Infrastructure.Redis;

/// <summary>
/// §8.1's third row: <c>SET key NX PX</c> with a token-checked release. The
/// TTL is mandatory and enforced here — a lock without one is a memory leak
/// on a noeviction instance, which eventually stops writes entirely (§8.1).
/// </summary>
public interface IDistributedLockFactory
{
    /// <summary>
    /// Null when the lock is held elsewhere. Throws if Redis is unreachable —
    /// §8.1: fail the operation, never proceed unlocked.
    /// </summary>
    Task<IDistributedLock?> TryAcquireAsync(string name, TimeSpan duration, CancellationToken ct = default);
}

/// <summary>A held lock. Disposing releases it, token-checked: a handle whose
/// key has expired and been re-acquired elsewhere releases nothing.</summary>
public interface IDistributedLock : IAsyncDisposable
{
    string Name { get; }
}
```

`RedisDistributedLockFactory.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Common.Infrastructure.Redis;

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
```

`RedisDistributedLock.cs`:

```csharp
using StackExchange.Redis;

namespace Common.Infrastructure.Redis;

internal sealed class RedisDistributedLock(IConnectionMultiplexer redis, string key, string name, string token)
    : IDistributedLock
{
    // Delete only what this handle wrote. GET-compare-DEL as one script: the
    // check and the delete must be atomic, or a lock that expires between
    // them deletes the next holder's key — the §8.1 failure with no error.
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

        await redis.GetDatabase().ScriptEvaluateAsync(ReleaseScript, [(RedisKey)key], [(RedisValue)token]);
    }
}
```

- [ ] **Step 4: Tests green, build clean.**
- [ ] **Step 5: Commit** — `feat(common): distributed lock over the coordination connection`,
  body arguing the mandatory TTL and the atomic token check.

---

### Task 3: `AddRedisConnections`

**Files:**
- Create: `src/BuildingBlocks/Common.Infrastructure/Redis/DependencyInjection.cs`
- Create: `tests/Common.Infrastructure.Tests/TestEnvironment.cs` (moved out of
  `RedisKeysTests.cs`; both test classes now use it)
- Create: `tests/Common.Infrastructure.Tests/AddRedisConnectionsTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–2.
- Produces: `services.AddRedisConnections(IConfiguration)` — the one
  registration entry point; C# 14 extension block, mirroring
  `Common.Application.DependencyInjection`.

- [ ] **Step 1: Failing tests** — registration surface without a provider, and
  fail-at-startup on missing keys:

```csharp
using Common.Infrastructure.Redis;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace Common.Infrastructure.Tests;

public sealed class AddRedisConnectionsTests
{
    private static IConfiguration Configuration(string? cache = "localhost:6379", string? coordination = "localhost:6380")
    {
        Dictionary<string, string?> settings = new();
        if (cache is not null)
            settings["ConnectionStrings:RedisCache"] = cache;
        if (coordination is not null)
            settings["ConnectionStrings:RedisCoordination"] = coordination;

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static ServiceCollection Registered()
    {
        ServiceCollection services = new();
        services.AddRedisConnections(Configuration());
        return services;
    }

    [Fact]
    public void Both_connections_register_as_keyed_singletons()
    {
        ServiceCollection services = Registered();

        services.ShouldContain(d =>
            d.ServiceType == typeof(IConnectionMultiplexer) &&
            Equals(d.ServiceKey, RedisConnections.Cache) &&
            d.Lifetime == ServiceLifetime.Singleton);
        services.ShouldContain(d =>
            d.ServiceType == typeof(IConnectionMultiplexer) &&
            Equals(d.ServiceKey, RedisConnections.Coordination) &&
            d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void The_cache_stack_and_the_helpers_are_registered()
    {
        ServiceCollection services = Registered();

        services.ShouldContain(d => d.ServiceType == typeof(HybridCache));
        services.ShouldContain(d => d.ServiceType == typeof(IDistributedCache));
        services.ShouldContain(d => d.ServiceType == typeof(RedisKeys));
        services.ShouldContain(d => d.ServiceType == typeof(IDistributedLockFactory));
    }

    [Theory]
    [InlineData(null, "localhost:6380", "ConnectionStrings:RedisCache")]
    [InlineData("localhost:6379", null, "ConnectionStrings:RedisCoordination")]
    public void A_missing_connection_string_fails_at_registration_naming_the_key(
        string? cache, string? coordination, string expected)
    {
        ServiceCollection services = new();

        InvalidOperationException thrown = Should.Throw<InvalidOperationException>(() =>
            services.AddRedisConnections(Configuration(cache, coordination)));

        thrown.Message.ShouldContain(expected);
    }
}
```

- [ ] **Step 2: Run, watch them fail.**

- [ ] **Step 3: Implement `DependencyInjection.cs`.**

```csharp
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using StackExchange.Redis;

namespace Common.Infrastructure.Redis;

/// <summary>
/// §8.1's two keyed connections, §8.2's cache stack over the first of them,
/// and the coordination-side helpers — one call, so an unregistered cache is
/// a service that will not start rather than a slower one that silently reads
/// the database (§8.2).
/// </summary>
public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddRedisConnections(IConfiguration configuration)
        {
            // Read eagerly: a host missing its connection string must not
            // start (PR-08's precedent — AddSqlServer throws on null), and a
            // lazy factory would move the failure to the first cache miss.
            string cacheConnection = RequiredConnectionString(configuration, RedisConnections.Cache);
            string coordinationConnection = RequiredConnectionString(configuration, RedisConnections.Coordination);

            services.AddKeyedSingleton<IConnectionMultiplexer>(
                RedisConnections.Cache,
                (_, _) => Connect(cacheConnection));
            services.AddKeyedSingleton<IConnectionMultiplexer>(
                RedisConnections.Coordination,
                (_, _) => Connect(coordinationConnection));

            services.AddSingleton<RedisKeys>();
            services.AddSingleton<IDistributedLockFactory, RedisDistributedLockFactory>();

            // The CACHE connection (allkeys-lru). Coordination keys use the
            // other. The factory hands the cache its keyed multiplexer — one
            // connection per instance, and the traced connection is the one
            // the cache actually uses, not a private third.
            services.AddStackExchangeRedisCache(_ => { });
            services
                .AddOptions<RedisCacheOptions>()
                .Configure<IServiceProvider>((options, provider) =>
                {
                    // The §8.1 key prefix, spelled once, in RedisKeys (§8.3).
                    options.InstanceName = provider.GetRequiredService<RedisKeys>().CacheInstanceName;
                    options.ConnectionMultiplexerFactory = () =>
                        Task.FromResult(provider.GetRequiredKeyedService<IConnectionMultiplexer>(RedisConnections.Cache));
                });

            services.AddHybridCache(options =>
            {
                options.DefaultEntryOptions = new HybridCacheEntryOptions
                {
                    Expiration = TimeSpan.FromMinutes(10),            // L2, Redis
                    LocalCacheExpiration = TimeSpan.FromMinutes(1)    // L1, in-process
                };
                options.MaximumPayloadBytes = 1024 * 1024;
            });

            // §13.2's rule — the instrumentation lands with the package it
            // instruments. It cannot live in Common.Web: these connections
            // are keyed, and the parameterless overload only discovers an
            // unkeyed IConnectionMultiplexer, so there it would silently
            // instrument nothing.
            services
                .AddOpenTelemetry()
                .WithTracing(tracing => tracing
                    .AddRedisInstrumentation()
                    .ConfigureRedisInstrumentation((provider, instrumentation) =>
                    {
                        instrumentation.AddConnection(provider.GetRequiredKeyedService<IConnectionMultiplexer>(RedisConnections.Cache));
                        instrumentation.AddConnection(provider.GetRequiredKeyedService<IConnectionMultiplexer>(RedisConnections.Coordination));
                    }));

            return services;
        }
    }

    private static string RequiredConnectionString(IConfiguration configuration, string name) =>
        configuration.GetConnectionString(name)
            ?? throw new InvalidOperationException(
                $"ConnectionStrings:{name} is not configured — §8.1 needs both Redis connections.");

    private static ConnectionMultiplexer Connect(string connectionString)
    {
        ConfigurationOptions options = ConfigurationOptions.Parse(connectionString);

        // Degrade, don't die: §8.1's first row tolerates Redis being down, and
        // the readiness check is what reports it. The multiplexer connects in
        // the background and retries; coordination callers still fail closed,
        // because their operations throw while the connection is absent.
        options.AbortOnConnectFail = false;

        return ConnectionMultiplexer.Connect(options);
    }
}
```

  If `ConfigureRedisInstrumentation` lacks the `(IServiceProvider, …)` overload
  on the pinned package, fall back to
  `services.ConfigureOpenTelemetryTracerProvider((sp, builder) => …)` or the
  named-options route — resolve at compile time and record the finding in the
  commit body.

- [ ] **Step 4: Move `TestEnvironment` to its own file** (both test classes use
  it now). Run the whole unit half: `dotnet test tests/Common.Infrastructure.Tests`.
  Container tests do not exist yet, so this passes without Docker.

- [ ] **Step 5: Commit** — `feat(common): AddRedisConnections — two connections, one cache stack`,
  body arguing eager configuration reads, multiplexer reuse and the
  instrumentation's home.

---

### Task 4: Redis fixture and the container lock suite

**Files:**
- Create: `tests/Common.Infrastructure.Tests/RedisFixture.cs`
- Create: `tests/Common.Infrastructure.Tests/IntegrationCollection.cs`
- Create: `tests/Common.Infrastructure.Tests/DistributedLockRedisTests.cs`

**Interfaces:**
- Consumes: `AddRedisConnections` (Task 3), `TestEnvironment` (Task 3).
- Produces: `RedisFixture.ConnectionString`;
  `RedisFixture.BuildProvider(string applicationName)` returning a
  `ServiceProvider` composed through the real registration — Task 5 reuses it.

- [ ] **Step 1: Fixture and collection.**

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.Redis;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>
/// A real Redis (ADR-010, §12.4) — the image §14.1's Compose file runs, so a
/// test and a developer machine cannot disagree about the engine. One
/// container serves both §8.1 roles: the *policies* differ per instance in
/// Compose, but what these tests assert — key shapes, TTLs, NX semantics,
/// token-checked release — is identical on either policy.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    public string ConnectionString => _redis.GetConnectionString();

    /// <summary>The real composition path — no test re-wires what
    /// AddRedisConnections wires (§6.2's argument, one layer down).
    /// <paramref name="configure"/> lets a test add its exporter without
    /// touching the registration under test.</summary>
    public ServiceProvider BuildProvider(string applicationName, Action<IServiceCollection>? configure = null)
    {
        Dictionary<string, string?> settings = new()
        {
            ["ConnectionStrings:RedisCache"] = ConnectionString,
            ["ConnectionStrings:RedisCoordination"] = ConnectionString
        };
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment(applicationName));
        services.AddRedisConnections(configuration);
        configure?.Invoke(services);

        return services.BuildServiceProvider();
    }

    public async ValueTask InitializeAsync() =>
        await _redis.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() =>
        await _redis.DisposeAsync();
}
```

```csharp
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>
/// §12.4's per-assembly declaration, the third assembly that needs Docker —
/// and the same policy as the first two: no skip and no category when the
/// daemon is absent, because a skip on a missing daemon fails open.
/// </summary>
[CollectionDefinition(nameof(IntegrationCollection))]
public sealed class IntegrationCollection : ICollectionFixture<RedisFixture>;
```

- [ ] **Step 2: The lock suite** — each test its own lock name, so the shared
  container never couples tests:

```csharp
using Common.Infrastructure.Redis;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

[Collection(nameof(IntegrationCollection))]
public sealed class DistributedLockRedisTests(RedisFixture fixture)
{
    private IDistributedLockFactory Factory(string applicationName = "locks") =>
        fixture.BuildProvider(applicationName).GetRequiredService<IDistributedLockFactory>();

    [Fact]
    public async Task A_held_lock_cannot_be_acquired_again()
    {
        IDistributedLockFactory factory = Factory();

        await using IDistributedLock? first = await factory.TryAcquireAsync("contend", TimeSpan.FromSeconds(30));
        IDistributedLock? second = await factory.TryAcquireAsync("contend", TimeSpan.FromSeconds(30));

        first.ShouldNotBeNull();
        second.ShouldBeNull();
    }

    [Fact]
    public async Task Release_frees_the_lock_for_the_next_holder()
    {
        IDistributedLockFactory factory = Factory();

        IDistributedLock? first = await factory.TryAcquireAsync("handover", TimeSpan.FromSeconds(30));
        await first!.DisposeAsync();

        await using IDistributedLock? second = await factory.TryAcquireAsync("handover", TimeSpan.FromSeconds(30));
        second.ShouldNotBeNull();
    }

    [Fact]
    public async Task Expiry_frees_the_lock_without_a_release()
    {
        IDistributedLockFactory factory = Factory();

        IDistributedLock? first = await factory.TryAcquireAsync("expire", TimeSpan.FromMilliseconds(200));
        first.ShouldNotBeNull();

        IDistributedLock? second = await WaitForAcquireAsync(factory, "expire");
        second.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_stale_handle_must_not_release_the_next_holders_lock()
    {
        IDistributedLockFactory factory = Factory();

        IDistributedLock? stale = await factory.TryAcquireAsync("stolen", TimeSpan.FromMilliseconds(200));
        stale.ShouldNotBeNull();

        // The key expires; another holder takes the lock with a new token.
        IDistributedLock? current = await WaitForAcquireAsync(factory, "stolen");
        current.ShouldNotBeNull();

        // The stale handle's token no longer matches: this must delete nothing.
        await stale.DisposeAsync();

        IDistributedLock? intruder = await factory.TryAcquireAsync("stolen", TimeSpan.FromSeconds(30));
        intruder.ShouldBeNull();
    }

    /// <summary>Polls acquisition past a short TTL. Bounded: ~5 s of attempts,
    /// far past the 200 ms TTLs above, so a pass is never a lucky race.</summary>
    private static async Task<IDistributedLock?> WaitForAcquireAsync(IDistributedLockFactory factory, string name)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            IDistributedLock? held = await factory.TryAcquireAsync(name, TimeSpan.FromSeconds(30));
            if (held is not null)
                return held;

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        return null;
    }
}
```

- [ ] **Step 3: Run with Docker up** — the suite passes; without the daemon it
  fails on the container start, which is the true statement about the machine.
- [ ] **Step 4: Commit** — `test(common): the lock's semantics against a real Redis`,
  body naming the stale-handle case as the §8.1 claim under test.

---

### Task 5: HybridCache and tracing against the container

**Files:**
- Create: `tests/Common.Infrastructure.Tests/HybridCacheRedisTests.cs`

**Interfaces:**
- Consumes: `RedisFixture.BuildProvider` (Task 4).

- [ ] **Step 1: The suite.** Distinct `applicationName` per test where the
  assertion scans keys, so the shared container never couples tests:

```csharp
using System.Diagnostics;
using Common.Infrastructure.Redis;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace Common.Infrastructure.Tests;

[Collection(nameof(IntegrationCollection))]
public sealed class HybridCacheRedisTests(RedisFixture fixture)
{
    [Fact]
    public async Task Two_reads_execute_the_factory_once()
    {
        await using ServiceProvider provider = fixture.BuildProvider("hits");
        HybridCache cache = provider.GetRequiredService<HybridCache>();
        int executions = 0;

        for (int i = 0; i < 2; i++)
            await cache.GetOrCreateAsync(
                "product:1:v1",
                (object?)null,
                (_, _) =>
                {
                    executions++;
                    return ValueTask.FromResult("priced");
                },
                cancellationToken: TestContext.Current.CancellationToken);

        executions.ShouldBe(1);
    }

    [Fact]
    public async Task The_stored_key_carries_the_cache_prefix_and_a_TTL()
    {
        await using ServiceProvider provider = fixture.BuildProvider("prefixed");
        HybridCache cache = provider.GetRequiredService<HybridCache>();

        await cache.GetOrCreateAsync(
            "product:2:v1",
            (object?)null,
            (_, _) => ValueTask.FromResult("priced"),
            cancellationToken: TestContext.Current.CancellationToken);

        IConnectionMultiplexer redis =
            provider.GetRequiredKeyedService<IConnectionMultiplexer>(RedisConnections.Cache);
        RedisKey[] keys =
            [.. redis.GetServer(redis.GetEndPoints()[0]).Keys(pattern: "prefixed:cache:*")];

        keys.ShouldNotBeEmpty("the §8.3 prefix comes from InstanceName, and its absence " +
            "is a key outside the keyspace whose eviction policy was §8.1's whole argument");
        foreach (RedisKey key in keys)
            redis.GetDatabase().KeyTimeToLive(key).ShouldNotBeNull(
                "every cache key has a TTL — §8.1's first enforced rule");
    }

    [Fact]
    public async Task Removing_a_tag_invalidates_the_entry()
    {
        await using ServiceProvider provider = fixture.BuildProvider("tags");
        HybridCache cache = provider.GetRequiredService<HybridCache>();
        int executions = 0;

        async ValueTask<string> ReadAsync() =>
            await cache.GetOrCreateAsync(
                "product:3:v1",
                (object?)null,
                (_, _) =>
                {
                    executions++;
                    return ValueTask.FromResult("priced");
                },
                tags: ["product:3"],
                cancellationToken: TestContext.Current.CancellationToken);

        await ReadAsync();
        await cache.RemoveByTagAsync("product:3", TestContext.Current.CancellationToken);
        await ReadAsync();

        executions.ShouldBe(2);
    }

    [Fact]
    public async Task A_cache_operation_produces_a_Redis_client_span()
    {
        List<Activity> exported = [];

        // The fixture's composition plus an in-memory exporter — the
        // instrumentation itself is registered by AddRedisConnections, which
        // is the claim under test (§13.2's amended home).
        await using ServiceProvider provider = fixture.BuildProvider(
            "traced",
            services => services
                .AddOpenTelemetry()
                .WithTracing(tracing => tracing.AddInMemoryExporter(exported)));

        HybridCache cache = provider.GetRequiredService<HybridCache>();
        await cache.GetOrCreateAsync(
            "product:4:v1",
            (object?)null,
            (_, _) => ValueTask.FromResult("priced"),
            cancellationToken: TestContext.Current.CancellationToken);

        // The instrumentation drains profiling sessions on a timer, so the
        // span arrives after the operation rather than during it.
        for (int attempt = 0; attempt < 150 && exported.Count == 0; attempt++)
            await Task.Delay(100, TestContext.Current.CancellationToken);

        exported.ShouldNotBeEmpty("AddRedisConnections registers the Redis instrumentation " +
            "and hands it both keyed connections — §13.2");
    }
}
```

  If the drain interval proves longer than the poll budget, configure the
  instrumentation's flush interval down in the test composition
  (`services.Configure<StackExchangeRedisInstrumentationOptions>(o => o.FlushInterval = TimeSpan.FromMilliseconds(100))`)
  rather than lengthening the poll.

- [ ] **Step 2: Run the whole solution's tests with Docker up** — everything
  green, including both existing container suites.
- [ ] **Step 3: Commit** — `test(common): HybridCache wears the prefix, a TTL and a span`,
  body noting the tag-invalidation proof is §8.4's mechanism verified at this
  pin, two PRs before its consumer.

---

### Task 6: Blueprint reconciliation

**Files:**
- Modify: `docs/backend-architecture/08-caching-redis.md`
- Modify: `docs/backend-architecture/13-observability.md`
- Modify: `src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs` (comment only)
- Modify: `src/BuildingBlocks/Common.Web/Common.Web.csproj` (comment only)
- Modify: `docs/backend-architecture/appendix-d-type-inventory.md`
- Modify: `deploy/compose/docker-compose.yml` (one comment)

Grep first, per the one rule: `AddRedisConnections`, `RedisConnections`,
`InstanceName`, `AddRedisInstrumentation`, `DB index` across `docs/` and
`src/`, and reconcile every mention — the list below is the expected set, not
a licence to stop looking.

- [ ] **Step 1: §8.1.** In the keyspace table, the `{service}:lock:` placement
  cell becomes `**noeviction** — a separate instance` (drop “or a separate
  Redis DB index with its own policy”: `maxmemory-policy` is per-instance in
  Redis, so a DB index cannot carry its own and the alternative was never
  real). Adjust the sentence under the table if it leans on the DB-index
  option. In “Two rules the helper library enforces rather than documents”,
  name the enforcers: `RedisKeys` (no cross-service keys — the prefix is not
  writable by a call site) and `IDistributedLockFactory` / the HybridCache
  defaults (no key without a TTL).
- [ ] **Step 2: §8.2.** The `AddRedisConnections` sample: home comment becomes
  `// Common.Infrastructure — called by AddOrderingInfrastructure (§4.2).`;
  replace the `options.Configuration = …` line with the
  `ConnectionMultiplexerFactory` reuse of the keyed cache connection; source
  `InstanceName` from `RedisKeys.CacheInstanceName`; show or state the
  instrumentation registration; convert the fence to the C# 14 extension-block
  form the code uses. Keep the sample abbreviated where the code is long — a
  comment naming what is elided is the chapter's established form.
- [ ] **Step 3: §8.3.** After the InstanceName paragraph, name `RedisKeys` as
  the coordination-half helper and the reason there is no `Cache(string)`
  method (double-prefix hazard).
- [ ] **Step 4: §8.5.** `RedisIdempotencyStore` sample: inject `RedisKeys`
  and build keys with `keys.Idempotency(suffix)`; fold the existing
  ApplicationName comment into `RedisKeys`' rationale (it now lives there).
- [ ] **Step 5: §13.2.** Remove `.AddRedisInstrumentation()` from the printed
  `AddObservability` block; rewrite the “waits for PR-12” prose: the call
  landed at PR-12 **inside `AddRedisConnections`**, because the connections
  are keyed (invisible to the parameterless overload) and because a host with
  no Redis must not carry `StackExchange.Redis` — the same dependency-truth
  rule the paragraph already argues. Update the matching comments in
  `ObservabilityExtensions.cs` (lines around `AddSource("MassTransit")`) and
  `Common.Web.csproj` (the “arrives … at PR-12” package comment).
- [ ] **Step 6: Appendix D.** D.4 gains rows for `RedisKeys`,
  `RedisDistributedLockFactory` (+ its handle); D.1-or-D.4 placement judged
  by the appendix's own rule — `IDistributedLockFactory` is defined in §8's
  amended text, so it is inventoried where shown. Update the D.5 rows for
  `AddRedisConnections` / `RedisConnections` if their one-liners no longer
  hold.
- [ ] **Step 7: Compose comment.** In `catalog-api`'s environment block,
  correct the pairing: the bus and the outbox arrive with PR-13 and PR-14,
  JWT with PR-16, and the Redis keys with the PR whose code first reads them
  — HybridCache's helpers landed at PR-12 without wiring a service.
- [ ] **Step 8:** `dotnet build Platform.slnx` (comment edits still compile),
  then commit — `docs: §8 and §13.2 say what PR-12 built`, body listing the
  contradictions closed (the DB-index cell, the instrumentation's home, the
  sample's old home comment).

---

### Task 7: CLAUDE.md, verification sweep

**Files:**
- Modify: `CLAUDE.md`
- Verify: everything.

- [ ] **Step 1: CLAUDE.md.** Present-tree: add `Common.Infrastructure/` (Redis
  helpers, no project references) and `Common.Infrastructure.Tests/`; planned
  tree annotation becomes “.Contracts (the rest exist)”; phase section: PR-12
  landed with its binding decisions (instrumentation home, no service wired,
  verbatim ApplicationName prefix, mandatory TTL), **PR-13 is next**; “three
  of five” building blocks becomes four of five with `Common.Contracts` the
  one remaining; Docker-needing test projects two → three; test counts
  refreshed from the actual runs.
- [ ] **Step 2: Full verification.**
  - `dotnet build Platform.slnx` — zero warnings.
  - `dotnet test Platform.slnx` — Docker up; record the new total.
  - `cd tools/new-service && py -3.12 -m unittest` — the scaffold reads
    Catalog and the compose pair; PR-12 touched a comment inside the
    catalog-api block, so this run is the proof the anchors survived.
  - `cd .github/licence-gate && py -3.12 -m unittest` — the new pins are in
    the register.
  - `python tools/new-service/new_service.py Probe --port 5199` on a throwaway
    branch if the unittest run leaves any doubt; discard after.
- [ ] **Step 3: `/validate-blueprint`** — required after substantive blueprint
  edits; fix what it finds in the affected files before shipping.
- [ ] **Step 4: Commit** — `chore: carry PR-12 in CLAUDE.md`, then `/ship`
  (already on `feat/common-redis-helpers`; it resumes at the commit/PR steps).
