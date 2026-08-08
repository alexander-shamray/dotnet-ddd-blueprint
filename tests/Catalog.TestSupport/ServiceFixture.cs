using System.Data.Common;
using Catalog.Infrastructure.Persistence;
using Catalog.Migrator;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respawn;
using Testcontainers.MsSql;
using Xunit;

namespace Catalog.TestSupport;

/// <summary>
/// A real SQL Server, migrated by the real migrator (ADR-010, §12.4). The
/// image is the one §14.1's Compose file runs, so a test and a developer
/// machine cannot disagree about the engine. §12.4's name and §4.1's home:
/// the fixture serves <c>Catalog.Application.Tests</c> and
/// <c>Catalog.Api.Tests</c>, which cannot reference each other — each
/// declares its own <c>IntegrationCollection</c> over this one type. SQL
/// only, today: the Redis and RabbitMQ containers of §12.4's full shape join
/// with the PRs whose code touches them.
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

    private Respawner? _respawner;

    /// <summary>
    /// The connection each §7.1 identity would hold, pointed at Catalog's own
    /// database rather than the container's <c>master</c>.
    /// </summary>
    public string ConnectionString { get; private set; } = null!;

    public CatalogApiFactory Factory { get; private set; } = null!;

    /// <summary>The exit code of the first real migration run.</summary>
    public int FirstRunExitCode { get; private set; } = -1;

    // ValueTask, not Task: xUnit v3 redefined IAsyncLifetime (§12.4).
    public async ValueTask InitializeAsync()
    {
        await _sql.StartAsync(TestContext.Current.CancellationToken);

        // The container hands out a connection to master; Catalog owns a
        // database of its own (§7.1), and MigrateAsync is what creates it.
        // DbConnectionStringBuilder rather than SqlConnectionStringBuilder, so
        // this project needs no provider package of its own.
        DbConnectionStringBuilder connection = new() { ConnectionString = _sql.GetConnectionString() };
        connection["Database"] = "Catalog";
        ConnectionString = connection.ConnectionString;

        FirstRunExitCode = await RunMigratorAsync(ConnectionString);

        Factory = new CatalogApiFactory(ConnectionString);

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
        _respawner ??= await Respawner.CreateAsync(connection, new RespawnerOptions
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

    /// <summary>Runs a statement outside any unit of work, for arranging.</summary>
    public async Task ExecuteAsync(string sql)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        CatalogDbContext db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        await db.Database.ExecuteSqlRawAsync(sql, TestContext.Current.CancellationToken);
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

    /// <summary>Rows the transaction probe holds for one id.</summary>
    public Task<int> ProbeRowCountAsync(Guid id) =>
        ScalarAsync<int>("SELECT Value = COUNT(*) FROM catalog.TransactionProbe WHERE Id = {0}", id);

    public async ValueTask DisposeAsync()
    {
        Factory?.Dispose();
        await _sql.DisposeAsync();
    }
}
