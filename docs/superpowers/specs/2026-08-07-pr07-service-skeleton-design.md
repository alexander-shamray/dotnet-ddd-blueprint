# PR-07 — Service skeleton and architecture test gate

Design for `feat(template): service skeleton and architecture test gate`
(Appendix C, PR-07; depends on PR-02 through PR-06). Written before
implementation and frozen at write time — where this document and the blueprint
disagree, the blueprint wins.

The row commits five things: a compilable empty service across five projects
(§4.1), a Minimal API host, health endpoints, OpenAPI, and the NetArchTest gate
— domain isolation, Application ↛ EF Core, endpoints ↛ Infrastructure,
Application and Domain ↛ MassTransit (§4.2, §9.3).

## 1. The skeleton is Catalog

The phase is named *Service template*, but the template is not an abstract
`Template.*` service — §4.1 draws no such tree, and inventing one would add a
seventh deployable the blueprint never mentions. The template is **Catalog**:
C.1 builds Catalog first, PR-10 (`feat(catalog): first vertical slice`) builds
directly on 07–09 in the same projects this PR creates, and PR-11's scaffold
script "copies and renames the template" — which PR-10's row identifies as the
first containerised service, i.e. Catalog.

The domain question CLAUDE.md records — whether the real solution keeps the
illustrative e-commerce domain — stays open, and this PR does not decide it.
Nothing here contains a domain type: the skeleton is structure, and renaming
five empty projects is the cheapest the rename will ever be. The PR body
raises the question rather than burying it.

## 2. Five projects, and what "empty" means in each

```
src/Services/Catalog/
  Catalog.Domain/            references Common.Domain, nothing else (§4.2)
  Catalog.Application/       references Catalog.Domain, Common.Application
  Catalog.Infrastructure/    references Catalog.Domain, Catalog.Application
  Catalog.Migrator/          console shell; the §7.4 job host arrives with PR-08
  Catalog.Api/               Minimal API host; references Application,
                             Infrastructure, Common.Web
```

**`Catalog.Domain`** holds one type: `public static class AssemblyMarker`. The
architecture tests need an anchor for `typeof(...).Assembly`, and the blueprint's
own idiom is a real type doing double duty — `OrderRepository` is "also the
Infrastructure assembly marker for the §6.2 scan" (Appendix D.5). An empty
assembly has no real type to borrow, so the marker is explicit; PR-10's first
aggregate can replace it as the anchor and delete it.

