# PR-07 Service Skeleton Implementation Plan

Derived from
[the design spec](../specs/2026-08-07-pr07-service-skeleton-design.md) and
frozen with it. The blueprint wins over both.

## Global constraints

- Branch: `feat/catalog-service-skeleton`; every commit leaves the build green.
- House C# dialect throughout (CLAUDE.md): file-scoped namespaces, blank line
  after the namespace, explicit locals outside the four `var` cases, no
  `Version=` on any `PackageReference`.
- Test names are sentences with underscores; CA1707 is already off for
  `*Tests` projects.
- Red-first for the gate: each architecture rule is observed failing via a
  deliberately added forbidden reference before that reference is removed
  again. The removal is the green step; the rule then stands as a constraint.

### Task 1: Source projects and the solution

`src/Services/Catalog/` — five projects, each csproj versionless and
comment-bearing in the established style:

- `Catalog.Domain/Catalog.Domain.csproj` — `Microsoft.NET.Sdk`;
  ProjectReference `Common.Domain` only. `AssemblyMarker.cs`: public static
  class, documented as the `typeof` anchor the architecture tests need until
  PR-10's first aggregate replaces it.
- `Catalog.Application/Catalog.Application.csproj` — ProjectReferences
  `Catalog.Domain`, `Common.Application`. `DependencyInjection.cs`:

  ```csharp
  namespace Catalog.Application;

  public static class DependencyInjection
  {
      public static IServiceCollection AddCatalogApplication(this IServiceCollection services)
      {
          services.AddPluggableFrom(typeof(DependencyInjection).Assembly);  // §6.2
          services.AddDispatcher();
          services.AddSingleton<RequestMetrics>();                          // §13.3

          // Ordered, explicit, not scanned — registration order is pipeline
          // order (§6.3). Two of four: IdempotencyBehavior and
          // TransactionBehavior join with their PRs.
          services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
          services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
          return services;
      }
  }
  ```

- `Catalog.Infrastructure/Catalog.Infrastructure.csproj` — ProjectReferences
  `Catalog.Domain`, `Catalog.Application`. `DependencyInjection.cs`:
  `AddCatalogInfrastructure()` calling
  `AddPluggableFrom(typeof(DependencyInjection).Assembly)`; no
  `IConfiguration` parameter until PR-08 has a line that reads it.
- `Catalog.Migrator/Catalog.Migrator.csproj` — `Microsoft.NET.Sdk`,
  `OutputType Exe`, no references. `Program.cs`: one `Console.WriteLine`
  stating no migrations exist yet and that PR-08 brings the §7.4 job host;
  exit 0.
- `Catalog.Api/Catalog.Api.csproj` — `Microsoft.NET.Sdk.Web`;
  ProjectReferences `Catalog.Application`, `Catalog.Infrastructure`,
  `Common.Web`; PackageReference `Microsoft.AspNetCore.OpenApi`.
  `Program.cs` exactly as the spec §2 prints it.

`Platform.slnx` gains `/src/Services/Catalog/` with the five projects and the
three test projects under `/tests/`, alphabetical within each folder.

### Task 2: Pin and register the one new package

- `Directory.Packages.props`, Runtime group:
  `Microsoft.AspNetCore.OpenApi` 10.0.0, commented as PR-07's OpenAPI
  deliverable — the framework generator, document only, no UI.
- `appendix-b-licences.md`: matching row, MIT, role naming the document
  endpoint the service hosts serve.

### Task 3: Test projects — the gate and the smoke

- `tests/Catalog.Domain.Tests/` — xunit trio + Shouldly; ProjectReference
  `Catalog.Domain`. `ArchitectureTests.cs`:
  `Domain_has_no_infrastructure_dependencies` — §4.2's reflection test
  anchored on `AssemblyMarker`.
- `tests/Catalog.Application.Tests/` — xunit trio, Shouldly,
  `NetArchTest.Rules`, `Microsoft.Extensions.DependencyInjection`;
  ProjectReference `Catalog.Application`. `ArchitectureTests.cs`:
  `Application_does_not_depend_on_ef_core`,
  `Application_and_domain_do_not_reference_masstransit` (§4.2's two-assembly
  loop). `DependencyInjectionTests.cs`:
  `AddCatalogApplication_registers_the_dispatcher_scoped`,
  `AddCatalogApplication_registers_the_two_behaviours_in_pipeline_order`.
- `tests/Catalog.Api.Tests/` — xunit trio, Shouldly, `NetArchTest.Rules`,
  `Microsoft.AspNetCore.Mvc.Testing`, FrameworkReference; ProjectReference
  `Catalog.Api`. `ArchitectureTests.cs`:
  `Endpoints_do_not_depend_on_infrastructure`. `HostSmokeTests.cs` over one
  class-shared `WebApplicationFactory<Program>`:
  `Live_probe_returns_200`,
  `Ready_probe_returns_200_when_no_readiness_checks_are_registered`,
  `OpenApi_document_is_served`.

Red verification: add `MassTransit.RabbitMQ` to `Catalog.Domain` briefly and
watch both the Domain and the MassTransit gates fail; add an EF Core reference
to `Catalog.Application` and watch its gate fail; revert both. The smoke tests
are red-first naturally — they are written against a `Program.cs` that exists
by then, so red is proven by asserting against a wrong path first or simply by
the §12.2 compile-then-fail rule where applicable.

### Task 4: CLAUDE.md

- Phase section: PR-06 → PR-07 landed; **PR-08 is next**
  (`feat(template): EF Core, repositories, IUnitOfWork, migrator host`).
- Present tree: `src/Services/Catalog/` five-project block and the three test
  projects; the planned tree's annotations move Catalog from ahead to landed.
- Test count updated from 122 to the number `dotnet test` prints.

### Task 5: Verification and the PR

- `dotnet restore && dotnet build && dotnet test` on `Platform.slnx` — all
  green, zero warnings.
- `python .github/licence-gate/` run (however ci.yml invokes it) — green with
  the new pin.
- `/validate-blueprint` — no drift introduced.
- Commits split semantically: spec+plan (`docs:`), skeleton+pin+register
  (`feat(template):`), gate+smoke tests (`test:` … or folded per /commit's
  judgement), CLAUDE.md (`chore:`). PR titled with Appendix C's row verbatim:
  `feat(template): service skeleton and architecture test gate`; body raises
  the open domain question.
