# PR-09 — TransactionBehavior over IUnitOfWork — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land §6.3's `TransactionBehavior` in `Common.Application`, wire it as
Catalog's third pipeline behaviour, and close PR #15's retry double-apply with
`ChangeTracker.Clear()` — proven by the three tests Appendix C names plus a
transient-fault retry test.

**Architecture:** The behaviour depends only on two ports — `IUnitOfWork`
(landed with PR-08) and `IDomainEventDispatcher`, whose interface comes forward
from §7.5 with nothing behind it but a Catalog-local null object until PR-14's
outbox. The EF change tracker is cleared at the top of every retry attempt so
each attempt starts from committed state.

**Tech Stack:** .NET 10 / C# 14, xUnit v3 + Shouldly, Testcontainers.MsSql,
EF Core (SqlServer) — all already pinned in `Directory.Packages.props`.

## Global Constraints

- The spec: `docs/superpowers/specs/2026-08-08-pr09-transaction-behavior-design.md`.
  Where it and the blueprint disagree, the blueprint wins.
- `TransactionBehavior`, `IDomainEventDispatcher` and the amended `EfUnitOfWork`
  must match §6.3/§7.5's samples **verbatim** — code and sample are one
  artefact (CLAUDE.md, "the one rule that matters").
- `Common.Application` must NOT gain a reference to `Common.Domain` — no new
  signature may name a domain type. The architecture gates and the csproj
  comment both assert this.
- House style is a build input: explicit types except the four `var` cases,
  file-scoped namespaces, blank line after `namespace X;`, IDE0055 clean,
  no `#pragma`, no `Version=` on any `PackageReference`, CRLF, 120-column
  budget, test names as underscore sentences.
- `dotnet test Platform.slnx` needs a running Docker daemon (PR-08 rule);
  the container tests fail truthfully without one.
- Commits are semantic and present-tense; each carries an arguing body.

---

### Task 1: The behaviour and its two new types, test-driven

**Files:**
- Create: `src/BuildingBlocks/Common.Application/IDomainEventDispatcher.cs`
- Create: `src/BuildingBlocks/Common.Application/InvariantViolationException.cs`
- Create: `src/BuildingBlocks/Common.Application/TransactionBehavior.cs`
- Create: `tests/Common.Application.Tests/FakeUnitOfWork.cs`
- Create: `tests/Common.Application.Tests/FakeDomainEventDispatcher.cs`
- Create: `tests/Common.Application.Tests/TransactionBehaviorTests.cs`
- Modify: `tests/Common.Application.Tests/TestRequests.cs` (add `Approve`)

**Interfaces:**
- Consumes: `IUnitOfWork`, `IPipelineBehavior<,>`/`NextDelegate<T>`,
  `ICommand<T>`, `Result`, `Error`, `TestContainer.Build`, `PipelineLog`,
  the existing `Ping`/`Ask`/`Reject` test requests.
- Produces: `IDomainEventDispatcher` (member
  `Task DispatchAsync(CancellationToken ct)`),
  `InvariantViolationException(string message)`,
  `TransactionBehavior<TCommand, TResult>(IUnitOfWork, IDomainEventDispatcher)`
  constrained `where TCommand : ICommand<TResult>` — Tasks 2–4 register and
  invoke these exact names.

- [ ] **Step 1: Write the two ports** (the tests cannot name them otherwise —
  they are §7.5/D.5 types copied in, not design work)

`src/BuildingBlocks/Common.Application/IDomainEventDispatcher.cs` — §7.5
verbatim, plus the remark PR-09 owes the reader:

```csharp
namespace Common.Application;

public interface IDomainEventDispatcher
{
    /// <summary>
    /// Collects raised domain events and stages outbox rows for them — the
    /// allow-listed ones on the Broker lane, those with projection handlers on
    /// the Local lane. Runs no handlers. Called by TransactionBehavior inside
    /// the transaction, before SaveChanges.
    /// </summary>
    /// <remarks>
    /// The port arrives with PR-09 because §6.3's behaviour cannot compile
    /// without it; everything §7.5 builds behind it — the collector, the
    /// registry, the real dispatcher — arrives with the outbox. No member
    /// names a domain type, which is why this file draws no edge to
    /// <c>Common.Domain</c> (see <see cref="IUnitOfWork"/> for the argument).
    /// </remarks>
    Task DispatchAsync(CancellationToken ct);
}
```

`src/BuildingBlocks/Common.Application/InvariantViolationException.cs`:

```csharp
namespace Common.Application;

/// <summary>
/// Thrown when a command modifies more than one aggregate root (§2.3,
/// principle 3). Raised by <c>TransactionBehavior</c> at the transaction
/// boundary — the violation is not structural, so no architecture test can
/// catch it; the first execution that does it fails instead, with the command
/// name and the count in the message (§6.3).
/// </summary>
public sealed class InvariantViolationException(string message) : Exception(message);
```

- [ ] **Step 2: Add the missing test request** — `Reject` covers the failure
  arm; the success arm needs a `Result`-returning command that succeeds.
  Append to `tests/Common.Application.Tests/TestRequests.cs` after the
  `RejectHandler` block:

```csharp
/// <summary>
/// A command the domain accepts, returning the non-generic <c>Result</c> —
/// the success arm of §6.3's failure guard, `Reject`'s counterpart.
/// </summary>
public sealed record Approve : ICommand<Result>;

public sealed class ApproveHandler : ICommandHandler<Approve, Result>
{
    public Task<Result> HandleAsync(Approve command, CancellationToken ct) =>
        Task.FromResult(Result.Success());
}
```

- [ ] **Step 3: Write the fakes.** Both write to the scoped `PipelineLog` so
  one log carries the cross-fake ordering the tests assert.

`tests/Common.Application.Tests/FakeUnitOfWork.cs`:

```csharp
namespace Common.Application.Tests;

/// <summary>
/// A recording <see cref="IUnitOfWork"/>. Every member the behaviour touches
/// writes to the shared <see cref="PipelineLog"/>, so ordering across this
/// fake and <see cref="FakeDomainEventDispatcher"/> is one assertion — the
/// behaviour's whole contract is a sequence, and two separate logs would let
/// the sequence lie.
/// </summary>
public sealed class FakeUnitOfWork(PipelineLog log) : IUnitOfWork
{
    /// <summary>What <see cref="ModifiedAggregateCount"/> reports.</summary>
    public int AggregateCount { get; set; }

    public bool HasActiveTransaction { get; set; }

    public int ModifiedAggregateCount
    {
        get
        {
            log.Add("count");
            return AggregateCount;
        }
    }

    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken ct)
    {
        log.Add("execute");
        return await operation(ct);
    }

    public Task<int> SaveChangesAsync(CancellationToken ct)
    {
        log.Add("save");
        return Task.FromResult(0);
    }

    public Task ExecuteRawAsync(string sql, object parameters, CancellationToken ct)
    {
        log.Add("raw");
        return Task.CompletedTask;
    }
}
```

`tests/Common.Application.Tests/FakeDomainEventDispatcher.cs`:

```csharp
namespace Common.Application.Tests;

/// <summary>Records the dispatch into the shared pipeline log.</summary>
public sealed class FakeDomainEventDispatcher(PipelineLog log) : IDomainEventDispatcher
{
    public Task DispatchAsync(CancellationToken ct)
    {
        log.Add("dispatch");
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 4: Write the failing tests.**
  `tests/Common.Application.Tests/TransactionBehaviorTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Common.Application.Tests;

