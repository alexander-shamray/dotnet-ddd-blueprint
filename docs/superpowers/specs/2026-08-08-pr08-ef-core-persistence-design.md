# PR-08 — EF Core, repositories, IUnitOfWork, migrator host

Design for `feat(template): EF Core, repositories, IUnitOfWork, migrator host`
(Appendix C, PR-08; depends on PR-07 and PR-06). Written before implementation
and frozen at write time — where this document and the blueprint disagree, the
blueprint wins.

The row commits five things: a `DbContext` sealed in Infrastructure, the
`IUnitOfWork` port, the `*.Migrator` project, dual connection strings (§7.1),
and a Testcontainers smoke test. CLAUDE.md's phase note adds readiness checks,
which §13.5 ties to the connection string this PR introduces.

## 1. The word "repositories" in the title, and what it can mean here

The title says repositories and this PR ships none. That is not an omission —
it is what §5.6 requires. A repository is defined **per aggregate root**, in
Domain, with methods named for the loads that aggregate actually needs
(`GetAsync`, `GetByPaymentReferenceAsync`, `Add`); there is no `Update`, no
`GetAll`, no `IQueryable`, and §5.7 names "repository per entity" as an
anti-pattern in its own right. Catalog has no aggregate until PR-10, whose row
delivers "one aggregate, one command, one cursor-paginated query".

So the only repository PR-08 could ship is a generic one — `IRepository<T, TId>`
over the `DbSet` — and that is precisely the shape §5.6 spends a paragraph
rejecting. Shipping it to satisfy a word in a PR title would put the
anti-pattern in the template every later service copies (PR-11), which is the
most expensive place in the repository to put one.

What PR-08 ships instead is everything a repository needs to exist: the
`DbContext` it loads through, the `IUnitOfWork` that tracks its changes, and the
schema it writes into. `IOrderRepository`'s Catalog counterpart lands in PR-10
beside the aggregate that gives it methods. This is stated in the PR body rather
than left for a reviewer to notice.

## 2. `IUnitOfWork` — the whole port, in `Common.Application`

§6.3 states the interface across two code blocks, the second opening
`// ... as above`. The port is one type with five members and PR-08 ships all
five:

```csharp
namespace Common.Application;

public interface IUnitOfWork
{
    bool HasActiveTransaction { get; }
    int ModifiedAggregateCount { get; }

    Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken ct);
    Task<int> SaveChangesAsync(CancellationToken ct);
    Task ExecuteRawAsync(string sql, object parameters, CancellationToken ct);
}
```

Splitting it — three members now, two with PR-09 — would be the more literal
reading of Appendix C, and it is the wrong one. `EfUnitOfWork` implements the
interface, not a subset of it, so a member added later is a second edit to the
same two files for no gain; and `ModifiedAggregateCount` is the member PR-09's
`TransactionBehavior` asserts principle 3 through, so deferring it means PR-09
lands the port and the behaviour together with nothing between them to review.

**No signature here names a domain type**, so `Common.Application` still does
not reference `Common.Domain`. CLAUDE.md records that edge as PR-09's, and this
PR does not draw it early. `ExecuteRawAsync` takes `string` and `object` for the
same reason — Dapper's `CommandDefinition` is an Infrastructure type and §4.2
forbids it on this side of the boundary.

## 3. `CatalogDbContext` — sealed, in Infrastructure, default schema `catalog`

```csharp
namespace Catalog.Infrastructure.Persistence;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options)
    : DbContext(options)
```

`public`, not `internal`, and this is the one place the boundary is loosened on
purpose: `dotnet ef` constructs the context by name, the migrator host resolves
it, and the Testcontainers fixture migrates through it. §6.3's rule is that
`DbContext` **never leaves Infrastructure** — which is a rule about references,
enforced by the architecture gates, not about the access modifier. `Catalog.Api`
may name it and does not; `Catalog.Application` cannot, because the gate forbids
the EF Core dependency it would need.

`EfUnitOfWork` stays `internal sealed` — nothing outside the assembly resolves
it by type, only through the port.

Two overrides, both from §7.2:

- `OnModelCreating` — `HasDefaultSchema("catalog")`, then
  `ApplyConfigurationsFromAssembly(typeof(CatalogDbContext).Assembly)`. The scan
  finds nothing today; it is the line PR-10's `IEntityTypeConfiguration<Product>`
  is found by, and adding it now means PR-10 adds a configuration rather than a
  configuration and the code that discovers it.
