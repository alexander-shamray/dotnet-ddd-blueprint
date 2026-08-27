using System.Data.Common;
using Catalog.Infrastructure.Persistence;
using Catalog.Migrator;
using Common.Application;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Outbox;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Respawn;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;
using Xunit;

namespace Catalog.TestSupport;

/// <summary>
/// A real SQL Server, migrated by the real migrator (ADR-010, §12.4), and a
/// real RabbitMQ for the bus to connect to. Each image is the one §14.1's
/// Compose file runs — for the broker that means the base tag its Dockerfile
/// builds from, since ADR-021 made §14.1 build rather than pull, and Catalog
/// may stop at the base because it registers no message scheduler and
/// schedules nothing; Ordering's fixture builds the Dockerfile itself, for
/// exactly the reason this one need not. So a test and a developer machine
/// cannot disagree about
/// the engine. §12.4's name and §4.1's home: the fixture serves
/// <c>Catalog.Application.Tests</c> and <c>Catalog.Api.Tests</c>, which
/// cannot reference each other — each declares its own
/// <c>IntegrationCollection</c> over this one type. §12.4's full shape is
/// complete since §8.5's PR: the two Redis containers arrived with the
/// behaviour whose code reads those keys, which is the same rule the broker
/// followed.
/// </summary>
/// <remarks>
/// Tests deliberately collapse the two database identities of §7.1 — the
/// container's <c>sa</c> login holds both DML and DDL — but not the two
/// configuration keys, which stay distinct so that the migrator can be caught
/// reading the wrong one. Production keeps both separate, and migrations run as
/// a job, never from a host (ADR-007).
/// </remarks>
public sealed class ServiceFixture : IAsyncLifetime
{
    /// <summary>
    /// §8.1's two servers, and two rather than one for §12.4's stated reason:
    /// with a single server playing both roles, a stack accidentally wired to
    /// the wrong connection passes every prefix, TTL and claim test while
    /// production idempotency keys sit on an <c>allkeys-lru</c> instance —
    /// evicted under exactly the memory pressure that makes the duplicate
    /// write hardest to reproduce. Two servers make role-routing assertable.
    /// </summary>
    /// <remarks>
    /// They joined with §8.5's PR, which is the rule this fixture already
    /// followed for the broker: a container arrives with the code that reads
    /// what it holds.
    /// </remarks>
    private readonly RedisContainer _redisCache = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .WithCommand("--maxmemory-policy", "allkeys-lru")
        .Build();

    private readonly RedisContainer _redisCoordination = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .WithCommand("--maxmemory-policy", "noeviction")
        .Build();

    private readonly MsSqlContainer _sql = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    // Assigned in InitializeAsync rather than here, because the image has to
    // be BUILT and a field initialiser cannot await. It used to be the stock
    // `rabbitmq:4.1-management-alpine` on the argument that Catalog needs no
    // plugin and sharing the base tag was cheaper than a second image.
    //
    // #44 ended that: §14.1's broker image is where definitions.json lives, so
    // the stock image is a broker with ONE administrator account and no
    // permissions at all — the state this suite is now meant to prove Catalog
    // works without. Ordering's fixture already builds this image and names it
    // the same, so the cost is a cache hit rather than a second download.
    private RabbitMqContainer? _rabbit;

    private Respawner? _respawner;

    /// <summary>
    /// The connection each §7.1 identity would hold, pointed at Catalog's own
    /// database rather than the container's <c>master</c>.
    /// </summary>
    public string ConnectionString { get; private set; } = null!;

    public CatalogApiFactory Factory { get; private set; } = null!;

    /// <summary>The exit code of the first real migration run.</summary>
    public int FirstRunExitCode { get; private set; } = -1;