/// <summary>
/// §6.3's behaviour against a recording unit of work. The contract is a
/// sequence — enter the unit, run the handler, dispatch, count, save — and
/// every test here is an assertion about which of those happened and in what
/// order, which is why the fakes share one <see cref="PipelineLog"/>.
/// </summary>
public class TransactionBehaviorTests
{
    [Fact]
    public async Task A_successful_command_enters_the_unit_dispatches_then_saves_once()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Result result = await Dispatch(scope, new Approve());

        result.IsSuccess.ShouldBeTrue();
        Log(scope).Entries.ShouldBe(
            ["execute", "dispatch", "count", "save"],
            "dispatch before save is what puts outbox rows into the same save, " +
            "and dispatch before the count is §6.3's own comment");
    }

    [Fact]
    public async Task A_failed_result_skips_dispatch_and_save()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Result result = await Dispatch(scope, new Reject());

        result.IsFailure.ShouldBeTrue();
        Log(scope).Entries.ShouldBe(
            ["execute"],
            "a rejected command must not stage events or save — §6.3's failure guard");
    }

    [Fact]
    public async Task A_command_whose_result_is_not_a_Result_commits_normally()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        string result = await dispatcher.SendAsync(new Ping("hi"), TestContext.Current.CancellationToken);

        result.ShouldBe("pong:hi");
        Log(scope).Entries.ShouldBe(
            ["execute", "dispatch", "count", "save"],
            "the failure guard is a pattern match on Result; any other result type commits");
    }

    [Fact]
    public async Task A_nested_dispatch_runs_inside_the_open_unit_without_a_second_one()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<FakeUnitOfWork>().HasActiveTransaction = true;

        Result result = await Dispatch(scope, new Approve());

        result.IsSuccess.ShouldBeTrue();
        Log(scope).Entries.ShouldBeEmpty(
            "already inside a transaction, the behaviour is a passthrough — " +
            "the outer dispatch owns the dispatch-count-save sequence");
    }

    [Fact]
    public async Task A_command_modifying_two_aggregates_throws_and_saves_nothing()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<FakeUnitOfWork>().AggregateCount = 2;

        InvariantViolationException exception =
            await Should.ThrowAsync<InvariantViolationException>(() => Dispatch(scope, new Approve()));

        exception.Message.ShouldContain(nameof(Approve));
        exception.Message.ShouldContain("2");
        Log(scope).Entries.ShouldBe(
            ["execute", "dispatch", "count"],
            "the guard fires after dispatch — §6.3 counts staged rows too — and before save");
    }

    [Fact]
    public async Task A_throwing_handler_saves_nothing()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await Should.ThrowAsync<InvalidOperationException>(
            () => dispatcher.SendAsync(new Boom(), TestContext.Current.CancellationToken));

        Log(scope).Entries.ShouldBe(
            ["execute"],
            "an exception unwinds through the unit; nothing is dispatched or saved");
    }

    [Fact]
    public async Task A_query_never_touches_the_unit_of_work()
    {
        // Appendix C's third test, on the real type: CommandOnlyBehavior
        // proved the container honours constraints, and this proves
        // TransactionBehavior actually carries one.
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        string result = await dispatcher.QueryAsync(new Ask("why"), TestContext.Current.CancellationToken);

        result.ShouldBe("answer:why");
        Log(scope).Entries.ShouldBeEmpty(
            "a query opening a transaction is the defect this catches — §6.3");
    }

    private static ServiceProvider BuildProvider() =>
        TestContainer.Build(services =>
        {
            services.AddScoped<FakeUnitOfWork>();
            services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<FakeUnitOfWork>());
            services.AddScoped<IDomainEventDispatcher, FakeDomainEventDispatcher>();
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));
        });

    private static Task<Result> Dispatch(IServiceScope scope, ICommand<Result> command) =>
        scope.ServiceProvider
            .GetRequiredService<IDispatcher>()
            .SendAsync(command, TestContext.Current.CancellationToken);

    private static PipelineLog Log(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<PipelineLog>();
}
```

- [ ] **Step 5: Run to verify the suite fails to compile** (no
  `TransactionBehavior` yet):
  `dotnet test Platform.slnx --filter "FullyQualifiedName~TransactionBehaviorTests"`
  — expected: build error `CS0246: TransactionBehavior` not found.

- [ ] **Step 6: Write the behaviour** — §6.3 verbatim.
  `src/BuildingBlocks/Common.Application/TransactionBehavior.cs`:

```csharp
namespace Common.Application;

