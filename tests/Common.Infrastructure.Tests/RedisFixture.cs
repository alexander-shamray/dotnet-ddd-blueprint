using Common.Infrastructure.Redis;
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
/// token-checked release — is identical under either policy.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    public string ConnectionString => _redis.GetConnectionString();

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

    // ValueTask, not Task: xUnit v3 redefined IAsyncLifetime (§12.4).
    public async ValueTask InitializeAsync() =>
        await _redis.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() =>
        await _redis.DisposeAsync();
}
