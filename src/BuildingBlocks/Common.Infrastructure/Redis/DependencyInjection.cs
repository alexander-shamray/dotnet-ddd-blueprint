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
/// a service that will not start rather than a slower one that silently
/// reads the database (§8.2).
/// </summary>
public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddRedisConnections(IConfiguration configuration)
        {
            // Read eagerly: a host missing its connection string must not
            // start (PR-08's precedent — AddSqlServer throws on a null one),
            // and a lazy factory would move the failure to the first miss.
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

            // The CACHE connection (allkeys-lru); coordination keys use the
            // other. The factory hands the cache its keyed multiplexer — one
            // connection per instance, and the traced connection is then the
            // one the cache actually uses, not a private third.
            services.AddStackExchangeRedisCache(_ => { });
            services
                .AddOptions<RedisCacheOptions>()
                .Configure<IServiceProvider>((options, provider) =>
                {
                    // §8.1's key prefix, spelled once, in RedisKeys (§8.3).
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

            // §13.2's rule — an instrumentation lands with the package it
            // instruments. It cannot live in Common.Web: these connections
            // are keyed, the parameterless overload only discovers an
            // unkeyed IConnectionMultiplexer, and there it would silently
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

        // Degrade, don't die: §8.1's first row tolerates Redis being down,
        // and the readiness check is what reports it. The multiplexer
        // connects in the background and retries; coordination callers still
        // fail closed, because their operations throw while it is absent.
        options.AbortOnConnectFail = false;

        return ConnectionMultiplexer.Connect(options);
    }
}