public sealed class TransactionBehavior<TCommand, TResult>(IUnitOfWork unitOfWork, IDomainEventDispatcher domainEvents)
    : IPipelineBehavior<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<TResult> HandleAsync(TCommand command, NextDelegate<TResult> next, CancellationToken ct)
    {
        // Already inside a transaction (nested dispatch) — do not open another.
        if (unitOfWork.HasActiveTransaction)
            return await next();

        return await unitOfWork.ExecuteAsync(async token =>
        {
            TResult result = await next();

            // A handler that returns a failed Result has rejected the command.
            // Returning here skips both the staging and the save, so the
            // transaction commits nothing and no outbox row announces a state
            // change that did not happen. Result<T> derives from Result, so one
            // pattern covers every command shape without reflection.
            if (result is Result { IsFailure: true })
                return result;

            // Stages outbox rows only — no handler runs here (§7.5).
            // Reactions happen after commit, driven by the outbox.
            await domainEvents.DispatchAsync(token);

            // Principle 3 (§2.3), asserted rather than trusted — see below for
            // why it is here and not in a code review checklist. After
            // dispatch, so the staged rows of a legitimate single-root command
            // are already in the tracker and not miscounted.
            if (unitOfWork.ModifiedAggregateCount > 1)
                throw new InvariantViolationException(
                    $"{typeof(TCommand).Name} modified {unitOfWork.ModifiedAggregateCount} " +
                    "aggregate roots. One transaction, one aggregate (§2.3 principle 3) — " +
                    "the second aggregate should react to a domain event after commit (§7.5).");

            await unitOfWork.SaveChangesAsync(token);

            return result;
        }, ct);
    }
}
```

If an analyser rejects any line (the interpolated message is the candidate —
CA1305), fix the code minimally **and amend §6.3's sample in the same commit**;
the two must not diverge. Do not suppress.

- [ ] **Step 7: Run the new suite** — same filter as Step 5. Expected: all 7
  PASS. Then the whole project:
  `dotnet test Platform.slnx --filter "FullyQualifiedName~Common.Application.Tests"`
  — expected: PASS, no regressions.

- [ ] **Step 8: Commit**

```bash
git add src/BuildingBlocks/Common.Application tests/Common.Application.Tests
git commit -m "feat(common): TransactionBehavior over IUnitOfWork"
```

Body: the §6.3 sequence, the two new ports, and the no-Domain-edge argument.

---

### Task 2: Catalog wiring — third behaviour, null dispatcher

**Files:**
- Modify: `src/Services/Catalog/Catalog.Application/DependencyInjection.cs:26-30`
- Create: `src/Services/Catalog/Catalog.Infrastructure/NullDomainEventDispatcher.cs`
- Modify: `src/Services/Catalog/Catalog.Infrastructure/DependencyInjection.cs:39`
- Test: `tests/Catalog.Application.Tests/DependencyInjectionTests.cs:62-79`

**Interfaces:**
- Consumes: `TransactionBehavior<,>` and `IDomainEventDispatcher` from Task 1.
- Produces: `AddCatalogApplication` registering three behaviours in order;
  `AddCatalogInfrastructure` resolving `IDomainEventDispatcher` — Task 3's
  container test resolves both from the real host.

- [ ] **Step 1: Make the registration test expect three.** In
  `DependencyInjectionTests.cs` rename
  `AddCatalogApplication_registers_the_two_behaviours_in_pipeline_order` to
  `AddCatalogApplication_registers_the_three_behaviours_in_pipeline_order` and
  replace the assertion:

```csharp
        behaviours.ShouldBe(
            [
                typeof(LoggingBehavior<,>),
                typeof(ValidationBehavior<,>),
                typeof(TransactionBehavior<,>)
            ],
            "three of four — IdempotencyBehavior joins with its PR, between Validation and Transaction (§6.3)");
