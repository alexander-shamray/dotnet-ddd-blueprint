using System.Data.Common;
using Ordering.Domain.Common;
using Ordering.Domain.Orders;
using Ordering.Infrastructure.Persistence;
using Ordering.Migrator;
using Common.Application;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Outbox;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Respawn;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;
using Xunit;

namespace Ordering.TestSupport;

/// <summary>
/// A real SQL Server, migrated by the real migrator (ADR-010, §12.4), and a
/// real RabbitMQ for the bus to connect to. Each image is the one §14.1's
/// Compose file runs — SQL Server by tag, and the broker by <em>building the
/// same Dockerfile</em>, because since ADR-021 §14.1 does not run a tag for it
/// — so a test and a developer machine cannot disagree about the engine.
/// §12.4's name and §4.1's home: the fixture serves
/// <c>Ordering.Api.Tests</c> today, and the application suite the moment that
/// suite gains a handler test — the two cannot reference each other, so each
/// declares its own
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
    private readonly MsSqlContainer _sql = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

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
    /// what it holds. Before that PR the host resolved no multiplexer, so a
    /// Redis here would have been an unused registration with a startup cost.
    /// </remarks>
    private readonly RedisContainer _redisCache = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .WithCommand("--maxmemory-policy", "allkeys-lru")
        .Build();

    private readonly RedisContainer _redisCoordination = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .WithCommand("--maxmemory-policy", "noeviction")
        .Build();

    /// <summary>
    /// Built in <see cref="InitializeAsync"/> rather than initialised here,
    /// because the image it runs does not exist until this fixture builds it.
    /// </summary>
    /// <remarks>
    /// <b>The stock tag is not an option any more, and the failure it would
    /// cause is the quiet kind.</b> This fixture starts the <em>production</em>
    /// bus, which registers <c>UseDelayedMessageScheduler</c> (ADR-021). A
    /// stock broker takes that registration, connects and reports healthy —
    /// the delayed exchange is not declared until something schedules — so
    /// every test here passes against <c>rabbitmq:4-management-alpine</c>
    /// today, none of them driving the saga. The first one that did would not
    /// fail either: ADR-021's measurement is that the scheduling call **hangs**
    /// while MassTransit retries a declare the broker refuses, so the test
    /// would time out with nothing on the service side naming a plugin.
    /// <para>
    /// So the tag was not merely stale — it made this fixture's own reason for
    /// existing false. "A test and a developer machine cannot disagree about
    /// the engine" is the claim, and §14.1 stopped running that tag in the
    /// same change that made the plugin load-bearing.
    /// </para>
    /// </remarks>
    private RabbitMqContainer? _rabbit;

    private Respawner? _respawner;

    /// <summary>
    /// SQL Server's "chosen as the deadlock victim" error, the only fault
    /// <see cref="ResetAsync"/> retries — the argument is on that method. Named
    /// rather than written as a literal in the filter, because a bare
    /// <c>e.Number == 1205</c> reads as a magic number in the one place it most
    /// needs to be obvious that a narrow fault is being caught and not a broad
    /// one.
    /// </summary>
    private const int DeadlockVictim = 1205;

    /// <summary>
    /// Attempts, not retries — two attempts is one retry.
    /// </summary>
    private const int ResetAttempts = 3;

    /// <summary>
    /// The connection each §7.1 identity would hold, pointed at Ordering's own
    /// database rather than the container's <c>master</c>.
    /// </summary>
    public string ConnectionString { get; private set; } = null!;

    public OrderingApiFactory Factory { get; private set; } = null!;

    /// <summary>The exit code of the first real migration run.</summary>
    public int FirstRunExitCode { get; private set; } = -1;

    /// <summary>
    /// <c>deploy/compose/rabbitmq</c>, found by walking up from the test
    /// assembly to the directory holding <c>Platform.slnx</c>.
    /// </summary>
    /// <remarks>
    /// <b>It throws rather than falling back, and that is the whole design of
    /// this method.</b> A fixture that could not find the Dockerfile and
    /// quietly used the stock tag instead would restore the exact defect this
    /// change closes — and restore it invisibly, on whichever machine had the
    /// unexpected layout. The marker is the solution file rather than
    /// <c>.git</c>, which a worktree stores as a file rather than a directory
    /// and a downloaded archive does not carry at all.
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

    /// <summary>
    /// Widens <c>ordering-svc</c>'s <c>write</c> for the duration of the suite,
    /// because this harness stands in for services that do not exist yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §9.6's saga is driven by events Inventory, Payments, Shipping and
    /// Catalog publish, and none of those services has been built — so the
    /// tests publish them through the host's own <c>IBus</c>, as
    /// <c>ordering-svc</c>. Under ADR-036's production permissions that is
    /// refused, correctly: Ordering must not be able to forge a
    /// <c>StockReservationFailed</c>.
    /// </para>
    /// <para>
    /// **The widening is here rather than in <c>definitions.json</c> on
    /// purpose.** That file is the deployed artefact and a gate holds it to
    /// the code; loosening it so a test can pass would make the gate agree
    /// with a permission set nothing deploys, which is the "double cannot
    /// disagree with itself" failure one artefact over. Doing it in the
    /// harness keeps the production shape honest and puts the exception where
    /// a reader of the suite can see it.
    /// </para>
    /// <para>
    /// **So be precise about what `dotnet test` proves and what it does not.**
    /// <c>configure</c> and <c>read</c> are untouched, so a receive endpoint or
    /// a peer queue this service is not permitted to declare or bind still
    /// fails here — which is the half that rots as endpoints are added.
    /// ADR-036's *negative* property is NOT exercised by this suite; it is
    /// exercised by <c>check_permissions.py</c>, which asserts no service may
    /// write another's resources, and it was measured directly against a
    /// running broker as <c>catalog-svc</c>.
    /// </para>
    /// <para>
    /// This shrinks to nothing as the platform grows: each of those events
    /// gains a real publisher with its own account, and the day the last one
    /// does, this method deletes itself.
    /// </para>
    /// </remarks>
    private async Task WidenWriteForTheHarnessAsync()
    {
        const string scope =
            "^(ordering-|inventory-commands|payments-commands|Common\\.Contracts|" +
            "Ordering\\.Infrastructure\\.Messaging:|MassTransit:)";

        ExecResult result = await _rabbit!.ExecAsync(
            [
                "rabbitmqctl", "set_permissions", "-p", "/", "ordering-svc",
                scope, scope, scope
            ],
            TestContext.Current.CancellationToken);

        // A silent failure here is the worst outcome available: every saga test
        // would then fail on a publish, twenty minutes later, naming a message
        // rather than a permission. Measured — that is exactly how this was
        // found, as a suite that retried a refused publish until it timed out.
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not widen ordering-svc's broker permissions for the harness "
                + $"(exit {result.ExitCode}). stdout: {result.Stdout} stderr: {result.Stderr}");
        }
    }

    // ValueTask, not Task: xUnit v3 redefined IAsyncLifetime (§12.4).
    public async ValueTask InitializeAsync()
    {
        // The broker image §14.1 builds, built from the same Dockerfile rather
        // than copied into a second one. Named and left behind on purpose:
        // WithCleanUp(false) keeps Ryuk from removing it, so the plugin is
        // downloaded once per machine rather than once per run.
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
            .WithName("ashamray-test-broker-ordering:4.1-delayed")
            .WithCleanUp(false)
            .Build();

        // Constructed before the build rather than after it, so the ordinary
        // failure — a checksum mismatch, an unreachable release — leaves a
        // container for the teardown to dispose. That was the first fix and it
        // was not enough on its own: BrokerContextPath() and the builder chain
        // above both run earlier and both can throw, which is why the teardown
        // is null-safe as well. Two guards, because the field is assigned in
        // the middle of a method that can fail on either side of it.
        // The service's OWN broker account, not `guest` (#44). The image above
        // carries definitions.json, so this container starts with exactly the
        // permissions §14.1's broker grants `ordering-svc` — which is what
        // makes the ACL something `dotnet test` exercises rather than something
        // only a Compose stack has ever run under. A permission too narrow for
        // a receive endpoint fails HERE, on the branch that narrowed it.
        //
        // These two literals and deploy/compose/rabbitmq/definitions.json are
        // one credential in two files, which is the shape this repository
        // otherwise refuses. It is accepted on §14.1's local-development
        // exception — the same one carrying `admin`/`admin` for Keycloak: a
        // documented local default is not a secret. **Not "the reason §14.1
        // accepts `guest`/`guest`", which is what this comment said until
        // ADR-036 deleted that account** — a source comment resting on a
        // rationale the blueprint has since reversed is the one-rule failure
        // at its quietest, because nothing compiles a comment.
        //
        // The alternative is unavailable rather than merely worse: the
        // definitions file holds a salted hash, so nothing can recover the
        // password from it to hand to the container.
        _rabbit = new RabbitMqBuilder()
            .WithImage(broker)
            .WithUsername("ordering-svc")
            .WithPassword("local-dev-ordering")
            .Build();

        await broker.CreateAsync(TestContext.Current.CancellationToken);

        // Together, §12.4's printed shape — the broker's start hides inside
        // SQL Server's, which is the slower of the two by some margin. The
        // image build above is deliberately NOT inside that overlap: a
        // container cannot start before its image exists, and hiding the build
        // behind SQL Server's start would only move where the wait is
        // reported.
        await Task.WhenAll(
            _sql.StartAsync(TestContext.Current.CancellationToken),
            _rabbit.StartAsync(TestContext.Current.CancellationToken),
            _redisCache.StartAsync(TestContext.Current.CancellationToken),
            _redisCoordination.StartAsync(TestContext.Current.CancellationToken));

        await WidenWriteForTheHarnessAsync();

        // The container hands out a connection to master; Ordering owns a
        // database of its own (§7.1), and MigrateAsync is what creates it.
        // DbConnectionStringBuilder out of habit rather than necessity now:
        // this project does carry the provider package, for the open
        // SqlConnection Respawn inspects in ResetAsync.
        DbConnectionStringBuilder connection = new() { ConnectionString = _sql.GetConnectionString() };
        connection["Database"] = "Ordering";
        ConnectionString = connection.ConnectionString;

        FirstRunExitCode = await RunMigratorAsync(ConnectionString);

        // Both Redis connections, because AddRedisConnections reads both
        // eagerly (§8.1) — and real ones rather than the factory's unreachable
        // default, because §8.5's behaviour claims a key on every protected
        // command this suite dispatches.
        Factory = new OrderingApiFactory(
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
            CREATE TABLE ordering.TransactionProbe
            (
                Id   uniqueidentifier NOT NULL PRIMARY KEY,
                Note nvarchar(100)    NOT NULL
            );
            """);
    }

    /// <summary>
    /// §12.4's reset: truncation over the <c>ordering</c> schema, far faster
    /// than recreating it and honest where a rolled-back transaction would
    /// hide transaction-related bugs. Tests that share the collection call
    /// this from <c>InitializeAsync</c>; suites asserting the migrator or the
    /// probe table arrange per-test identities instead and never need it.
    /// <para>
    /// <b>It resets SQL and cannot reset the broker, and one consequence is
    /// latent rather than theoretical.</b> <c>Unschedule</c> is a no-op on
    /// ADR-021's scheduler, so every saga test leaves its timeouts armed in
    /// the collection-wide RabbitMQ. One landing mid-run would cross
    /// <c>InboxFilter</c> and write a row — and <c>InboxFilterTests</c>
    /// asserts <c>ShouldBeEmpty()</c> over the whole table, in this same
    /// collection. What stops it is only that the shortest schedule is five
    /// minutes and this collection runs in about eighty seconds. **A runner
    /// four times slower makes that a flake in a test that has nothing to do
    /// with sagas**, so read the PR-21 entry in the decision log before
    /// chasing it. Copilot raised it; the fix is a broker per saga class and
    /// was judged too expensive for the hazard.
    /// </para>
    /// <para>
    /// <b>It deadlocks against whatever is still consuming, and the retry below
    /// is the only honest answer this fixture has.</b> Respawn deletes every
    /// row in the <c>ordering</c> schema in its own dependency order while a
    /// consumer from the previous test may still be committing — and since
    /// ADR-032 the saga's consume transaction is longer, multi-table
    /// (<c>InboxState</c>, <c>OutboxMessage</c>, <c>OrderFulfilmentStates</c>)
    /// and <c>Serializable</c>. Two multi-table transactions taking locks in
    /// different orders is a deadlock, and SQL Server picks a victim:
    /// <c>SqlException</c> 1205 out of this method, in whichever test happened
    /// to reset next and therefore in tests with nothing to do with sagas.
    /// <para>
    /// <b>Two things were tried and only one of them was the fix, which is
    /// worth recording because the wrong one was argued convincingly.</b>
    /// ADR-032 also registers a hosted
    /// <c>InboxCleanupService&lt;OrderingDbContext&gt;</c>, and
    /// <see cref="OrderingApiFactory"/> now removes it on the same argument it
    /// already removed the outbox dispatcher and the retention purge. That
    /// removal is right and it is <b>not</b> what closes this: the deadlock
    /// reproduced with the cleanup service gone. A revision of this comment
    /// claimed "there is no second deleter to race" and deleted the retry on
    /// the strength of it — **a deadlock needs two transactions with opposing
    /// lock order, not two deleters**, and the claim was reasoned rather than
    /// run. Six runs of the suite reproduced it on the second.
    /// </para>
    /// <para>
    /// So the retry stays, bounded, on 1205 and nothing else. <b>This
    /// particular</b> race cannot happen in production — nothing there deletes
    /// a schema — so a fixture's race is answered in the fixture, by rerunning,
    /// which is what SQL Server's own message asks for. That is narrower than
    /// "deadlocks cannot happen in production", which nothing here establishes
    /// and a review pass declined to let this comment claim: MassTransit's own
    /// cleanup deletes <c>InboxState</c> while a consume transaction locks a
    /// row there and then inserts into <c>OutboxMessage</c>, which is an
    /// opposing order. What differs is the consequence — a faulted message the
    /// endpoint retries, rather than a reset that fails a test.
    /// <para>
    /// Draining harder is the alternative and does not reach it: a test can
    /// wait for the deliveries it published, and the saga's own sends are
    /// second-order deliveries it never named.
    /// </para>
    /// </para>
    /// </para>
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
                SchemasToInclude = ["ordering"]
            });

        // Bounded, and small on purpose: a reset that loses twice in a row is
        // not the race this handles and should be seen. Rethrowing on the last
        // attempt keeps the original exception rather than a wrapper naming the
        // retry instead of the deadlock.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await _respawner.ResetAsync(connection);
                return;
            }
            catch (SqlException e) when (e.Number == DeadlockVictim && attempt < ResetAttempts)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(100 * attempt),
                    TestContext.Current.CancellationToken);
            }
        }
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
            .. Setting("ConnectionStrings:OrderingMigrator", migratorConnectionString),
            .. Setting("ConnectionStrings:Ordering", runtimeConnectionString)
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
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

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
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        return await db.Database
            .SqlQueryRaw<T>(sql, parameters)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The migrations EF considers applied. Asked through EF rather than by
    /// selecting from <c>__EFMigrationsHistory</c>, so the assertion is about
    /// what that table holds and not about where it lives — which is EF's to
    /// decide, is configured by <c>MigrationsHistoryTable</c> rather than by
    /// this context's <c>HasDefaultSchema</c>, and is no part of what this
    /// fixture claims.
    /// </summary>
    public async Task<string[]> AppliedMigrationsAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

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

    /// <summary>
    /// Persists a real aggregate through the DbContext, so the row satisfies
    /// every invariant §5 enforces. A raw INSERT drifts from the aggregate the
    /// first time it gains a column, and drifts silently.
    /// </summary>
    /// <remarks>
    /// The events are cleared before saving: a seeded order is a fixture
    /// rather than a thing that happened, and leaving them staged would put
    /// outbox rows under assertions that are not about the outbox. A test
    /// that wants the events seeds through the write path instead.
    /// </remarks>
    public async Task<Guid> SeedOrderAsync(Guid customerId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        Order order = Order.Place(
            new CustomerId(customerId),
            Address.Of("1 Test Street", null, "Almaty", "050000", "KZ"),
            [(ProductId.New(), 1, Money.Of(19.99m, "EUR"))],
            "EUR",
            DateTimeOffset.UtcNow);
        order.ClearDomainEvents();

        db.Orders.Add(order);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return order.Id.Value;
    }

    /// <summary>Every outbox row, untracked, for asserting over.</summary>
    public async Task<IReadOnlyList<OutboxMessage>> OutboxAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        return await db.OutboxMessages
            .AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Writes rows directly, for tests about the dispatcher rather than the staging.</summary>
    public async Task StageOutboxAsync(params OutboxMessage[] rows)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

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
            "UPDATE ordering.OutboxMessages SET Attempts = {0} WHERE MessageId = {1};",
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
            "UPDATE ordering.OutboxMessages SET Lane = {0} WHERE MessageId = {1};",
            lane.ToString(),
            messageId);

    /// <summary>
    /// Clears retry backoff leases so the next pass is gated only by the
    /// attempt cap. Lets a test distinguish "backed off" from "abandoned"
    /// without sleeping.
    /// </summary>
    public Task ExpireOutboxLeasesAsync() =>
        ExecuteAsync("UPDATE ordering.OutboxMessages SET LockedUntil = NULL WHERE ProcessedAt IS NULL;");

    /// <summary>Every inbox row, untracked, for asserting over (§9.5).</summary>
    public async Task<IReadOnlyList<InboxMessage>> InboxAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

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
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

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
            "UPDATE ordering.OutboxMessages SET ProcessedAt = {0} WHERE MessageId = {1};",
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
        ScalarAsync<int>("SELECT Value = COUNT(*) FROM ordering.TransactionProbe WHERE Id = {0}", id);

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
                // Null when InitializeAsync threw before the container was
                // built — a missing Dockerfile, an unreadable build context, a
                // failed image build. xUnit disposes a fixture whose
                // initialisation threw, so dereferencing here would replace
                // that diagnosis with a NullReferenceException. Moving the
                // assignment earlier was the first attempt and did not close
                // it: BrokerContextPath() and the builder chain both run
                // before the assignment, and both can throw.
                try
                {
                    if (_rabbit is not null)
                        await _rabbit.DisposeAsync();
                }
                finally
                {
                    // Nested on the same argument as every layer above it: a
                    // failed broker disposal must not leave two Redis
                    // containers running for the rest of the CI job. These
                    // need no null guard — they are field initialisers, so
                    // they exist before InitializeAsync can throw.
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