- `ConfigureConventions` — the three global conventions §7.2 prints verbatim:
  `decimal` precision (19, 4), `string` max length 400, `DateTimeOffset` as
  `datetimeoffset(7)`.

The conventions apply to a model with no properties, which is the point of
landing them here: §7.2's argument is that defaulting `string` to a bounded
length turns "someone forgot" into a visible override, and a convention added
*after* the first entity is a convention that silently changes an existing
column.

## 4. The initial migration, and what an empty model can honestly create

`dotnet ef migrations add InitialCreate` against a model with no entity types
produces an empty `Up`. The migration is still worth having — it is what creates
the database and `__EFMigrationsHistory`, and it is the row the migrator's exit
code is about — but an empty one asserts nothing.

So the `Up` carries one hand-written line, `migrationBuilder.EnsureSchema
("catalog")`, and §7.4 is the section that permits it: "hand-written DDL rides
along, in the same transaction, applied by the same job, versioned by the same
migration history". The schema is the one piece of Catalog's shape that exists
before its first table, and creating it here means PR-10's first `CREATE TABLE`
lands in a schema that is already there rather than being ordered against it.

`Down` drops the schema. It is not reachable in any deployment — §7.4 rolls
forward — but a `Down` that does not undo its `Up` is a lie in a file whose
whole job is to be the record of a change.

**The tool is pinned, because it was already wrong once.** This machine had
`dotnet-ef` 8.0.11 against an EF Core 10 pin, which fails with a message about
the wrong thing. `.config/dotnet-tools.json` pins the tool to the EF Core
version in `Directory.Packages.props`, so `dotnet tool restore` is the whole
setup. The manifest is not a package pin and does not enter the licence gate —
the gate reads `Directory.Packages.props`, and a register row with no matching
pin fails its staleness check.

## 5. `Catalog.Migrator` — the §7.4 job host

The shell prints a line and exits 0. It becomes a host that does one thing:

```csharp
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<CatalogDbContext>(o =>
    o.UseSqlServer(
        builder.Configuration.GetConnectionString("CatalogMigrator"),   // §7.1
        sql => sql.EnableRetryOnFailure()));

using IHost host = builder.Build();
// ... resolve, MigrateAsync, log, return 0 or 1
```

A host rather than a bare `new DbContextOptionsBuilder<>()`, for three reasons
that are all about the job being run by a Kubernetes `Job` (§7.4) rather than by
a developer: configuration binds `ConnectionStrings__CatalogMigrator` from the
environment the way every other host in this repo does; the logging provider
puts EF's migration output in the pod log, which is the only place a failed
pre-upgrade hook is diagnosed from; and `MigrateAsync` inherits the retry
strategy configured above, so a database still accepting connections slowly does
not fail the deploy.

**It reads `CatalogMigrator`, never `Catalog`.** §7.1's split is two principals
with different rights, and the migrator's secret is the one that carries DDL.
Reading the runtime key here would make the two connection strings a naming
convention rather than a boundary. The reverse holds in
`AddCatalogInfrastructure`, which reads `Catalog` and never the migrator key.

The exit code is the contract: `Database.MigrateAsync()` and nothing else, 0 on
success, 1 with the exception logged on failure. `backoffLimit: 2` in §7.4's
manifest is what turns a 1 into a retry, so swallowing the exception would make
the job succeed against an unmigrated database.

**§4.2's dependency table has no row for `*.Migrator`**, and this PR is where
that gap becomes load-bearing: the migrator must reference Infrastructure to
reach the `DbContext`. The table gains the row in this PR — `*.Migrator` may
reference Infrastructure, must never reference another service's projects — and
the `Ordering.Migrator` line in §4.1's tree already calls it a "Migration job
host (§7.4)", so the table was the only place silent about it.

## 6. The parameter arrives with its first reader

PR-07 deliberately left the parameter off, arguing that an unused parameter is
the same untruth as an unused `using`. Three lines now read it:

```csharp
services.AddDbContext<CatalogDbContext>(o =>
    o.UseSqlServer(
        configuration.GetConnectionString("Catalog"),        // runtime identity, §7.1
        sql => sql.EnableRetryOnFailure()));

services.AddScoped<IUnitOfWork, EfUnitOfWork>();             // §6.3

services
    .AddHealthChecks()
    .AddSqlServer(configuration.GetConnectionString("Catalog")!, name: "sql", tags: ["ready"]);
```