```

- [ ] **Step 2: Run it to verify it fails:**
  `dotnet test Platform.slnx --filter "FullyQualifiedName~Catalog.Application.Tests.DependencyInjectionTests"`
  — expected: FAIL, two behaviours found where three are asserted.

- [ ] **Step 3: Register the behaviour.** In
  `Catalog.Application/DependencyInjection.cs`, replace the comment's second
  sentence and add the line:

```csharp
        // Ordered, explicit, not scanned — registration order is pipeline
        // order (§6.3). Three of four: IdempotencyBehavior joins with the PR
        // that builds it, and slots in between Validation and Transaction.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));
```

- [ ] **Step 4: Write the null dispatcher.**
  `src/Services/Catalog/Catalog.Infrastructure/NullDomainEventDispatcher.cs`:

```csharp
using Common.Application;

namespace Catalog.Infrastructure;

/// <summary>
/// The truthful <see cref="IDomainEventDispatcher"/> while Catalog can raise
/// no domain event: the domain has no aggregate until PR-10, and there is no
/// outbox to stage into until PR-14. PR-14's real dispatcher replaces this
/// class — from PR-10 until then, any event an aggregate raises is dropped
/// here, which that PR's slice must weigh.
/// </summary>
internal sealed class NullDomainEventDispatcher : IDomainEventDispatcher
{
    public Task DispatchAsync(CancellationToken ct) => Task.CompletedTask;
}
```

- [ ] **Step 5: Register it.** In `Catalog.Infrastructure/DependencyInjection.cs`,
  under the `EfUnitOfWork` line:

```csharp
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();                     // §6.3
        services.AddScoped<IDomainEventDispatcher, NullDomainEventDispatcher>();   // §7.5, null until PR-14
```

  Explicit rather than scanned, like the real one in §7.5's sample.

- [ ] **Step 6: Run the registration suite and the host smoke:**
  `dotnet test Platform.slnx --filter "FullyQualifiedName~Catalog.Application.Tests|FullyQualifiedName~HostSmokeTests"`
  — expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Services/Catalog tests/Catalog.Application.Tests
git commit -m "feat(catalog): register TransactionBehavior third, over a null dispatcher"
```

---

### Task 3: Container test — the behaviour drives the rollback

**Files:**
- Modify: `tests/Catalog.Api.Tests/DatabaseSmokeTests.cs` (add one test + one record)

**Interfaces:**
- Consumes: `TransactionBehavior<,>` (Task 1), the `IDomainEventDispatcher`
  registration (Task 2), `SqlServerFixture`, `InsertProbeAsync`,
  `ProbeRowCountAsync`.
- Produces: nothing later tasks use.

- [ ] **Step 1: Write the test.** Appendix C's second test, end to end —
  PR-08 proved `EfUnitOfWork` rolls a raw write back; this proves the
  behaviour is what drives it. Add to `DatabaseSmokeTests`, after
  `ExecuteAsync_rolls_back_a_raw_write_when_the_operation_fails`:

```csharp
    [Fact]
    public async Task The_behaviour_leaves_no_row_when_a_handler_writes_raw_and_then_fails()
    {
        // Appendix C's PR-09 test, on the full §6.3 stack: the real behaviour
        // over the scope's real unit of work and the registered dispatcher,
        // with a handler that writes through ExecuteRawAsync and then rejects.
        // PR-08 proved EfUnitOfWork's half from the port; this proves the
        // behaviour is what opens the unit and declines the commit.
        Guid id = Guid.CreateVersion7();

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        IDomainEventDispatcher dispatcher =
            scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>();

        TransactionBehavior<ProbeCommand, Result> behaviour = new(unitOfWork, dispatcher);
        CancellationToken ct = TestContext.Current.CancellationToken;

        Result result = await behaviour.HandleAsync(
            new ProbeCommand(),
            async () =>
            {
                await InsertProbeAsync(unitOfWork, id, ct);
                return Result.Failure(Error.Rule("probe.rejected", "The handler rejected the command."));
            },
            ct);

        result.IsFailure.ShouldBeTrue();

        int rows = await fixture.ProbeRowCountAsync(id);
        rows.ShouldBe(0, "the behaviour must decline the commit, and the rollback must take the raw write");
    }
```

And at the bottom of the file, below the class:

```csharp
/// <summary>The command shape the behaviour's constraint requires — nothing more.</summary>
public sealed record ProbeCommand : ICommand<Result>;
```

- [ ] **Step 2: Run it** (Docker required):
  `dotnet test Platform.slnx --filter "FullyQualifiedName~The_behaviour_leaves_no_row"`
  — expected: PASS (the production code exists since Tasks 1–2; this test is
  the missing evidence, not missing behaviour — red-first here would mean
  deleting shipped code).

- [ ] **Step 3: Commit**

```bash
git add tests/Catalog.Api.Tests
git commit -m "test(catalog): the behaviour declines the commit and the rollback takes the raw write"
```

---

### Task 4: `ChangeTracker.Clear()` — code, doc and the retry test together

The code line, §6.3's amended sample and the note beside "may therefore run
more than once" are one artefact and land in one commit.

**Files:**
- Modify: `src/Services/Catalog/Catalog.Infrastructure/Persistence/EfUnitOfWork.cs:28-33`
- Modify: `docs/backend-architecture/06-cqrs.md` (EfUnitOfWork sample ~line 636;
  the "more than once" note ~line 693)
- Create: `tests/Catalog.Api.Tests/TransientFaultInjection.cs`
- Modify: `tests/Catalog.Api.Tests/DatabaseSmokeTests.cs` (add the retry test)
- Modify: `tests/Catalog.Api.Tests/Catalog.Api.Tests.csproj` (EF SqlServer ref)

**Interfaces:**
- Consumes: `AddCatalogInfrastructure(IConfiguration)`, `CatalogDbContext`
  (public), `SqlServerFixture.ConnectionString`, `ProbeRowCountAsync`.
- Produces: `FakeTransientException`, `MarkerRetryingStrategy` — test-local.

- [ ] **Step 1: Add the package reference** (strategy subclass needs
  `SqlServerRetryingExecutionStrategy`). In `Catalog.Api.Tests.csproj`, after
  the Testcontainers reference:

```xml
    <!-- SqlServerRetryingExecutionStrategy, for the subclass that lets the
         retry test inject a fault the strategy treats as transient. Already
         pinned centrally and in Appendix B via Catalog.Infrastructure. -->
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" />
```

- [ ] **Step 2: Write the fault-injection pair.**
  `tests/Catalog.Api.Tests/TransientFaultInjection.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Catalog.Api.Tests;

/// <summary>
/// The marker the retry test throws on its first attempt. A real transient
/// fault is a SqlException with one of a fixed set of numbers, and those are
/// not constructible; a strategy taught to retry this marker exercises the
/// same path without reflection over provider internals.
/// </summary>
public sealed class FakeTransientException : Exception;

/// <summary>
/// The production strategy plus one retriable exception type. Everything the
/// test proves — the delegate re-runs, the first attempt rolls back, one
/// commit — is the base class's behaviour, not this subclass's.
/// </summary>
public sealed class MarkerRetryingStrategy(ExecutionStrategyDependencies dependencies)
    : SqlServerRetryingExecutionStrategy(dependencies)
{
    protected override bool ShouldRetryOn(Exception exception) =>
        exception is FakeTransientException || base.ShouldRetryOn(exception);
}
```

