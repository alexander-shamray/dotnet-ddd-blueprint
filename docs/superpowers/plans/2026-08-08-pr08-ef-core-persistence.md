# PR-08 implementation plan — EF Core, repositories, IUnitOfWork, migrator host

Derived from `2026-08-08-pr08-ef-core-persistence-design.md` and frozen at write
time. Where this plan and the blueprint disagree, the blueprint wins; where it
and the shipped code disagree, the disagreement is the record of where the
design moved, and is left standing.

Branch: `feat/template-ef-core-persistence`. Commit title, verbatim from
Appendix C: `feat(template): EF Core, repositories, IUnitOfWork, migrator host`.

## Step 0 — prerequisites

1. Start the Docker engine. `docker info` must answer before any container test
   is trusted; a red run against a dead daemon proves nothing about the code.
2. `dotnet new tool-manifest`, then pin `dotnet-ef` to the EF Core version in
   `Directory.Packages.props`. The machine carried 8.0.11 against a 10.0.0 pin,
   which is the failure this step exists to remove.

## Step 1 — pins and the three files each touches

Add to `Directory.Packages.props`, each with a comment naming its consumer:

- `Microsoft.EntityFrameworkCore.Design` — Runtime group, design-time
- `Microsoft.Extensions.Hosting` — Runtime group, the migrator host
- `Microsoft.Extensions.Configuration.Abstractions` — Runtime group, beside the
  two abstraction pins already there for the same reason

Then, in the same step because the gate fails otherwise:

- `appendix-b-licences.md` — three rows in the **Chosen** table, MIT, each with
  its role
- `04-solution-structure.md` §4.4 — the same three lines in the fenced props
  transcription, in the same order as the file

Verify: `python .github/licence-gate/licence_gate.py` reports every pin
registered and printed. Run it before anything compiles, which is the whole
point of where §15.1 puts it.

## Step 2 — `IUnitOfWork` in `Common.Application`

One new file, `IUnitOfWork.cs`, carrying all five members of §6.3 with the XML
docs the chapter prints. No project reference changes: no member names a domain
type, so the `Common.Application → Common.Domain` edge stays PR-09's.

Verify: `dotnet build` green; `Common.Application.csproj` unchanged.

## Step 3 — `Catalog.Infrastructure` gains EF Core

Packages: `Microsoft.EntityFrameworkCore.SqlServer`, `Dapper`,
`AspNetCore.HealthChecks.SqlServer`,
`Microsoft.Extensions.Configuration.Abstractions`.

Files, in `Persistence/`:

1. `CatalogDbContext.cs` — `public sealed`, primary constructor taking
   `DbContextOptions<CatalogDbContext>`. `OnModelCreating`:
   `HasDefaultSchema("catalog")` then `ApplyConfigurationsFromAssembly`.
   `ConfigureConventions`: §7.2's three, verbatim.
2. `EfUnitOfWork.cs` — `internal sealed`, §6.3's implementation verbatim,
   including `CreateExecutionStrategy` and the failed-`Result` guard before
   `CommitAsync`.

`DependencyInjection.cs` gains the `IConfiguration configuration` parameter and
the three blocks from §4.2 and §13.5: `AddDbContext` with `EnableRetryOnFailure`
reading `ConnectionStrings:Catalog`, `AddScoped<IUnitOfWork, EfUnitOfWork>`, and
`AddHealthChecks().AddSqlServer(..., name: "sql", tags: ["ready"])`.

`Catalog.Api/Program.cs` passes `builder.Configuration`.

Verify: `dotnet build Platform.slnx` green. The Application ↛ EF Core gate in
`Catalog.Application.Tests` still passes — EF Core entered Infrastructure only,
and a gate that goes red here means the reference landed in the wrong project.

## Step 4 — the initial migration

```bash
dotnet ef migrations add InitialCreate \
    --project src/Services/Catalog/Catalog.Infrastructure \
    --startup-project src/Services/Catalog/Catalog.Migrator \
    --output-dir Persistence/Migrations
```

The generated `Up` is empty. Hand-add `migrationBuilder.EnsureSchema("catalog")`
and a `Down` that drops it, per §7.4's rule that hand-written DDL rides along in
the same migration. Leave the model snapshot exactly as generated — it is the
input to PR-10's `migrations add`, and an edited snapshot produces a wrong
migration two PRs later.

This step is ordered after step 5's csproj work in practice, because `dotnet ef`
needs the startup project to build.

## Step 5 — `Catalog.Migrator` becomes the §7.4 job host

`Catalog.Migrator.csproj`: `ProjectReference` to `Catalog.Infrastructure`;
`PackageReference` to `Microsoft.Extensions.Hosting` and to
`Microsoft.EntityFrameworkCore.Design` with `PrivateAssets="all"`.

