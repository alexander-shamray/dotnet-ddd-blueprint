# PR-13 MassTransit Registration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire the RabbitMQ bus into the template service — one registration
helper in `Catalog.Infrastructure/Messaging`, readiness carried by
MassTransit's own bus health check, publish/consume proven with the in-memory
harness, a real broker in the Testcontainers fixture — and reconcile the
blueprint's health-check samples, the licence register and the scaffold in the
same PR.

**Architecture:** Per the frozen spec
(`specs/2026-08-10-pr13-masstransit-registration-design.md`):
`AddMassTransitMessaging(IServiceCollection, IConfiguration)` mirrors the
`Common.Infrastructure/Redis/DependencyInjection.cs` shape; eager
`ConnectionStrings:RabbitMq` read that throws naming the key;
`DisableUsageTelemetry()`; `WaitUntilStarted` left false so readiness carries
the wait; the Xabaril RabbitMQ health check is removed from the blueprint as a
latent defect (parameterless `AddRabbitMQ()` resolves an `IConnection` nothing
registers).

**Tech Stack:** .NET 10 / C# 14, MassTransit.RabbitMQ 8.5.3,
Testcontainers.RabbitMq 4.6.0, xunit.v3 + Shouldly.

## Global Constraints

- House C# dialect (CLAUDE.md): explicit types unless the RHS names the type;
  spread over `.ToArray()`; file-scoped namespaces; blank line after
  `namespace X;`; four-space indent, CRLF, newline at EOF; British spelling in
  comments, identifiers keep their real spelling; no `#pragma`.
- `TreatWarningsAsErrors` — a warning is a stop.
- Central package management — no `Version=` on a `PackageReference`; the pin
  *removal* here must keep the licence gate green (register row amended in the
  same task).
- Blueprint reconciliation in the same task that contradicts a chapter.
- Docker required for the container tests; no skip, no category.
- A Catalog change that breaks `tools/new-service` is reconciled in the same
  change; `py -3.12 -m unittest` is the gate.

---

### Task 1: The registration helper and its unit tests

**Files:**
- Create: `src/Services/Catalog/Catalog.Infrastructure/Messaging/DependencyInjection.cs`
- Modify: `src/Services/Catalog/Catalog.Infrastructure/Catalog.Infrastructure.csproj` (add `MassTransit.RabbitMQ`)
- Modify: `src/Services/Catalog/Catalog.Infrastructure/DependencyInjection.cs` (call the helper)
- Create: `tests/Catalog.Api.Tests/MessagingRegistrationTests.cs`

- [ ] **Step 1:** Write `MessagingRegistrationTests` first (TDD):
  publish/consume through `AddMassTransitMessaging` +
  `AddMassTransitTestHarness` with a test-local record and consumer;
  registration surface (bus + hosted service descriptors, no provider build);
  missing `ConnectionStrings:RabbitMq` throws `InvalidOperationException`
  naming the key. Test-local message is a positional record in the test file.
- [ ] **Step 2:** Implement the helper exactly as the spec prints it; add the
  package reference with a comment in the csproj's established voice; call
  `services.AddMassTransitMessaging(configuration)` from
  `AddCatalogInfrastructure` beside the health-check block, with a comment
  noting the bus health check rides in with `AddMassTransit`.
- [ ] **Step 3:** `dotnet build`, run the new tests (no Docker needed), red →
  green in order.

### Task 2: Fixture, host smoke, readiness poll

**Files:**
- Modify: `tests/Catalog.TestSupport/ServiceFixture.cs` (RabbitMqContainer, `Task.WhenAll`, doc comment)
- Modify: `tests/Catalog.TestSupport/CatalogApiFactory.cs` (second parameter + `UseSetting`)
- Modify: `tests/Catalog.TestSupport/Catalog.TestSupport.csproj` (`Testcontainers.RabbitMq`)
- Modify: `tests/Catalog.Api.Tests/HostSmokeTests.cs` (rename factory, two-check assertion)
- Modify: `tests/Catalog.Api.Tests/DatabaseSmokeTests.cs` (readiness poll)