- [ ] **Step 3: Write the retry test.** Add to `DatabaseSmokeTests`:

```csharp
    [Fact]
    public async Task A_transient_fault_retries_the_whole_unit_and_commits_it_once()
    {
        // PR #15's finding, half of it testable today: the strategy re-runs
        // the whole delegate, and attempt 1's work must not survive into the
        // commit — here the raw write, rolled back with its transaction. The
        // other half, attempt 2 reading attempt 1's tracked mutation from the
        // identity map, needs an entity type and lands with PR-10's first
        // aggregate; ChangeTracker.Clear() ships now so the sample never
        // teaches the defect.
        Guid id = Guid.CreateVersion7();
        int attempts = 0;

        ServiceCollection services = new();
        services.AddCatalogInfrastructure(new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Catalog"] = fixture.ConnectionString })
            .Build());

        // The same registration with one change: the strategy also retries
        // the marker. AddDbContext backs off registrations that exist, so the
        // stock options descriptor is removed first.
        ServiceDescriptor options =
            services.Single(d => d.ServiceType == typeof(DbContextOptions<CatalogDbContext>));
        services.Remove(options);
        services.AddDbContext<CatalogDbContext>(o =>
            o.UseSqlServer(
                fixture.ConnectionString,
                sql => sql.ExecutionStrategy(deps => new MarkerRetryingStrategy(deps))));

        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        CancellationToken ct = TestContext.Current.CancellationToken;

        Result result = await unitOfWork.ExecuteAsync(
            async token =>
            {
                attempts++;
                await InsertProbeAsync(unitOfWork, id, token);
                if (attempts == 1)
                    throw new FakeTransientException();
                return Result.Success();
            },
            ct);

        result.IsSuccess.ShouldBeTrue();
        attempts.ShouldBe(2, "the strategy must re-run the delegate, not surface the fault");

        int rows = await fixture.ProbeRowCountAsync(id);
        rows.ShouldBe(1, "attempt 1's write rolls back; attempt 2's commits exactly once");
    }
```

Add the usings the file now needs (`Microsoft.EntityFrameworkCore`,
`Microsoft.Extensions.Configuration`, `Catalog.Infrastructure`,
`Catalog.Infrastructure.Persistence`).

- [ ] **Step 4: Run it** — expected: **PASS already**, and that is the honest
  statement of this test's role: it pins the retry contract around the
  `Clear()` line, whose own observable defect needs PR-10's entity type. It
  must be seen green *before* the code change so a failure after Step 5 is
  attributable.

- [ ] **Step 5: Add the line.** In `EfUnitOfWork.ExecuteAsync`, first
  statement inside the strategy delegate:

```csharp
        return await strategy.ExecuteAsync(
            async token =>
            {
                // Every attempt starts from committed state. EF does not reset
                // the change tracker when a transaction rolls back, so without
                // this line a retry re-runs the domain method on attempt 1's
                // tracked, already-mutated aggregates out of the identity map,
                // and one SaveChanges commits the mutation twice.
                db.ChangeTracker.Clear();

                await using IDbContextTransaction tx =
                    await db.Database.BeginTransactionAsync(token);
```

- [ ] **Step 6: Amend §6.3 in the same change.** In
  `docs/backend-architecture/06-cqrs.md`, the `EfUnitOfWork` sample gains the
  identical six lines in the identical position. Then extend the
  "`CreateExecutionStrategy` is not optional" paragraph — after "which is
  another reason the outbox exists.", append:

> Outside the transaction is only half of it: **in-memory state survives a
> rollback too**, because EF does not reset the change tracker when a
> transaction fails. Without the `ChangeTracker.Clear()` above, attempt 2's
> load returns attempt 1's tracked, already-mutated aggregate from the
> identity map rather than the committed row, the domain method runs again,
> and one `SaveChanges` commits the mutation twice — the staged outbox rows
> survive and double-publish the same way. Clearing at the top of each attempt
> is the fix that keeps retry behind the port; a fresh `DbContext` per attempt
> would move it into the dispatcher and cost §6.3's behaviour the property
> that it depends on nothing but `IUnitOfWork`.