`EnableRetryOnFailure` is not decoration: §6.3 requires `CreateExecutionStrategy`
in `EfUnitOfWork.ExecuteAsync`, and with no retry configured the strategy is a
no-op that quietly stops proving anything.

**`IDbConnectionFactory` and `SqlConnectionFactory` are not registered.** §4.2's
sample has them and §6.5 explains what they are for — Dapper reads against read
models — and Catalog has no query until PR-10. This is PR-07's argument applied
again: a registration nothing injects is an unused project reference in
container form.

**The readiness check is the deliverable §13.5 cares about most.** Its own
paragraph says "reports ready immediately" is indistinguishable from "readiness
was never wired up", and Catalog has been in the first state since PR-07 for the
honest reason that it had no connection string. It now has one, so it gets the
check, and the test below is written so that removing the check fails a test
rather than making one vacuously greener.

## 7. Tests

### `Catalog.Api.Tests` — the host smoke, amended

`Ready_probe_returns_200_when_no_readiness_checks_are_registered` is deleted.
Its premise — a host with no connection string — stopped being true in this PR,
and a test whose comment cites §13.5's empty-set rule cannot be repointed at a
host that now has a check. Two replace it:

| Test | Asserts |
|---|---|
| `Ready_probe_reports_the_sql_check` | The registered health-check set contains one named `sql` tagged `ready`. Read from `IOptions<HealthCheckServiceOptions>` — no network, no container |
| `Ready_probe_returns_503_when_sql_is_unreachable` | `/health/ready` is 503 against an unresolvable host. `Connect Timeout=1` and a `.invalid` name, so it fails on NXDOMAIN in well under a second |

The pair is deliberate. The first fails if the registration is deleted; the
second fails if the check is registered but untagged, or if the predicate stops
selecting it — which is the failure §13.5 describes and neither test alone
catches. `/health/live` staying 200 in the same class is the third assertion,
and it is the one that would catch a readiness check leaking into liveness.

The factory therefore has to supply a connection string, because
`AddSqlServer(null!)` throws at registration. That is the correct behaviour and
worth stating: a service host without a connection string does not start.

### `Catalog.Api.Tests` — the Testcontainers smoke

One fixture, one SQL Server container, `mcr.microsoft.com/mssql/server:
2022-latest` — the image §12.4 names and the one Compose runs, so the test and
the developer environment cannot disagree about the engine. `IAsyncLifetime`
returning `ValueTask`, per xUnit v3 (§12.4).

| Test | Asserts |
|---|---|
| `Migrator_creates_the_schema_and_records_the_migration` | After `MigrateAsync`, `__EFMigrationsHistory` holds `InitialCreate` and `catalog` is in `sys.schemas` |
| `Migrating_twice_is_a_no_op` | The second run applies nothing and throws nothing — the pre-upgrade hook reruns on every deploy (§7.4) |
| `Ready_probe_returns_200_against_a_real_database` | The §13.5 check, end to end, against an engine that is actually up |
| `ExecuteAsync_commits_when_the_operation_succeeds` | A row written through `ExecuteRawAsync` survives the unit |
| `ExecuteAsync_rolls_back_when_the_operation_returns_a_failed_Result` | The same write leaves no row |
| `HasActiveTransaction_is_false_outside_and_true_inside` | The guard `TransactionBehavior` checks in PR-09 reports what it claims |

The last three are `EfUnitOfWork`'s own tests, not PR-09's. PR-09's row covers
`TransactionBehavior` — `SaveChanges` once on success and never on failure — and
that behaviour leans on a guard that lives in `EfUnitOfWork.ExecuteAsync` and
commits nothing on a failed `Result`. §6.3 says so explicitly: "the two together
mean a failed command commits nothing by either route". PR-08 owns one of the
two routes, so PR-08 tests it.

They need a table and Catalog has none. The fixture creates
`catalog.TransactionProbe` itself, in the test project, and that is the honest
place for it: it is a fixture of the test, not a table of the service, and
putting it in a migration to make a test easier would ship it to production.

### Docker is now a prerequisite for `dotnet test`

The container test runs unconditionally. It is not gated behind a category and
not skipped when Docker is absent, and both were considered:

- **A skip on missing Docker fails open.** CI would go green on a runner whose
  Docker broke, which is the same failure mode `Common.Web.Tests`' assembly-wide
  parallelisation attribute was chosen to avoid — a mechanism that silently
  stops enforcing is worse than one that is loud.