- [ ] **Step 1:** `CatalogApiFactory(string connectionString, string rabbitConnectionString)`;
  both `UseSetting`s. `ServiceFixture` starts SQL and RabbitMQ with
  `Task.WhenAll` (§12.4's printed shape), passes both to the factory; fixture
  doc comment now says Redis alone still waits.
- [ ] **Step 2:** `HostSmokeTests`: `UnreachableInfrastructureFactory` with
  `.invalid` hosts for both; `Ready_probe_reports_the_sql_and_bus_checks`
  asserts `{sql, masstransit-bus}`, both tagged `ready`; liveness and 503
  tests unchanged in behaviour.
- [ ] **Step 3:** `Ready_probe_returns_200_against_a_real_database` becomes a
  poll to 200 within 30 s (bus connects in the background), with the comment
  arguing why polling is the honest assertion.
- [ ] **Step 4:** Full `dotnet test` with Docker up.

### Task 3: Compose

**Files:**
- Modify: `deploy/compose/docker-compose.yml` (catalog-api env + depends_on + comment)

- [ ] **Step 1:** `ConnectionStrings__RabbitMq: "amqp://guest:guest@rabbitmq:5672"`
  (plain value, §14.1's ordering-api spelling); `rabbitmq: { condition:
  service_healthy }`; rewrite the env comment (bus key read since PR-13;
  Redis, outbox, JWT still pending with their PRs).
- [ ] **Step 2:** `docker compose -f deploy/compose/docker-compose.yml config -q`.

### Task 4: Blueprint reconciliation

**Files:**
- Modify: `docs/backend-architecture/04-solution-structure.md` (§4.2 health block)
- Modify: `docs/backend-architecture/13-observability.md` (§13.5 block + prose)
- Modify: `docs/backend-architecture/appendix-b-licences.md` (health-check row)
- Modify: `docs/backend-architecture/appendix-d-type-inventory.md` (D.5 row)
- Modify: `Directory.Packages.props` (remove `AspNetCore.HealthChecks.Rabbitmq`)

- [ ] **Step 1:** Both samples lose `.AddRabbitMQ(name: "rabbitmq", tags:
  ["ready"])`; each gains the one-line comment that the bus check
  (`masstransit-bus`, tagged `ready`) is registered by `AddMassTransit`
  itself. §13.5 prose gains the why — the `IConnection` trap and the
  second-connection cost.
- [ ] **Step 2:** Appendix B row 44 loses the Rabbitmq identity; argument
  adjusted. Pin removed. Run the licence gate's tests
  (`py -3.12 -m unittest` in `.github/licence-gate`).
- [ ] **Step 3:** Appendix D D.5: describe `AddMassTransitMessaging` now it
  exists (per-service home, eager key read, telemetry off).
- [ ] **Step 4:** Grep `AddRabbitMQ`, `HealthChecks.Rabbitmq`, `RabbitMq`
  across docs for stragglers.

### Task 5: Scaffold reconciliation

**Files:**
- Modify: `tools/new-service/new_service.py`
- Modify: `tools/new-service/test_new_service.py`

- [ ] **Step 1:** Classify the new files (`Messaging/DependencyInjection.cs`,
  `MessagingRegistrationTests.cs` — template, name-substituted), update every
  anchor the edits moved (root `DependencyInjection.cs`, `ServiceFixture`,
  `CatalogApiFactory`, `HostSmokeTests`, `DatabaseSmokeTests`, the compose
  block, the Infrastructure csproj).
- [ ] **Step 2:** Extend the scaffold's tests for the new template files and
  compose lines; `py -3.12 -m unittest` green.

### Task 6: CLAUDE.md, validation, ship

- [ ] **Step 1:** CLAUDE.md phase section: PR-13 landed with its binding
  decisions (helper home, readiness mechanism, telemetry off, no consumers),
  PR-14 next; tree annotations; test counts from actual runs.
- [ ] **Step 2:** `/validate-blueprint`; fix findings.
- [ ] **Step 3:** Full verification: `dotnet build`, `dotnet test`, both
  Python suites, compose config. Then `/ship`.