`Program.cs`: `Host.CreateApplicationBuilder(args)`, `AddDbContext` reading
`ConnectionStrings:CatalogMigrator` with `EnableRetryOnFailure`, resolve the
context in a scope, `await db.Database.MigrateAsync()`, log the outcome, return
0. On exception, log and return 1 — §7.4's `backoffLimit: 2` is what turns the
1 into a retry, so the exception must not be swallowed.

Verify: `dotnet run --project src/Services/Catalog/Catalog.Migrator` against the
Testcontainers connection string in step 7 — but the exit-code contract is
proven by the container test, not by hand.

## Step 6 — amend the host smoke

`tests/Catalog.Api.Tests/HostSmokeTests.cs`:

- The factory gets a `WithWebHostBuilder` supplying
  `ConnectionStrings:Catalog` — unresolvable `.invalid` host,
  `Connect Timeout=1`. `AddSqlServer(null!)` throws at registration, so a host
  with no connection string cannot start, which is correct and is why the test
  supplies one rather than the registration tolerating none.
- Delete `Ready_probe_returns_200_when_no_readiness_checks_are_registered` — its
  premise died in step 3.
- Add `Ready_probe_reports_the_sql_check`, reading
  `IOptions<HealthCheckServiceOptions>` and asserting one registration named
  `sql` carrying the `ready` tag.
- Add `Ready_probe_returns_503_when_sql_is_unreachable`.
- `Live_probe_returns_200` and the OpenAPI test are unchanged, and the first is
  now also the assertion that readiness has not leaked into liveness.

Verify: `dotnet test` on this project alone, no Docker needed. Watch the
503 test's wall time — if the unresolvable host is not fast, shorten the
timeout rather than accepting a slow unit test.

## Step 7 — the Testcontainers smoke

`tests/Catalog.Api.Tests/`: add `Testcontainers.MsSql` to the csproj.

`SqlServerFixture.cs` — `IAsyncLifetime` with `ValueTask` (xUnit v3, §12.4),
one `MsSqlBuilder` on `mcr.microsoft.com/mssql/server:2022-latest`. Exposes the
connection string and a `WebApplicationFactory<Program>` pointed at it.

`DatabaseSmokeTests.cs` — the six assertions in the design's §7 table. The
transaction tests create `catalog.TransactionProbe` in the fixture, after
migrating; it is a fixture of the test and deliberately not a migration.

**Red first, per §12.2 and the design's "done means".** Before the suite is
trusted, break `EfUnitOfWork.ExecuteAsync`'s failed-`Result` guard — return
before the check so it always commits — and watch
`ExecuteAsync_rolls_back_when_the_operation_returns_a_failed_Result` fail. Then
restore it. A commit test that passes against an implementation that never
rolls back is a test of nothing.

Verify: `dotnet test Platform.slnx` green with Docker running.

## Step 8 — reconcile the documentation

| File | Change |
|---|---|
| `04-solution-structure.md` | §4.2's dependency table gains the `*.Migrator` row (may reference Domain, Application, Infrastructure; must never reference another service's projects) |
| `CLAUDE.md` | Phase section: PR-09 next and what it depends on; the `src/Services/Catalog` and `tests/` annotations; the test count; Docker as a `dotnet test` prerequisite; the `Common.Application` note, which says the `Common.Domain` edge is still undrawn and now says `IUnitOfWork` is why it still can be; the `Catalog.TestSupport` line |

Then `/validate-blueprint`, and fix what it finds in this PR rather than noting
it.

## Step 9 — ship

`/ship`: branch is already correct, so commit, push, PR, then the Grok and
Copilot review loops. The PR body raises three things rather than burying them:

1. **The title says repositories and none ship** — §5.6's reason, design §1.
2. **The §7.1 Compose seed script is still unwritten**, and PR-10 is its home.
3. **The domain question is still open** — PR-10 is the first PR that cannot
   avoid deciding it, since it names an aggregate.

## Commit split

One commit per coherent claim, each with a body that argues it:

1. `chore: pin the EF tooling and the three packages PR-08 consumes` — the
   manifest, the props file, Appendix B, §4.4.
2. `feat(common): IUnitOfWork, the transaction boundary as a port` — step 2.
3. `feat(template): CatalogDbContext, EfUnitOfWork and the SQL readiness check`
   — steps 3 and 6, because the readiness test is the assertion that step 3's
   registration is real.
4. `feat(template): the initial migration and the §7.4 migrator host` — steps 4
   and 5.
5. `test(template): the Testcontainers smoke over the migrator and the unit of
   work` — step 7.
6. `docs: the migrator's row in §4.2, and PR-08 in the phase notes` — step 8.