- **A category is PR-22's deliverable**, which the row names ("Testcontainers
  categories"), with PR-25 running them as a separate CI stage. Inventing the
  taxonomy two PRs early is inventing a project early by another route.

ADR-010 already made real infrastructure non-optional, and `ubuntu-latest`
carries a working Docker daemon, so CI needs no change. What changes is the
local contract, and CLAUDE.md's command block says so in this PR.

## 8. New pins, and the three files each one touches

| Package | Licence | Why |
|---|---|---|
| `Microsoft.EntityFrameworkCore.Design` | MIT | `dotnet ef` requires it in the startup project. `PrivateAssets="all"` — it is design-time and must not flow to anything referencing the migrator |
| `Microsoft.Extensions.Hosting` | MIT | The migrator's job host. ASP.NET Core's shared framework carries it and the migrator is not a web host, so it takes the package |
| `Microsoft.Extensions.Configuration.Abstractions` | MIT | `AddCatalogInfrastructure` names `IConfiguration` in its signature. It arrives transitively, and the register's rule is that referencing what is actually used keeps it honest |

Each needs `Directory.Packages.props`, the matching row in
`appendix-b-licences.md`, **and** §4.4's fenced transcription of the props file
— the licence gate compares all three and reports the chapter, so a pin added to
two of them fails the build before anything compiles.

`Testcontainers.MsSql`, `AspNetCore.HealthChecks.SqlServer`,
`Microsoft.EntityFrameworkCore.SqlServer` and `Dapper` are already pinned and
registered. This PR is the first consumer of all four.

## 9. Documentation reconciled in the same PR

| File | Change |
|---|---|
| `04-solution-structure.md` | §4.2's dependency table gains the `*.Migrator` row; §4.4's props sample gains the three pins |
| `appendix-b-licences.md` | Three register rows |
| `CLAUDE.md` | Phase section: PR-09 next; the Catalog tree annotations; test count; Docker as a `dotnet test` prerequisite; the `Catalog.TestSupport` note, which said TestSupport waits for containers and now waits for a second consumer |

`Appendix D` is untouched, for PR-07's reason: D.1 already carries `IUnitOfWork`
and D.5 already carries `EfUnitOfWork`, and `CatalogDbContext` is §4.1's pattern
instantiated rather than a name any sample references. `docs/roadmap.md` is
untouched — landing a PR changes no estimate. §7.1, §7.2, §7.4, §6.3 and §13.5
describe what this PR builds and need no amendment.

## 10. Deliberately not in this PR

- **`TransactionBehavior` and its registration** — PR-09. The port lands here,
  the behaviour there; `AddCatalogApplication`'s pipeline stays at two.
- **`IDomainEventCollector`, `IDomainEventDispatcher`, `IProjectionRegistry`,
  the outbox** — §7.5's mechanism, delivered by PR-14. `EfUnitOfWork` needs none
  of them.
- **A repository, an aggregate, an entity configuration, endpoints, the
  Dockerfile and the Compose block** — PR-10.
- **`IDbConnectionFactory` / `SqlConnectionFactory`** — PR-10, with the first
  query that reads through them.
- **The §7.1 two-login seed script for Compose.** §7.1 commits to Compose
  seeding both principals from the same script the cloud path uses, and that
  commitment is unmet today. It stays unmet here on purpose: the script needs a
  database to grant against and a container to be exercised by, and both arrive
  with PR-10's Compose block. Landing it now would ship an unexercised script,
  which is the shape of thing that is wrong by the time anyone runs it. **This is
  flagged in the PR body as a debt with a named home**, not left silent.
- **`Catalog.TestSupport`** — §4.1 describes it as referenced by two test
  projects that "cannot reference each other", and there is one consumer. It
  lands when the second one exists, or with PR-16's test auth, whichever is
  first.
- **Respawn** — pinned since PR-01 and still unused. Resetting between tests
  needs data to reset, which arrives with PR-10.

## 11. Done means

- `dotnet build Platform.slnx` and `dotnet test Platform.slnx` green, with
  Docker running.
- The licence gate passes with three new pins registered and printed in §4.4.
- The migration observed applying to a real SQL Server, and observed applying a
  second time without error.
- `EfUnitOfWork`'s rollback path observed red before it is green — the commit
  test passing against an implementation that never commits would prove nothing.
- `/validate-blueprint` finds no code ↔ blueprint drift.