- [ ] **Step 7: Run the whole container suite** (Docker):
  `dotnet test Platform.slnx --filter "FullyQualifiedName~Catalog.Api.Tests"`
  — expected: PASS, including both PR-08 probe tests (the clear must not
  disturb them).

- [ ] **Step 8: Commit**

```bash
git add src/Services/Catalog/Catalog.Infrastructure tests/Catalog.Api.Tests docs/backend-architecture/06-cqrs.md
git commit -m "fix(catalog): every retry attempt starts from committed state"
```

Body: PR #15's finding, the one-line fix, why not a fresh context, and the
PR-10 handover for the identity-map assertion.

---

### Task 5: Reconcile CLAUDE.md, run everything, validate

**Files:**
- Modify: `CLAUDE.md` (phase section, `Common.Application` tree notes, test count)
- Check, expect no change: `docs/backend-architecture/appendix-d-type-inventory.md`,
  `appendix-b-licences.md`, `docs/roadmap.md`

**Interfaces:** none — documentation of Tasks 1–4.

- [ ] **Step 1: Full suite:** `dotnet tool restore && dotnet build Platform.slnx && dotnet test Platform.slnx`
  (Docker running). Expected: 0 warnings, all tests pass. Note the total test
  count from the output.

- [ ] **Step 2: Update CLAUDE.md.** The load-bearing edits, each grepped for
  every other mention before changing:
  - `Common.Domain … no packages` block: `Common.Application` line gains
    "§6.3's TransactionBehavior over IUnitOfWork and the §7.5 dispatcher port"
    phrasing consistent with what is there.
  - "the §6.2 dispatcher and its two behaviours" → three behaviours.
  - "The pipeline is two behaviours of four: **`IdempotencyBehavior` (§8.5)
    and `TransactionBehavior` (§6.3) do not exist**, and PR-09 is the PR that
    adds the second one." → three of four; only `IdempotencyBehavior` missing.
  - The "Which phase are you in" section: `dotnet test` count `143` → the
    Step 1 total; "**PR-09 is next**" paragraph → PR-09 landed, **PR-10 is
    next** (`feat(catalog): first vertical slice — command, query, cursor
    pagination`, depends 07–09), carrying forward: (a) the identity-map
    double-apply assertion is PR-10's to add beside its first aggregate,
    (b) any domain event PR-10 raises is dropped by
    `NullDomainEventDispatcher` until PR-14, (c) the commit-acknowledgement
    race stays open until PR-14 — keep that paragraph.
  - Catalog.Infrastructure tree annotation gains the null dispatcher.
- [ ] **Step 3: Verify the appendices hold without edits:** appendix-d already
  rows `TransactionBehavior<,>` (§6.3), `IDomainEventDispatcher` (§7.5) and
  `InvariantViolationException` (D.5); no package is new to
  `Directory.Packages.props`, so Appendix B stands. State this in the commit
  body rather than leaving it implied.
- [ ] **Step 4: Run `/validate-blueprint`** and fix anything it raises in the
  same branch.
- [ ] **Step 5: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: PR-09 landed — reconcile the phase note and pipeline state"
```

---

### Task 6: Ship

- [ ] **Step 1:** Commit this plan file (it precedes the code in review order
  but may be committed at write time, before Task 1).
- [ ] **Step 2:** Run `/ship` — it resumes past the existing branch and
  commits, opens the PR titled `feat(common): TransactionBehavior over
  IUnitOfWork` (Appendix C's title, verbatim), then loops Grok and Copilot
  reviews until clean. Findings classed `Needs a decision` stop the loop for
  the user.
