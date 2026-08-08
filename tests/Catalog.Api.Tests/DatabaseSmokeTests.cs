using System.Net;
using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>
/// PR-08's deliverables against a real engine: the migrator applies the schema
/// and reports it, the readiness check of §13.5 answers from a database that is
/// actually up, and <c>EfUnitOfWork</c> commits and rolls back the way §6.3
/// says it does.
/// </summary>
/// <remarks>
/// These tests require Docker and are deliberately not skipped without it.
/// ADR-010 already made real infrastructure non-optional; a skip would let CI
/// go green on a runner whose daemon had broken, and a category is PR-22's
/// deliverable rather than something to invent two PRs early.
/// </remarks>
public class DatabaseSmokeTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    [Fact]
    public async Task Migrator_exits_zero_and_creates_the_schema()
    {
        // The fixture ran the real host against an empty server, so this is the
        // §7.4 job's own outcome rather than a re-enactment of it.
        fixture.FirstRunExitCode.ShouldBe(0);

        int schema = await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM sys.schemas WHERE name = 'catalog'");
        schema.ShouldBe(1, "InitialCreate's hand-written EnsureSchema is the only thing that creates it");

        string[] applied = await fixture.AppliedMigrationsAsync();
        applied.ShouldHaveSingleItem().ShouldEndWith("_InitialCreate");
    }

    [Fact]
    public async Task Migrating_twice_applies_nothing_and_still_exits_zero()
    {
        // §7.4 runs this as a pre-install/pre-upgrade hook, so it reruns on
        // every deploy. Applying nothing is a successful outcome, and a job
        // that failed here would block every deploy after the first.
        int exitCode = await SqlServerFixture.RunMigratorAsync(fixture.ConnectionString);

        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task Migrator_fails_when_only_the_runtime_connection_string_is_set()
    {
        // §7.1's split is two principals with different rights, and it is a
        // boundary only while the migrator reads its own key. Handing it the
        // runtime connection under the runtime name must not work — if it did,
        // the two connection strings would be a naming convention.
        int exitCode = await SqlServerFixture.RunMigratorAsync(
            migratorConnectionString: null,
            runtimeConnectionString: fixture.ConnectionString);

        exitCode.ShouldBe(1);
    }

    [Fact]
    public async Task Ready_probe_returns_200_against_a_real_database()
    {
        using HttpClient client = fixture.Factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ExecuteAsync_commits_a_raw_write_when_the_operation_succeeds()
    {
        Guid id = Guid.CreateVersion7();

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.ExecuteAsync(
            async ct =>
            {
                await InsertProbeAsync(unitOfWork, id, ct);
                return Result.Success();
            },
            TestContext.Current.CancellationToken);

        int rows = await fixture.ProbeRowCountAsync(id);
        rows.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_rolls_back_a_raw_write_when_the_operation_fails()
    {
        // The guard §6.3 puts in EfUnitOfWork rather than in the behaviour, and
        // the reason it is there: TransactionBehavior declining to SaveChanges
        // covers everything EF tracks, and covers nothing that ExecuteRawAsync
        // has already sent down the connection. Only the rollback takes that
        // back, so this is the route PR-09's behaviour cannot test for itself.
        Guid id = Guid.CreateVersion7();

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        Result result = await unitOfWork.ExecuteAsync(
            async ct =>
            {
                await InsertProbeAsync(unitOfWork, id, ct);
                return Result.Failure(Error.Rule("probe.rejected", "The operation rejected the command."));
            },
            TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();

        int rows = await fixture.ProbeRowCountAsync(id);
        rows.ShouldBe(0, "a rejected command must leave no row behind, by either route");
    }

    [Fact]
    public async Task ExecuteRawAsync_outside_a_unit_of_work_throws_rather_than_autocommitting()
    {
        // Without the guard this call succeeds: Dapper is handed a null
        // transaction, SQL Server autocommits, and the row is durable outside
        // any unit — the dual write ExecuteRawAsync exists to prevent. The
        // assertion is therefore both halves, the throw and the empty table.
        Guid id = Guid.CreateVersion7();

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        unitOfWork.HasActiveTransaction.ShouldBeFalse();

        await Should.ThrowAsync<InvalidOperationException>(
            () => InsertProbeAsync(unitOfWork, id, TestContext.Current.CancellationToken));

        int rows = await fixture.ProbeRowCountAsync(id);
        rows.ShouldBe(0, "the guard must refuse the write, not merely report it afterwards");
    }

    [Fact]
    public async Task HasActiveTransaction_is_false_outside_the_unit_and_true_inside_it()
    {
        // The guard PR-09's behaviour reads to avoid opening a second
        // transaction on a nested dispatch. It is one property and it is
        // invisible until something depends on it, which is why it is pinned
        // here rather than discovered there.
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        unitOfWork.HasActiveTransaction.ShouldBeFalse();

        bool inside = await unitOfWork.ExecuteAsync(
            _ => Task.FromResult(unitOfWork.HasActiveTransaction),
            TestContext.Current.CancellationToken);

        inside.ShouldBeTrue();
        unitOfWork.HasActiveTransaction.ShouldBeFalse("the unit disposes its transaction on the way out");
    }

    private static Task InsertProbeAsync(IUnitOfWork unitOfWork, Guid id, CancellationToken ct) =>
        unitOfWork.ExecuteRawAsync(
            "INSERT INTO catalog.TransactionProbe (Id, Note) VALUES (@Id, @Note)",
            new { Id = id, Note = "written through IUnitOfWork" },
            ct);
}