    /// <summary>
    /// The directory holding §14.1's broker Dockerfile, found by walking up to
    /// <c>Platform.slnx</c>.
    /// </summary>
    /// <remarks>
    /// A second copy of Ordering.TestSupport's helper, deliberately. §4.3
    /// permits exactly one assembly to cross a service boundary and a test
    /// helper is not it — the same rule that gives the gateway suite its own
    /// <c>TestAuthHandler</c>.
    /// </remarks>
    private static string BrokerContextPath()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "Platform.slnx")))
                continue;

            string context = Path.Combine(dir.FullName, "deploy", "compose", "rabbitmq");
            if (!File.Exists(Path.Combine(context, "Dockerfile")))
            {
                throw new InvalidOperationException(
                    $"Found the solution at {dir.FullName} but no Dockerfile at {context} (§14.1, ADR-021).");
            }

            return context;
        }

        throw new InvalidOperationException(
            $"No Platform.slnx above {AppContext.BaseDirectory}; the broker image cannot be built.");
    }

    // ValueTask, not Task: xUnit v3 redefined IAsyncLifetime (§12.4).
    public async ValueTask InitializeAsync()
    {
        // §14.1's broker image, built from the repository's own Dockerfile so
        // this container carries definitions.json — the per-service accounts
        // and their permissions. WithCleanUp(false) keeps Ryuk from removing
        // it, so the plugin is downloaded once per machine.
        IFutureDockerImage broker = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(BrokerContextPath())
            .WithDockerfile("Dockerfile")
            // ONE NAME PER FIXTURE, and the reason is a FILE rather than a
            // tag. Testcontainers writes the build context to a tar named after
            // the image — `ashamray-test-broker-4-1-delayed.tar` under the temp
            // root — so two suites building the same name concurrently do not
            // race on Docker at all: they race on that file, and the loser dies
            // with "The process cannot access the file … because it is being
            // used by another process".
            //
            // Measured, and both ways round: under `dotnet test Platform.slnx`
            // whichever fixture started second failed EVERY one of its tests in
            // under 100 ms, while each passed alone — a fixture fault wearing a
            // suite-wide failure, which is why the count is the tell and the
            // duration is the proof.
            //
            // The two images share every layer but the tag, so the second build
            // is a cache hit and not a second plugin download.
            .WithName("ashamray-test-broker-catalog:4.1-delayed")
            .WithCleanUp(false)
            .Build();

        // Catalog's OWN account (#44). Its permissions cover its own contracts
        // and nothing else, so a Catalog change that starts publishing another
        // context's events fails in this suite rather than in a cluster.
        _rabbit = new RabbitMqBuilder()
            .WithImage(broker)
            .WithUsername("catalog-svc")
            .WithPassword("local-dev-catalog")
            .Build();

        await broker.CreateAsync(TestContext.Current.CancellationToken);

        // Together, §12.4's printed shape — the broker's start hides inside
        // SQL Server's, which is the slower of the two by some margin.
        await Task.WhenAll(
            _sql.StartAsync(TestContext.Current.CancellationToken),
            _rabbit.StartAsync(TestContext.Current.CancellationToken),
            _redisCache.StartAsync(TestContext.Current.CancellationToken),
            _redisCoordination.StartAsync(TestContext.Current.CancellationToken));

        // The container hands out a connection to master; Catalog owns a
        // database of its own (§7.1), and MigrateAsync is what creates it.
        // DbConnectionStringBuilder out of habit rather than necessity now:
        // this project does carry the provider package, for the open
        // SqlConnection Respawn inspects in ResetAsync.
        DbConnectionStringBuilder connection = new() { ConnectionString = _sql.GetConnectionString() };
        connection["Database"] = "Catalog";
        ConnectionString = connection.ConnectionString;

        FirstRunExitCode = await RunMigratorAsync(ConnectionString);

        // Both Redis connections, because AddRedisConnections reads both
        // eagerly (§8.1) — and real ones rather than the factory's unreachable
        // default, because §8.5's behaviour claims a key on every protected
        // command this suite dispatches.
        Factory = new CatalogApiFactory(
            ConnectionString,
            _rabbit.GetConnectionString(),
            _redisCache.GetConnectionString(),
            _redisCoordination.GetConnectionString());

        // A table for the transaction tests, created here and not in a
        // migration. It is a fixture of the test rather than a table of the
        // service, and putting it in a migration to make a test easier would
        // ship it to production.
        await ExecuteAsync(
            """
            CREATE TABLE catalog.TransactionProbe
            (
                Id   uniqueidentifier NOT NULL PRIMARY KEY,
                Note nvarchar(100)    NOT NULL
            );
            """);
    }

    /// <summary>
    /// §12.4's reset: truncation over the <c>catalog</c> schema, far faster
    /// than recreating it and honest where a rolled-back transaction would
    /// hide transaction-related bugs. Tests that share the collection call
    /// this from <c>InitializeAsync</c>; suites asserting the migrator or the
    /// probe table arrange per-test identities instead and never need it.
    /// </summary>
    public async Task ResetAsync()
    {
        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        // dbo is excluded, so EF's migration history survives the truncation.
        _respawner ??= await Respawner.CreateAsync(
            connection,
            new RespawnerOptions
            {
                DbAdapter = DbAdapter.SqlServer,
                SchemasToInclude = ["catalog"]
            });

        await _respawner.ResetAsync(connection);
    }

    /// <summary>
    /// Drives the real §7.4 job host, so the smoke covers which connection
    /// string it reads and what it returns — not a copy of its wiring. A null
    /// argument leaves that key unset, which is how the §7.1 boundary is
    /// tested rather than assumed.
    /// </summary>
    public static async Task<int> RunMigratorAsync(
        string? migratorConnectionString,
        string? runtimeConnectionString = null)
    {
        string[] args =
        [
            .. Setting("ConnectionStrings:CatalogMigrator", migratorConnectionString),
            .. Setting("ConnectionStrings:Catalog", runtimeConnectionString)
        ];

        using IHost host = MigratorHost.Build(args);
        using IServiceScope scope = host.Services.CreateScope();

        return await scope.ServiceProvider
            .GetRequiredService<MigrationRunner>()
            .RunAsync(TestContext.Current.CancellationToken);

        static string[] Setting(string key, string? value) =>
            value is null ? [] : [$"--{key}={value}"];
    }

    /// <summary>
    /// Runs a statement outside any unit of work, for arranging. Placeholders
    /// are <c>{0}</c>-style and EF turns each into a real SQL parameter — the
    /// same rule <see cref="ScalarAsync{T}"/> states, and for the same two
    /// reasons: a formatted string here would be both an injection shape and
    /// a CA1305.
    /// </summary>
    public async Task ExecuteAsync(string sql, params object[] parameters)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        CatalogDbContext db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        await db.Database.ExecuteSqlRawAsync(sql, parameters, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Reads one scalar outside any unit of work, for asserting. Placeholders
    /// are <c>{0}</c>-style and EF turns each into a real SQL parameter — a
    /// formatted string here would be both an injection shape and a CA1305.
    /// </summary>
    public async Task<T> ScalarAsync<T>(string sql, params object[] parameters)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        CatalogDbContext db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        return await db.Database
            .SqlQueryRaw<T>(sql, parameters)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The migrations EF considers applied. Asked through EF rather than by
    /// selecting from <c>__EFMigrationsHistory</c>, so the assertion is about
    /// what that table holds and not about where it lives — which is EF's to
    /// decide, is configured by <c>MigrationsHistoryTable</c> rather than by
    /// this context's <c>HasDefaultSchema</c>, and is no part of what PR-08
    /// claims.
    /// </summary>
    public async Task<string[]> AppliedMigrationsAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        CatalogDbContext db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        return [.. await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)];
    }

    /// <summary>
    /// The host's own map (§9.4), with this assembly's events in it — the
    /// builders in <see cref="Outbox.OutboxRows"/> stage through it, so a row
    /// a test writes is a row the running dispatcher can resolve.
    /// </summary>
    public MessageTypeMap MessageTypes =>
        Factory.Services.GetRequiredService<MessageTypeMap>();

    /// <summary>
    /// The host's payload format, converters included — so a row a test
    /// stages is written the way the dispatcher will read it.
    /// </summary>
    public OutboxJson OutboxJson =>
        Factory.Services.GetRequiredService<OutboxJson>();

    /// <summary>Runs exactly one claim-and-deliver pass. No timers, no waiting.</summary>
    public Task<int> ProcessOutboxBatchAsync() =>
        Factory.Services
            .GetRequiredService<OutboxDispatcher>()
            .ProcessBatchAsync(TestContext.Current.CancellationToken);

    /// <summary>Every outbox row, untracked, for asserting over.</summary>
    public async Task<IReadOnlyList<OutboxMessage>> OutboxAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        CatalogDbContext db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        return await db.OutboxMessages
            .AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Writes rows directly, for tests about the dispatcher rather than the staging.</summary>
    public async Task StageOutboxAsync(params OutboxMessage[] rows)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        CatalogDbContext db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        db.OutboxMessages.AddRange(rows);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Seeds a prior attempt count through the same column the dispatcher
    /// writes. Explicit rather than hidden in a builder, so no state carries
    /// between tests (§12.8).
    /// </summary>
    public Task SetOutboxAttemptsAsync(Guid messageId, int attempts) =>
        ExecuteAsync(
            "UPDATE catalog.OutboxMessages SET Attempts = {0} WHERE MessageId = {1};",
            attempts,
            messageId);

    /// <summary>
    /// Repoints a staged row at the other lane, which is the only way to
    /// produce the row <see cref="OutboxMessage.Stage"/> refuses: a lane that
    /// disagrees with its payload. Written through SQL on purpose — the point
    /// of the dispatcher's re-checks is rows that reached the table without
    /// passing the staging guards, and a test that could build one in process
    /// would be testing a different claim.
    /// </summary>
    public Task SetOutboxLaneAsync(Guid messageId, OutboxLane lane) =>
        ExecuteAsync(
            "UPDATE catalog.OutboxMessages SET Lane = {0} WHERE MessageId = {1};",
            lane.ToString(),
            messageId);

    /// <summary>
    /// Clears retry backoff leases so the next pass is gated only by the
    /// attempt cap. Lets a test distinguish "backed off" from "abandoned"
    /// without sleeping.
    /// </summary>
    public Task ExpireOutboxLeasesAsync() =>
        ExecuteAsync("UPDATE catalog.OutboxMessages SET LockedUntil = NULL WHERE ProcessedAt IS NULL;");

    /// <summary>Every inbox row, untracked, for asserting over (§9.5).</summary>
    public async Task<IReadOnlyList<InboxMessage>> InboxAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        CatalogDbContext db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        return await db.InboxMessages
            .AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Writes inbox rows directly, for tests about the purge rather than the
    /// filter. The filter's own tests go through a consume pipeline, because
    /// what they are about is which of <c>MessageId</c> and <c>Endpoint</c> the
    /// row is keyed on and when it is committed.
    /// </summary>
    public async Task StageInboxAsync(params InboxMessage[] rows)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        CatalogDbContext db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        db.InboxMessages.AddRange(rows);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Ages a processed outbox row, which is how a retention test reaches the
    /// window without a fake clock: the purge resolves <c>TimeProvider</c> from
    /// its own scope inside the host, and moving a row backwards is both
    /// simpler and closer to what the table actually looks like.
    /// </summary>
    public Task SetOutboxProcessedAtAsync(Guid messageId, DateTimeOffset processedAt) =>
        ExecuteAsync(
            "UPDATE catalog.OutboxMessages SET ProcessedAt = {0} WHERE MessageId = {1};",
            processedAt,
            messageId);

    /// <summary>Runs exactly one retention pass over both tables. No timers, no waiting.</summary>
    public Task<(int Outbox, int Inbox)> PurgeRetentionAsync() =>
        Factory.Services
            .GetRequiredService<RetentionPurgeService>()
            .PurgeAsync(TestContext.Current.CancellationToken);

    /// <summary>
    /// One pass under a policy of the test's own, for the batching edges the
    /// registered one cannot show: a batch of 5,000 would need 10,001 rows
    /// before a second batch ran at all.
    /// </summary>
    /// <remarks>
    /// Constructed rather than resolved, because the policy is a constructor
    /// argument and the service composes its two statements from the same
    /// registered tables either way — so what varies is the batching and
    /// nothing else.
    /// </remarks>
    public Task<(int Outbox, int Inbox)> PurgeWithAsync(RetentionPolicy policy)
    {
        RetentionPurgeService purge = new(
            Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Factory.Services.GetRequiredService<OutboxTable>(),
            Factory.Services.GetRequiredService<InboxTable>(),
            policy,
            Factory.Services.GetRequiredService<ILogger<RetentionPurgeService>>());

        return purge.PurgeAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Rows the transaction probe holds for one id.</summary>
    public Task<int> ProbeRowCountAsync(Guid id) =>
        ScalarAsync<int>("SELECT Value = COUNT(*) FROM catalog.TransactionProbe WHERE Id = {0}", id);

    public async ValueTask DisposeAsync()
    {
        // Each teardown runs even when an earlier one throws: a failed
        // factory or SQL disposal must not leave the broker container
        // running for the rest of the CI job.
        try
        {
            Factory?.Dispose();
        }
        finally
        {
            try
            {
                await _sql.DisposeAsync();
            }
            finally
            {
                try
                {
                    // Null-safe on Ordering's fixture's argument: the image
                    // build and the builder chain both run before the field is
                    // assigned, and either can throw.
                    if (_rabbit is not null)
                        await _rabbit.DisposeAsync();
                }
                finally
                {
                    // Nested on the same argument as every layer above it: a
                    // failed broker disposal must not leave two Redis
                    // containers running for the rest of the CI job.
                    try
                    {
                        await _redisCache.DisposeAsync();
                    }
                    finally
                    {
                        await _redisCoordination.DisposeAsync();
                    }
                }
            }
        }
    }
}
