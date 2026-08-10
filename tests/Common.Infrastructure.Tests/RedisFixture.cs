using Common.Infrastructure.Redis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.Redis;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>
/// Two real Redis containers (ADR-010) — the image §14.1's Compose file runs
/// and §8.1's split, for §12.4's stated reason: with one server playing both
/// roles, a cache stack accidentally wired to the coordination connection
/// still passes every prefix, TTL and span test while production cache
/// entries fill the noeviction instance. Two servers make role-routing an
/// assertable fact.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _cache = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .WithCommand("--maxmemory-policy", "allkeys-lru")
        .Build();

    private readonly RedisContainer _coordination = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .WithCommand("--maxmemory-policy", "noeviction")
        .Build();

    public string CacheConnectionString => _cache.GetConnectionString();

    public string CoordinationConnectionString => _coordination.GetConnectionString();

    /// <summary>
    /// The real composition path — no test re-wires what AddRedisConnections
    /// wires (§6.2's argument, one layer down). <paramref name="configure"/>
    /// lets a test add its exporter without touching the registration under
    /// test.
    /// </summary>
    public ServiceProvider BuildProvider(string applicationName, Action<IServiceCollection>? configure = null)
    {
        Dictionary<string, string?> settings = new()
        {
            ["ConnectionStrings:RedisCache"] = CacheConnectionString,
            ["ConnectionStrings:RedisCoordination"] = CoordinationConnectionString
        };
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment(applicationName));
        services.AddRedisConnections(configuration);
        configure?.Invoke(services);

        return services.BuildServiceProvider();
    }

    // ValueTask, not Task: xUnit v3 redefined IAsyncLifetime (§12.4).
    public async ValueTask InitializeAsync() =>
        await Task.WhenAll(
            _cache.StartAsync(TestContext.Current.CancellationToken),
            _coordination.StartAsync(TestContext.Current.CancellationToken));

    public async ValueTask DisposeAsync()
    {
        await _cache.DisposeAsync();
        await _coordination.DisposeAsync();
    }
}