**`Catalog.Application`** holds `DependencyInjection.cs` with
`AddCatalogApplication()` (§4.1's shape), which is also the assembly's anchor
type. It registers what exists and nothing more:

- `AddPluggableFrom(typeof(DependencyInjection).Assembly)` — each layer scans
  itself (§6.2), and the scan finding nothing is the truthful state.
- `AddDispatcher()` — `Dispatcher` is internal to `Common.Application`, so this
  is the only way to register it.
- `AddSingleton<RequestMetrics>()` — `LoggingBehavior` injects it, and
  `ValidateOnBuild` would otherwise refuse to start the host.
- The two behaviours that exist, in pipeline order (§6.3):
  `LoggingBehavior<,>`, then `ValidationBehavior<,>`. `IdempotencyBehavior`
  (PR-14's dependency chain) and `TransactionBehavior` (PR-09) join this list
  in their own PRs.

**Not registered, deliberately**: `TimeProvider.System` — §4.2's worked example
registers it because handlers take one, and no handler exists. A registration
nothing injects is the unused-project-reference claim in container form; PR-10
adds the line with the first handler that needs it. The same argument defers
`AddValidatorsFromAssemblyContaining` (no validators exist) and every port
registration in the §4.2 sample (no ports exist).

**`Catalog.Infrastructure`** holds `DependencyInjection.cs` with
`AddCatalogInfrastructure()` — and **no `IConfiguration` parameter yet**. §4.2's
`AddOrderingInfrastructure(config)` takes configuration because it reads
connection strings, and this PR has none to read: EF Core, the connection
string and the readiness checks all arrive with PR-08, which adds the parameter
alongside the first line that uses it. An unused parameter is the same untruth
as an unused `using`. The method body is
`AddPluggableFrom(typeof(DependencyInjection).Assembly)` — the §6.2 rule that
both registration methods scan their own assembly, wired from the start.

**`Catalog.Migrator`** is a console shell: it prints that no migrations exist
yet and exits 0 — truthfully, since applying zero migrations succeeds. §7.4's
`Database.Migrate()` job host is PR-08's deliverable, in the same PR as the
`DbContext` it migrates. The project exists now because the row says five
projects and §4.1 draws five; a service without its migrator's project is a
shape §4.1 does not permit.

**`Catalog.Api`** is the Minimal API host. `Program.cs` is the composition
root and follows §4.2's shape minus everything whose PR has not arrived
(authentication is PR-16, endpoints are PR-10):

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Host.UseDefaultServiceProvider(o =>
{
    o.ValidateOnBuild = true;
    o.ValidateScopes = true;
});

builder.AddCommonWebDefaults();                 // §13.2
builder.Services.AddCatalogApplication();       // §6.2
builder.Services.AddCatalogInfrastructure();    // §4.2
builder.Services.AddOpenApi();                  // Appendix C, PR-07

WebApplication app = builder.Build();

app.UseExceptionHandler();        // §10.5
app.UseCorrelationId();           // §10.4

app.MapCommonHealthEndpoints();   // §13.5
app.MapOpenApi();

app.Run();

public partial class Program;     // §12.4 — WebApplicationFactory<Program>
```

`/health/ready` reports healthy with an empty readiness set, and §13.5 says
that is the rule working as designed for a host with no connection string —
Catalog acquires its SQL readiness check with its SQL connection in PR-08.

## 3. OpenAPI — the framework generator, unconditionally mapped

The row's "OpenAPI" is its only mention in the blueprint; the mechanism is this
PR's to choose. `Microsoft.AspNetCore.OpenApi` (10.0.0, MIT) is the framework's
own document generator: `AddOpenApi()` / `MapOpenApi()`, no third-party
dependency, no UI. Swashbuckle left the .NET templates at .NET 9 and would be a
new dependency arguing against the platform's direction; a UI (Scalar,
Swagger UI) is a dev-tooling decision nobody has made, and this PR does not
smuggle one in.

The document is mapped unconditionally rather than gated to Development. The
services sit behind the gateway (§10.1), the document describes an API surface
the client is entitled to know, and an environment-gated endpoint makes the
smoke test hostage to `WebApplicationFactory`'s environment default. PR-16's
authentication work is the natural point to revisit exposure.

One new pin means one new register row in the same change: Appendix B gains
`Microsoft.AspNetCore.OpenApi` (MIT), or the licence gate fails the build
before anything compiles.

## 4. The gate — four rules, three homes

Each rule lives in the test project §12.1 gives its layer, so every §4.1 test
home for Catalog exists from this PR with real content:

| Test project | Rule | Mechanism |
|---|---|---|
| `Catalog.Domain.Tests` | Domain has no infrastructure dependencies | §4.2's reflection test verbatim: `GetReferencedAssemblies()` against the forbidden prefixes (EF Core, MassTransit, StackExchange.Redis, Microsoft.AspNetCore) |
| `Catalog.Application.Tests` | Application ↛ EF Core | NetArchTest `HaveDependencyOn("Microsoft.EntityFrameworkCore")` |
| `Catalog.Application.Tests` | Application and Domain ↛ MassTransit | §4.2's two-assembly loop verbatim (§9.3's rule) |
| `Catalog.Api.Tests` | Endpoints ↛ Infrastructure | §4.2's namespace rule — vacuously green until PR-10 adds the first endpoint, which is the point: "an architecture rule introduced before the violations is a constraint" |

`Catalog.Application.Tests` also asserts the registration surface — that
`AddCatalogApplication` registers `IDispatcher` and exactly the two behaviours
in pipeline order — because registration order is pipeline order (§6.3) and
the ordering is otherwise invisible until PR-09 makes it load-bearing.

`Catalog.Api.Tests` carries the host smoke through
`WebApplicationFactory<Program>` (Mvc.Testing, already pinned): the host builds
under `ValidateOnBuild`, `/health/live` and `/health/ready` return 200, and
`/openapi/v1.json` serves a document. No containers — the pyramid's `*.Api.Tests`
row gains them when there is infrastructure to contain (PR-08).

`Catalog.TestSupport` is **not** created: its contents (§12.4's
`ServiceFixture`, test auth, builders) all need containers or auth, and a
project invented before its PR is the mistake CLAUDE.md names.

## 5. Documentation reconciled in the same PR

| File | Change |
|---|---|
| `Directory.Packages.props` | `Microsoft.AspNetCore.OpenApi` 10.0.0 pin, Runtime group, with a comment naming the PR-07 deliverable |
| `appendix-b-licences.md` | The matching register row |
| `Platform.slnx` | `/src/Services/Catalog/` folder with the five projects; the three test projects under `/tests/` |
| `CLAUDE.md` | Phase section: PR-08 next; present tree gains `src/Services/Catalog` and the three test projects; test count updated from 122 |

`Appendix D` is untouched: D.5's rule inventories names *the blueprint's
samples reference*, and no sample names a Catalog skeleton type —
`AddCatalogApplication` is the §4.1 pattern instantiated, not a new vocabulary.
`docs/roadmap.md` is untouched: landing a PR changes no estimate. §4.1, §4.2,
§12.1 and Appendix C already describe what this PR builds and need no
amendment.

## 6. Deliberately not in this PR

- **EF Core, `DbContext`, connection strings, the real migrator, readiness
  checks, Testcontainers** — PR-08.
- **`TransactionBehavior`** — PR-09.
- **Endpoints, the first aggregate, Dockerfile and Compose block** — PR-10;
  the `deploy/compose` tree is not touched.
- **The scaffold script** — PR-11.
- **Authentication and the OpenAPI-exposure revisit** — PR-16.
- **`Common.Infrastructure`, `Common.Contracts`, `Catalog.TestSupport`** —
  their own PRs.

## 7. Done means

- `dotnet build Platform.slnx` and `dotnet test Platform.slnx` green —
  fourteen projects, all previous tests plus the new gate and smoke suites.
- The licence gate passes with the new pin registered.
- Each architecture test observed failing for the right reason before the
  design settles it (a deliberately added forbidden reference), per §12.2's
  red-first discipline.
- `/validate-blueprint` finds no code ↔ blueprint drift.
