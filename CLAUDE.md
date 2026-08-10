# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this repo is

`dotnet-ddd-blueprint` is a monorepo for an ASP.NET Core microservices platform
built with DDD, CQRS and TDD. **PR-01 through PR-13 have landed**, so the repo
is the blueprint under `docs/backend-architecture/`, the foundation that
blueprint specifies — SDK pin, central package management, the solution file,
CI and the licence gate — the building blocks: `Common.Domain`,
`Common.Application`, `Common.Infrastructure` and `Common.Web`, each with its
test project — §14.1's Compose infrastructure, with the CI smoke that proves
it — Catalog as the first real service: §4.1's five projects, §4.2's gates
live, PR-08's persistence, PR-09's transaction behaviour, and PR-10's
vertical slice with its containers — PR-11's scaffold under
`tools/new-service/` — PR-12's Redis helpers, §8 as code — and PR-13's bus
registration, the first of §9's three instalments. The phase section below
carries the current state; this sentence only names the shape.

**The C# solution will land in this repo.** The blueprint is the specification
for it, and Appendix C sequences that code into 26 pull requests starting with
`chore: solution structure, SDK pin, central package management, CI skeleton`.
Treat every chapter as a commitment the implementation will have to honour.

Both READMEs read **"Reference blueprint — adapt, don't copy wholesale. The C#
solution it specifies will be built in this repository"** — the blueprint serves
a visiting reader and this repo's own build at once.

Keep two different things apart:

- **Solution shape** — the projects §4.1 lays out: Catalog, Ordering, Inventory,
  Payments, Shipping, Notifications, plus the building blocks, gateway and BFF.
- **Service build order** — Appendix C.1, which is *not* the §4.1 listing
  order: **Catalog → Ordering → Inventory and Payments → Shipping →
  Notifications**. Notifications goes **last**, and the reason is worth
  carrying: it publishes nothing and its whole contract is seven events owned
  by Ordering, Payments and Shipping (§3.2), so before those exist it is a
  consumer with no producers. C.1 used to say it went first, on the grounds
  that a service with no domain logic proves the pipeline end to end with
  nothing else to debug — appealing, and wrong, because end to end needs both
  ends. C.2 never built it first either: PR-10 is Catalog and PR-18 is the
  "second service".

One thing is genuinely **undecided**: the READMEs call the e-commerce domain
"illustrative only", while §4.1 and Appendix C name those six services
concretely. Whether the real solution keeps that domain or substitutes another
has not been settled. Until it is, build the structure the blueprint specifies
and raise the domain question rather than assuming.

Present:

```
docs/backend-architecture/
  README.md                      index and chapter table
  01-purpose.md .. 15-cicd-deployment.md
  appendix-a-adrs.md             ADR-001 .. ADR-019
  appendix-b-licences.md         dependency licence register
  appendix-c-delivery-plan.md    PR sequencing plan
  appendix-d-type-inventory.md   type inventory
docs/roadmap.md                  estimates and calendar over Appendix C
docs/superpowers/
  specs/                         one design spec per PR, frozen at write time
  plans/                         its implementation plan, frozen the same way

global.json                      SDK pin (§4.4)
.config/dotnet-tools.json        dotnet-ef, pinned to the EF Core version —
                                 `dotnet tool restore` is the whole setup
Directory.Build.props            shared MSBuild settings, ADR-019's policy
Directory.Packages.props         central package management, exact pins
Platform.slnx                    the seventeen projects below
.editorconfig                    house style; a build input, not a hint
.github/workflows/ci.yml         licence gate and scaffold tests, then
                                 restore/build/test
.github/licence-gate/            the gate, its allow-list and its tests

tools/new-service/               §4.5's scaffold, its tests and its README.
                                 Stdlib Python, no restore. Renders a service
                                 from src/Services/Catalog at RUN TIME — there
                                 is no template directory, so a Catalog change
                                 that breaks it fails `py -3.12 -m unittest`
                                 here rather than six months later
.github/workflows/compose.yml    path-filtered smoke on deploy/compose/**:
                                 config -q, up --wait, down -v — and, since
                                 PR-10's build: stanzas, an image build

deploy/compose/                  §14.1's infrastructure — seven services,
                                 .env.example, the placeholder realm PR-16
                                 replaces, the collector config, a ports
                                 README; PR-10 added the first application
                                 pair (catalog-migrator + catalog-api, port
                                 5102) and the infra-only override — later
                                 blocks arrive with their services

src/BuildingBlocks/
  Common.Domain/                 Entity<TId>, AggregateRoot<TId>, IDomainEvent,
                                 IHasDomainEvents, IAggregateRoot,
                                 DomainException — no packages
  Common.Application/            Result, Result<T>, Error, ErrorType; the §6.2
                                 dispatcher and its three behaviours, plus
                                 RequestMetrics, PluggableInterfaces,
                                 InvariantViolationException, §6.5's
                                 CursorPage<T> and Cursor codec, and three
                                 ports: §6.3's IUnitOfWork, §6.5's
                                 IDbConnectionFactory and §7.5's
                                 IDomainEventDispatcher — the last an
                                 interface only, everything behind it waits
                                 for PR-14
  Common.Infrastructure/         §8 as code, one Redis/ folder:
                                 RedisConnections (keyed names, spelled like
                                 the configuration keys), AddRedisConnections
                                 (two keyed multiplexers read eagerly,
                                 HybridCache over the cache one via
                                 ConnectionMultiplexerFactory, InstanceName
                                 from RedisKeys, the Redis tracing
                                 instrumentation with both connections),
                                 RedisKeys, and IDistributedLockFactory —
                                 SET NX PX, mandatory TTL, token-checked
                                 release. NO project references, deliberately:
                                 PR-14's outbox is what draws edges here
  Common.Web/                    UseCorrelationId, AddCommonProblemDetails
                                 (which also registers §10.5's
                                 ValidationExceptionHandler — the 400 row's
                                 executor), ToHttpResult, AddObservability,
                                 MapCommonHealthEndpoints, SensitiveDataRedactor,
                                 BuildInfo and the AddCommonWebDefaults that
                                 composes them — the only building block
                                 referencing another, and the only one with a
                                 FrameworkReference (Catalog.Api's rides in
                                 with Sdk.Web)
src/Services/Catalog/
  Catalog.Domain/                the first aggregate: Product (Publish factory,
                                 ProductPublishedDomainEvent), ProductId,
                                 Catalog's own Money, IProductRepository —
                                 Add only, argued in the file. Product is the
                                 gates' typeof anchor; AssemblyMarker is gone
  Catalog.Application/           AddCatalogApplication: the §6.2 scan, the
                                 dispatcher, the NullDomainEventDispatcher
                                 PR-14 replaces (§4.2 registers the dispatcher
                                 in Application), the clock, RequestMetrics,
                                 the three behaviours in pipeline order, the
                                 §4.2 validator scan; two slices —
                                 Products/PublishProduct (command, validator,
                                 handler) and Products/GetProducts (§6.5's
                                 Dapper keyset query over (PublishedAt, Id))
  Catalog.Infrastructure/        AddCatalogInfrastructure(IConfiguration): the
                                 §6.2 scan, the sealed CatalogDbContext with
                                 §7.2's conventions, EfUnitOfWork,
                                 ProductConfiguration, ProductRepository,
                                 SqlConnectionFactory over the runtime key,
                                 §13.5's SQL readiness check, two migrations —
                                 InitialCreate (hand-written EnsureSchema) and
                                 AddProducts (generated DDL, hand-dressed
                                 only) — and, since PR-13, Messaging/
                                 AddMassTransitMessaging: the RabbitMQ bus,
                                 eager ConnectionStrings:RabbitMq read, usage
                                 telemetry off, no consumers yet
  Catalog.Migrator/              §7.4's job host: MigratorHost builds it,
                                 MigrationRunner migrates and returns 0 or 1.
                                 Reads ConnectionStrings:CatalogMigrator and
                                 never the runtime key (§7.1). Has a
                                 Dockerfile since PR-10, as does Catalog.Api
  Catalog.Api/                   the composition root of §4.2 minus PR-16:
                                 health probes, OpenAPI, and
                                 Endpoints/ProductEndpoints — POST and GET
                                 /v1/catalog/products, deliberately
                                 unauthenticated, stated in
                                 deploy/compose/README.md
tests/
  Common.Domain.Tests/           xunit.v3 + Shouldly; TestModel.cs holds the
  Common.Application.Tests/      anonymous sample types both suites build on;
                                 TestContainer.cs is the one registration path
  Common.Infrastructure.Tests/   a unit half — RedisKeys shapes, lock guards
                                 with the failed-release retry, the
                                 registration surface read off the
                                 IServiceCollection — and a Testcontainers
                                 Redis half: lock lifecycle with the
                                 stale-handle case, the §8.1 ACL grant
                                 proven live, prefix + TTL asserted on the
                                 server, tag invalidation, and two span
                                 tests — one per keyed connection. The third
                                 Docker-needing project, its own
                                 IntegrationCollection
  Common.Web.Tests/              + Microsoft.AspNetCore.TestHost; TestPipeline.cs
                                 starts the real middleware pipeline in memory
  Catalog.TestSupport/           NOT a test project (§4.1): ServiceFixture —
                                 SQL and RabbitMQ containers, real migrator
                                 run, Respawn reset — and CatalogApiFactory
                                 (both connection strings), shared by the
                                 two suites below, which cannot reference
                                 each other
  Catalog.Domain.Tests/          §4.2's gates in §12.1's homes: domain isolation
  Catalog.Application.Tests/     (allow-list now includes System.Collections and System.Linq —
  Catalog.Api.Tests/             a record's generated equality); ↛ EF Core,
                                 ↛ MassTransit, + registration surface +
                                 validator unit tests + the container-backed
                                 handler and pagination suites; endpoints ↛
                                 Infrastructure, + the host smoke (now two
                                 ready checks, sql and masstransit-bus), the
                                 messaging harness smoke, the endpoint
                                 contract tests and the Testcontainers suite
                                 over the real migrator, EfUnitOfWork and the
                                 bus-connect readiness poll — with
                                 Common.Infrastructure.Tests above, three
                                 projects need Docker, one collection each
```

The second block is PR-01's, the third PR-02's through PR-05's, the
compose tree PR-06's, the Catalog trees PR-07's, their persistence
PR-08's, the third behaviour with its two ports PR-09's, the slices,
endpoints, TestSupport and container half PR-10's, and
`Common.Infrastructure` with its tests PR-12's.
`Common.Application` does **not** reference `Common.Domain` yet — §4.2
permits it, but an unused project reference is a claim about the dependency
graph that nothing yet makes true. PR-08's `IUnitOfWork` did not change that
and could not: no member of it names a domain type, which is the whole reason
`ExecuteRawAsync` takes `string` and `object`. **The edge is not PR-09's**,
though this file and two comments beside it said so until a review checked the
claim — §6.3's behaviour reads `ModifiedAggregateCount` as an `int`, and the
`is IAggregateRoot` test it is derived from lives in `EfUnitOfWork`, on
Infrastructure's side of §4.2. Counting behind the port is precisely what
keeps it there. The edge belongs to the first Application type that really
names a domain type, which on the plan is §7.5's `IDomainEventCollector` and
its `IReadOnlyList<IDomainEvent>` — so it arrives with the outbox.
`Common.Web → Common.Application` is the one edge that has been drawn, because
`ToHttpResult` maps an `Error` and cannot be written without one.

The licence gate lives under `.github/` rather than a `build/` directory
because it is CI-only and §4.1 draws no such tree. It is stdlib Python, reads
`Directory.Packages.props` and Appendix B as text, and needs no restore — the
reason §15.1 can put it ahead of the build. **Adding a package means adding its
backticked identity to Appendix B in the same change**, or the gate fails the
build before anything compiles.

`docs/roadmap.md` sits outside the blueprint tree deliberately — it is a
schedule, not a specification, and it goes stale on a different clock. Nothing
in it states a requirement: it prices Appendix C's 26 PRs in ideal
engineer-days and derives a calendar from one stated ratio. **Where the two
disagree, Appendix C wins**, always. Because it is outside the tree, no nav
footer or index row will catch its drift — `/validate-blueprint` check 10 is
the only thing that does, which is why the roadmap is named in that command's
scope rather than left to the directory glob.

`docs/superpowers/` sits outside the blueprint tree for a different reason, and
it is the stronger one: these files are a **frozen historical record**, not a
specification. Each pair — a design spec and the implementation plan derived
from it — records how one PR was thought through *before* it was built, and
PR-05's is the first. They are written once and left alone. **Where one
disagrees with the blueprint, the blueprint wins**, and the disagreement is not
a defect to reconcile: it is the record showing where the design moved during
implementation, which is the only thing these files are for. PR-05's plan
still carries a `SourceRevisionId, §4.4` citation that the shipped code
corrected, and that stale line is left standing deliberately.

So they are **deliberately outside `/validate-blueprint`'s scope**, and unlike
the roadmap they are not named in it either. A drift check on a document whose
whole value is being stale would fail on every entry by design, and "fixing" it
would destroy the record. Do not edit a spec or a plan to match the code that
followed it — amend the chapter instead, which is where the specification
actually lives.

`tools/new-service/` took the opposite decision to the licence gate's, and the
difference is what each thing is. The gate is CI-only, so it lives under the CI
provider's directory and §4.1 stays silent about it. The scaffold is a
developer tool that happens to be tested in CI, so filing it under `.github/`
would have filed it by its least important property — **§4.1's tree gained a
`tools/` entry and §4.5 documents the script instead**, the honest fix for "the
blueprint draws no such tree" being to draw it.

**Changing Catalog can break the scaffold, and the failure is loud.** The
script names exact text inside `src/Services/Catalog` and `tests/Catalog.*`,
and every anchor must match exactly once. It also classifies **every** file
under those roots as template or slice and refuses to run on one it has never
seen — so a new file in Catalog is a decision the scaffold forces, the same way
the domain allow-list gate forces one. If `py -3.12 -m unittest` in
`tools/new-service` goes red after a Catalog change, reconcile the script in
the same change; that is the price of having one copy of the wiring instead of
two.

Planned, per §4.1 — do not invent a different shape for it. The three building
blocks built so far are shown above; the tree below is the target shape, and
its annotations mark what has already landed:

```
src/BuildingBlocks/   .Contracts (the other four exist)
src/Gateway/          Gateway.Api (YARP)
src/BFF/              Web.Bff
src/Services/         Catalog, Ordering, Inventory, Payments — five projects each:
                        Domain, Application, Infrastructure, Migrator, Api
                        (Catalog's five landed with PR-07, as shells)
                      Shipping — the same five, but Worker in place of Api
                      Notifications — four: no Domain, and a Worker
tests/                <Service>.Domain.Tests, .Application.Tests, .Api.Tests,
                      .TestSupport, plus Platform.IntegrationTests
                        (Catalog's first three landed with PR-07; TestSupport
                        with PR-10, when the container-backed handler tests
                        became §4.1's second consumer — "referenced by the two
                        above, which cannot reference each other")
deploy/               helm/, k8s/ — compose/ landed with PR-06
Directory.Build.props, Directory.Packages.props, Platform.slnx — landed with PR-01
```

Three things live outside that tree because §4.1 does not draw them:
`global.json`, which PR-01 delivers and whose SDK pin §4.1's prose relies on for
the `.slnx` floor; `.config/dotnet-tools.json`, which PR-08 delivers and which
pins `dotnet-ef` to the EF Core version — the machine that built PR-08 had the
8.0.11 tool against a 10.0.0 runtime, and the error names neither; and
`src/AppHost`, the optional Aspire host of §14.2. Aspire
is **not adopted** — Compose is the baseline (§14.1) and nothing references an
`Aspire.*` package today, which is why §4.4 pins none of them. If it is adopted,
`src/AppHost` is the only project taking `Aspire.Hosting.*`, but each service
picks up the client integrations for the resources it consumes — so backing it
out again costs a line per resource per service, not one deletion (§14.2).

### Which phase are you in

`Platform.slnx` holds seventeen projects and `dotnet test` runs 247 tests, so
the build rules and the drift rules below are live and a green run now means
something. Since PR-11 there is a second suite with a second runner:
`py -3.12 -m unittest` in `tools/new-service` runs 73, and CI has a `scaffold`
job for them beside `licence-gate`. **PR-14 is next**
(`feat(template): transactional outbox and allow-list event mapper`).
PR-07 landed the Catalog skeleton, so §4.2's architecture rules are a
build failure — each gate was observed red against a deliberately added
forbidden reference before it was trusted, and since PR-10 the endpoints gate
judges a real type (`ProductEndpoints`) rather than passing vacuously.

PR-13 landed the bus — `AddMassTransitMessaging` in
`Catalog.Infrastructure/Messaging`, the RabbitMQ registration of §9 with no
consumer on it yet — and five of its decisions bind what comes after:

- **The helper is per-service, in the `Redis/DependencyInjection` shape.** It
  is where each service's consumers, sagas and receive endpoints will be
  configured (§9.6 registers Ordering's saga inside it), and it keeps
  MassTransit out of `Common.Infrastructure` until PR-14's outbox — the first
  common code that names a MassTransit type (`IPublishEndpoint`).
- **Broker readiness is MassTransit's own health check.** `AddMassTransit`
  registers `masstransit-bus`, tagged `ready`, itself — verified in the 8.5.3
  source — so no health-check line exists for the bus and the
  `AspNetCore.HealthChecks.Rabbitmq` pin is gone: its parameterless
  `AddRabbitMQ()` resolves an `IConnection` nothing registers, a latent
  defect §13.5 now documents. `WaitUntilStarted` stays false; readiness
  carries the wait, and `DatabaseSmokeTests` polls ready to 200 to prove the
  bus connects against a real broker.
- **`ConnectionStrings:RabbitMq` is read eagerly and throws naming the key**
  (the `AddSqlServer` posture), so every host over `Program` — fixtures
  included — must supply one; `ServiceFixture` therefore carries a RabbitMQ
  Testcontainer beside SQL, and `CatalogApiFactory` takes both connection
  strings.
- **Usage telemetry is off.** MassTransit 8.5 reports anonymous usage data to
  a vendor endpoint by default; `DisableUsageTelemetry()` is called with the
  argument in the registration — §13.2 owns this platform's telemetry.
- **The harness smoke proves composition; the readiness poll proves the
  transport.** `AddMassTransitTestHarness` replaces an existing
  `AddMassTransit` bus with the in-memory transport (verified at the pin), so
  `MessagingRegistrationTests` proves the helper composes and the pipeline
  delivers — and deliberately not the `UsingRabbitMq` half, which the swap
  removes and `DatabaseSmokeTests` asserts against a real broker. A
  test-local record carries the smoke: no contract invented before
  `Common.Contracts` (PR-15), no retry policy before the receive endpoints
  it attaches to (§9.8).

PR-12 landed §8 as code — `Common.Infrastructure`, the fourth building block,
one `Redis/` folder — and five of its decisions bind what comes after:

- **`Common.Infrastructure` has no project references, and that is a claim to
  preserve.** Nothing in the Redis helpers names a domain or application
  type, so no edge is drawn — the `Common.Application ↛ Common.Domain`
  argument, one project over. PR-14's outbox is what draws edges here;
  drawing one earlier is inventing a dependency the code does not have.
- **The Redis tracing instrumentation lives in `AddRedisConnections`, and can
  never move to `Common.Web`.** The connections are keyed services; the
  parameterless `AddRedisInstrumentation()` discovers only an unkeyed
  `IConnectionMultiplexer`, so in `AddObservability` it would silently
  instrument nothing — and the package reference would hand
  `StackExchange.Redis` to hosts with no Redis. §13.2 says this; the sample
  there deliberately does not show the call.
- **No service is wired.** Catalog gained no Redis env vars, no readiness
  checks and no cached query — caching a read before ADR-018's invalidation
  machinery exists (PR-14) would teach the defect §8.4 exists to prevent.
  The helpers are proven by their own Testcontainers suite, the same shape
  as PR-04's dispatcher landing three PRs before its first service. The
  Redis keys join the Compose file with the PR whose code first reads them.
- **The key prefix is `ApplicationName` verbatim — no normalisation.** One
  source shared with §13.2's `service.name`, nothing to drift. §8.3's
  lowercase examples show a service whose ApplicationName is `catalog`, not
  a lowering rule. `RedisKeys` has deliberately no `Cache(string)` method:
  cache keys are prefixed by `InstanceName`, and a full-key builder would
  double-prefix the moment its result reached `HybridCache`.
- **`RemoveByTagAsync` works at this pin, verified.** §8.4's invalidation
  mechanism was proven by the container suite two PRs before its consumer —
  along with the mandatory TTL on the lock (refused before any I/O), the
  token-checked release (a stale handle must not delete the next holder's
  key), and the span tests — one per keyed connection — which force the
  `TracerProvider` the way a host's startup would because a raw
  `ServiceProvider` runs no hosted services.

PR-11 landed the scaffold of §4.5 — `tools/new-service/new_service.py`, stdlib
Python, one command per service — and five of its decisions bind what comes
after:

- **Catalog is the template, read at run time.** There is no template
  directory, so there is one copy of the wiring rather than two that drift, and
  the scaffold's tests render *this* repository. The consequence is stated
  above and worth repeating here: a Catalog change can turn
  `tools/new-service`'s suite red, and reconciling the script belongs in the
  same change.
- **The scaffold copies no domain.** The slice is excluded by name, so a new
  service is PR-07's state with the wiring accumulated through PR-13 on it —
  five service projects, three test projects and a `TestSupport` library
  (§4.1 calls that last one *not* a test project, and counting it as one is a
  drift a review has already caught here), both images, the Compose pair, the
  `InitialCreate` migration, the bus registration with its harness smoke, and
  thirty passing tests, and no aggregate.
  Three things arrive with the first real slice, each noted at the line
  concerned in the generated code: `Dapper`, the application-test container
  wiring and the two silent-scan registration tests.
- **`AssemblyMarker` runs the other way, and it is easy to state backwards.**
  The scaffold **emits** it — a service with no domain type has nothing for the
  two §4.2 gates to name — and the first aggregate is when it is **deleted**
  and the gates re-anchor, which is what PR-10 did to Catalog's when `Product`
  arrived. It does not "arrive with the first slice"; it leaves then. Seeing
  one in a service that *has* an aggregate is a defect, not a convention.
- **The template has no single line ending, and a tool that reads it must not
  assume one.** `.gitattributes` forces `*.cs text eol=crlf`, so C# is CRLF on
  every machine — but `.csproj`, `.slnx`, the Compose YAML, the Markdown and
  the Dockerfiles carry no attribute and arrive CRLF on Windows and **LF on the
  Ubuntu runner**. The scaffold's first version spelt its anchors with CRLF,
  passed on the machine that wrote it and matched nothing in CI. Anchors are LF
  now, matched against normalised text, with each file's own endings restored
  on the way out. Anything else in this repository that reads a file as text
  and looks for a literal line has the same trap waiting.
- **The generated model snapshot is EF's own output, not a hand-written copy.**
  It is derived from `InitialCreate.Designer.cs`, which already holds the
  tool's description of an empty model with a default schema. Verified rather
  than argued: a scaffolded service was built, `dotnet ef migrations add` was
  run against it, the generated `Up` was empty and EF's rewritten snapshot was
  byte-identical to the emitted one. Two details were found only by that diff —
  EF sorts its `using` block by namespace (so `;` must not participate in the
  sort), and the sort order changes when the service name passes `Microsoft`.

PR-10 landed the first vertical slice — `Product`, `PublishProductCommand`,
`GetProductsQuery` with §6.5's cursor pagination, the two Dockerfiles, the
Compose pair on port 5102 and the `docker-compose.infra-only.yml` override
(profiles technique, printed in §14.1) — and five of its findings bind what
comes after:

- **`ValidationExceptionHandler` is §10.5's 400 row, found by the first real
  endpoint.** Until PR-10 nothing translated `ValidationBehavior`'s thrown
  `ValidationException`, and the wire answered 500 for a malformed request.
  The handler lives in `Common.Web`, registered by `AddCommonProblemDetails`,
  and §10.5 now names it — the chapter previously implied the translation
  without showing it.
- **Locally there is one `sa` login and two configuration keys.** §7.1's
  callout used to claim Compose seeds both logins; §14.2, §12.4's fixture and
  the shipped Compose file all collapse the logins and keep the keys apart,
  and §7.1 was amended to match. The identity split is a cloud-side control;
  the key split is what every local environment exercises.
- **`Catalog.TestSupport` exists**, because PR-10 was the second consumer §4.1
  was waiting for (not PR-16, as this file once guessed): the handler tests
  live in `Catalog.Application.Tests` per §12.1 and share `ServiceFixture`
  with `Catalog.Api.Tests`. It is a Library, so it references
  `xunit.v3.extensibility.core` — `xunit.v3` itself refuses non-Exe output.
- **The compose smoke now builds images.** The application blocks carry
  `build:` stanzas, so the path-filtered workflow compiles the solution inside
  Docker; its timeout rose to 25 minutes and its header says why. A change
  under `src/` alone does not re-run it — per-service CI builds are PR-25's.
- **Chiselled images take the `-extra` tag, and the suffix is load-bearing.**
  Plain chiselled runs globalization-invariant and `Microsoft.Data.SqlClient`
  refuses to open a connection under it — found when the containerised
  migrator first ran, fixed in both Dockerfiles and §15.2's samples. Every
  later service image inherits this: `-extra` is ICU and tzdata, nothing
  else. Verified live: `up --wait` treats a `service_completed_successfully`
  one-shot as satisfied on exit 0 and failed on exit non-zero, so the smoke
  asserts the migrator's exit code for free.

PR-09 landed §6.3's `TransactionBehavior` and did **not** draw the
`Common.Application → Common.Domain` edge — the behaviour reads
`ModifiedAggregateCount` as an `int` and calls `DispatchAsync(CancellationToken)`,
so neither signature names a domain type, and the edge still waits for §7.5's
`IDomainEventCollector` (PR-14). It brought `IDomainEventDispatcher` forward as
an interface only, over Catalog's `NullDomainEventDispatcher`.

PR-09 also shipped PR #15's retry fix — `db.ChangeTracker.Clear()` at the top
of every `EfUnitOfWork.ExecuteAsync` attempt, so a transient fault cannot
re-run the domain method on attempt 1's tracked, already-mutated aggregates
and commit the mutation twice. Both halves are tested: a strategy subclass
retrying a marker exception proves the delegate re-runs and the raw write
commits once, and the identity-map half — attempt 2 must read committed
state, not attempt 1's mutation — is asserted through a **test-only
`IModelCustomizer`** that maps a `TrackedProbe` entity onto the fixture's
probe table in the retry tests' own `DbContextOptions` and nowhere else. That
was first deferred to PR-10 as needing an entity type; a Copilot review on
PR #18 pushed back, and the customizer is the answer that costs neither a
production model change nor snapshot drift. The technique generalises: a test
that needs an entity the model does not have swaps the customizer, never
edits `CatalogDbContext`.

**Two standing facts, restated here rather than left in commit bodies:**

- **Raised events are dropped until PR-14, and since PR-10 that is live, not
  hypothetical.** Every `Product.Publish` raises a
  `ProductPublishedDomainEvent` that `NullDomainEventDispatcher` swallows.
  The aggregate raises anyway — the domain must not teach the defect of not
  raising, the unit test pins the payload, and PR-14's outbox picks it up
  without touching `Product`.
- **`IdempotencyBehavior`'s seat.** The pipeline registers three of four
  behaviours; the missing one slots in *between* Validation and Transaction,
  and the registration comment names the seat. `PublishProductCommand`
  carries no `CommandId` for the same reason — §6.4 warns the field without
  the interface is unprotected, so both join with §8.5's PR.

**What PR-09's line does not fix is the commit-acknowledgement race**, and that
stays open past it on purpose. If `CommitAsync` succeeds on the server and
the connection drops before the ack, the strategy retries work that is already
durable, and no in-process tidying can tell those two states apart. Closing it
needs an idempotency marker written *inside* the transaction — §8.5's
`IIdempotentCommand` already carries a usable `CommandId`, but
`IIdempotencyStore` is Redis-backed and outside the transaction, so a Redis
claim is not atomic with the SQL commit. That decision belongs with PR-14,
where the outbox is what makes a double-apply externally visible and where a
SQL-side marker would live.

PR-08 landed the persistence layer, and three of its decisions bind what comes
after:

- **Catalog has a connection string, so it has a readiness check** (§13.5), and
  a host with no `ConnectionStrings:Catalog` no longer starts —
  `AddSqlServer` throws on a null one. Every `WebApplicationFactory` over
  `Catalog.Api` supplies one; `CatalogApiFactory` — in `Catalog.TestSupport`
  since PR-10 — is the single place that does it.
- **The migration is hand-authored and the snapshot is not.**
  `20260808035156_InitialCreate.cs` was rewritten into house style, because it
  is a file people edit — §7.4's hand-written DDL rides in its `Up`, and
  IDE0161 fails the build on the block-scoped namespace EF generates. The
  `.Designer.cs` and `CatalogDbContextModelSnapshot.cs` beside it carry an
  `auto-generated` header that exempts them from the analysers and are left
  **exactly** as the tool wrote them: the snapshot is the input to the next
  `migrations add`, and an edited one produces a wrong migration a PR later.
- **`dotnet test` needs Docker** — since PR-12 for three projects,
  `Catalog.Api.Tests`, `Catalog.Application.Tests` and
  `Common.Infrastructure.Tests`, each with its own `IntegrationCollection`
  and therefore its own container set (§12.4's stated price). See the
  commands below.

The building blocks are four of five. `Common.Contracts` does not exist, so a
change that "obviously belongs" in it is a change that belongs in the PR that
creates it (Appendix C, PR-15), not in a project invented early. The same rule
applies inside the ones that do exist: `Common.Infrastructure` holds §8's
Redis helpers and nothing else until PR-14's outbox joins it, and `Common.Web`
holds §10.4, §10.5, §13.2, §13.4 and §13.5, and nothing else until PR-16 adds
JWT validation — which is also the one gap inside `AddCommonWebDefaults`,
three of §13.2's five pieces today.

`Common.Application` is the same story one layer down. The pipeline is three
behaviours of four: **`IdempotencyBehavior` (§8.5) does not exist**, and its
seat is between Validation and Transaction. Built to be appended to in the
same way is
`PluggableInterfaces.All`, which lists two of its eventual five — the three
missing entries name interfaces §7.5 and §9.4 have not defined yet, and the
list is built to be appended to. Adding an interface there and nowhere else is
the design; adding one before its PR is inventing a project early by another
route.

The commands are the ones the target solution uses:

```bash
dotnet tool restore                # dotnet-ef, pinned in .config/
dotnet restore Platform.slnx
dotnet build Platform.slnx
dotnet test  Platform.slnx         # needs a running Docker daemon
```

Two suites, two runners. The scaffold's tests are Python and are **not** in
`Platform.slnx`, so `dotnet test` says nothing about them:

```bash
cd tools/new-service && py -3.12 -m unittest    # 73 tests, no Docker, no SDK
python tools/new-service/new_service.py <Name> --port <51xx>
```

**`py -3.12`, not `python`, and the block above is written that way on
purpose.** Both CI jobs pin Python 3.12; the default interpreter here is 3.14.
A newer one is the hazard, not an older one — it accepts APIs 3.12 does not, so
the local suite goes green on code the runner cannot execute.
`Path.read_text(newline=…)` is 3.13 and cost a CI round exactly that way. The
scaffold *script* is a different matter: running it is not a test of the floor,
so plain `python` is fine there.

3.12 is installed here, so both suites can be run against it:

```bash
py -3.12 -m unittest        # from tools/new-service and .github/licence-gate
```

This used to be carried by review, for want of a 3.12 to check against. It no
longer is, and that is the better answer: the rule the reviewer was asked to
hold is now a command that either passes or does not.

**`dotnet test` requires Docker from PR-08**, and the container tests are
neither skipped nor categorised when it is absent. Both were considered and
both fail in a way this repository has rejected before: a skip on a missing
daemon **fails open**, so CI would go green on a runner whose Docker broke —
the same argument that made `Common.Web.Tests` disable parallelisation
assembly-wide rather than trust a shared collection — and a category is
PR-22's named deliverable ("Testcontainers categories"), with PR-25 running
them as their own CI stage. ADR-010 already made real infrastructure
non-optional. Without a daemon the eight tests in `DatabaseSmokeTests` fail on
`Failed to connect to Docker endpoint`, which is a true statement about the
machine and not a defect in the branch.

Adding a migration needs the pinned tool and a startup project:

```bash
dotnet ef migrations add <Name> \
    --project src/Services/Catalog/Catalog.Infrastructure \
    --startup-project src/Services/Catalog/Catalog.Migrator \
    --output-dir Persistence/Migrations
```

Central package management means versions live in `Directory.Packages.props`
with **exact** pins — never add a `Version=` attribute to a `PackageReference`.

`Directory.Build.props` carries the analyser policy of **ADR-019**:
`TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`, `AnalysisLevel
latest-Recommended`, and no StyleCop. A warning stops the build, so a change
that provokes one is not done until the warning is gone — and `#pragma` is not
the way out. A genuinely warranted suppression goes in `Directory.Build.props`
with a comment.

Three live there, each arguing its case in the file:

- **CA1707** off for projects whose name ends `Tests`. §12's test names are
  sentences written with underscores, and the rule forbids them. Scoped by
  name — `EndsWith('Tests')`, not `'.Tests'`, because `Platform.IntegrationTests`
  ends with the word and not the dotted suffix.
- **CA1716** off repo-wide. It flags a type whose name is a reserved word in
  another .NET language, and `Error` (§10.5) is one in VB. Nothing here is a
  published library — §4.3 lets exactly one assembly cross a service boundary —
  so the scenario the rule protects does not exist.
- **CA1711** off repo-wide, added by PR-04. It bans a reserved suffix on a type
  name and fires on `NextDelegate` (§6.2), where the suffix is not incorrect at
  all — the type is a delegate. Admitted on CA1716's terms, and the two are
  the same argument: both protect a consumer of a published library from a name
  they cannot change, and there is no such consumer. It costs the rule
  everywhere, so a later `OrderCollection` that is not a collection stops being
  caught.

The first two were found by PR-02, the third by PR-04. **A fourth is a decision
about the policy, not about the file in front of you.** Argue it in the comment
or do not add it — and prefer changing the code: PR-04 met CA1848 by moving
`LoggingBehavior` onto `LoggerMessage.Define` rather than waiving a rule whose
whole subject is the hot path that behaviour sits on. PR-08 met three more the
same way and added no fourth suppression: **CA1725** on an override whose
parameter it had renamed (§7.2's `ConfigureConventions` sample said `builder`
and the base declares `configurationBuilder` — the chapter was amended, since
a reader consulting the framework's documentation is reading about the base
name), **CA1863 and CA1305** on a `string.Format`-built SQL predicate, which
became an EF `{0}` parameter and stopped being an injection shape at the same
time, and **NU1903** on a transitive of the EF design-time package, pinned
forward like `Microsoft.OpenApi` before it.

`EnforceCodeStyleInBuild` only bites on rules set to `warning` or above, and
exactly three are: **IDE0055** (formatting), **IDE0065** (`using` placement) and
**IDE0161** (file-scoped namespaces). The rest of `.editorconfig` is documented
and unenforced on purpose — the four `var` carve-outs above are the reason, and
raising a rule whose exception lives in prose would fail builds that are
correct. Verified end to end: each of the three fails a build, and a compliant
file is clean.

`Common.Web.Tests` also carries an `AssemblyInfo.cs` that disables xUnit's
parallelisation for the project, and the reason belongs beside the analyser
policy above because it is the same kind of decision: a rule scoped to the
whole assembly rather than argued file by file. OpenTelemetry's ASP.NET Core
instrumentation subscribes a **process-wide** `DiagnosticListener` the moment
any test builds a host through `AddObservability`, and while that listener is
live, ASP.NET Core's hosting layer starts a server `Activity` for every
request in the process — including one an unrelated test class sends through
its own `TestServer`. That is exactly the ambient state §10.4's
correlation-ID fallback test sets `Activity.Current` to null to rule out, and
a host still alive from another class handed it one anyway, failing the test
about half the time. Serialising the assembly makes the ordering
deterministic, and the parallelism given up is worth very little: the suite
is 59 tests running in about a second. A shared xUnit collection was rejected
for failing open: the next class that builds an observability host and
forgets to join the collection would silently reintroduce the flake, where
the assembly-wide attribute leaves nothing to forget.

## The one rule that matters

**The blueprint must not contradict itself.** It is ~10,500 lines that describe
one coherent system; the failure mode is a statement in §9 that quietly
disagrees with §6, or an appendix that lists a package no chapter uses. Most of
the work done in this repo has been finding and closing those gaps.

So: when you change any load-bearing claim — a timeout, a retry count, a type
name, a registration order, an endpoint path, a package version — **grep the
whole blueprint for every other mention of it** and reconcile them all in the
same change. Fixing one site and leaving three is worse than not touching it,
because it converts a consistent error into an inconsistent one.

**Once code lands, this rule spans both.** The blueprint and the solution are
one artefact with two representations, and they drift the moment someone changes
a retry count in `Program.cs` and nowhere else. From then on:

- A code change that contradicts a chapter is not done until the chapter is
  amended in the same PR, or the code is changed to match. Pick one, in the PR —
  never leave the disagreement for later.
- A blueprint change that the code already implements differently is a bug
  report against one of them. Say which, and say why.
- `appendix-d-type-inventory.md` and `appendix-b-licences.md` are the two that
  rot fastest: a type renamed in code or a package added to
  `Directory.Packages.props` has to reach the appendix in the same change.
- Where the blueprint is genuinely wrong, fix the blueprint. It is a
  specification, not a historical record — but ADRs are the exception and are
  superseded, never rewritten.

Run `/validate-blueprint` after any substantive edit.

## Prose conventions

- **Wrap prose at 80 columns.** Tables, links and code blocks may exceed it.
- **British spelling in prose** — `behaviour`, `serialisation`, `licence`,
  `normalise`, `organised`. **Identifiers keep their real spelling**:
  `IPipelineBehavior`, `AddAuthorization`, `[Authorize]`. Never "correct" a type
  name into British spelling, and never Americanise the surrounding prose to
  match a type name.
- **Chapter headings are numbered**: `# 9. Messaging`, `## 9.1 Integration
  events`. Third-level headings are unnumbered prose titles
  (`### Handler contracts`). Appendices use `# Appendix A — <title>` and
  `## ADR-001 — <title>`.
- **Cross-references use the section sign**: `§9.3`, and link the first mention
  in a passage — `[§9.3](09-messaging.md)`. Within a chapter, `(§6.5)` bare is
  fine. Cite the section that actually states the claim; a reference to a
  section that only mentions the topic is a defect.
- **Callouts are blockquotes whose opening sentence is bold**, no emoji, no
  admonition syntax. Of the 68 in the blueprint, two forms are named and
  recurring — `**Trap — …**` (9) for a mistake worth naming, and
  `**Decision — …**` (10), which always points at the ADR that records it:

  ```markdown
  > **Trap — projecting everything by default.** Each projection is a second
  > copy of the truth, with its own bugs and its own rebuild procedure.

  > **Decision — no mediator library.** See [ADR-004](appendix-a-adrs.md#adr-004--no-mediator-library).
  ```

  The other 49 are a bold assertion followed by its argument —
  `> **Unregistered, this fails silently and completely.** …`. That is the
  default; reach for `Trap` or `Decision` only when the callout genuinely is
  one. `**Decision.** / **Why.** / **Consequences.**` are the ADR body form
  (see *Working in this repo* below), not callouts.
- **Em dashes** (`—`) for asides, **en dashes** (`–`) for ranges. Both are
  literal Unicode, not `--`.
- **Every chapter ends with a rule and a nav footer**, in this exact shape:

  ```markdown
  ---

  [← §5 Tactical DDD](05-tactical-ddd.md) · [Index](README.md) · [§7 Persistence →](07-persistence.md)
  ```

  Separator is ` · ` (U+00B7). First chapter omits the `←` link, last appendix
  omits the `→` link. One `---` only — a doubled rule before the footer is a
  regression that has been fixed before.
- **Tables carry the summary data**; prose carries the argument. A two-column
  borderless table (`| | |`) is the established form for metadata blocks.

## C# style — samples now, source later

**One dialect, both phases.** The rules below govern the fenced samples today
and the compiled solution when it arrives, so a sample and its implementation
read identically.

The repo `.editorconfig` is not a documentation convenience — it is the file
PR-01 ships. Change it deliberately, and reconcile any change with the samples
already written against it.

**Follow:**

- Four-space indent, spaces not tabs. CRLF line endings. Newline at end of file.
- `using` directives **outside** the namespace; no blank lines between
  consecutive usings. This one binds source only — the samples carry no `using`
  directives at all, because they are excerpts rather than compilable units
  (Appendix D). Do not "complete" a sample by adding them.
- **No unused `using` directives**, and a file that stops needing one drops it
  in the change that stopped needing it. A stale using is a claim that the file
  depends on something it does not, which is the same class of untruth as an
  unused project reference — and the reader who trusts it looks in the wrong
  assembly first. Two of the four found in the last sweep were left behind by a
  refactor that moved the only call.

  **Nothing catches this, and the reason is a trap worth knowing.** IDE0005 is
  the rule, and it is **not reported by the build at all** unless
  `GenerateDocumentationFile` is on — so `dotnet build` is silent on an unused
  using even with `TreatWarningsAsErrors`, and so is
  `dotnet format style --diagnostics IDE0005`. Both were checked against a
  deliberately injected `using System.Text;` and neither said a word; a clean
  run here proves nothing. Turning it on costs a fourth entry in
  `Directory.Build.props`, because `GenerateDocumentationFile` also enables
  CS1591 and this repository has **62** public members with no XML comment.
  That is a decision about the policy, so until someone argues it the rule is
  carried by review, like the `[` placement rule and the `new()` rule above.
  The IDE does flag it — the greyed-out using is the only live signal there is.
- Pascal case for types, properties, methods and events; `I` prefix on
  interfaces; namespace matches folder.
- **A blank line always follows the namespace declaration.** `namespace X;` is a
  statement about the whole file, not the first line of the type below it, and
  the blank line is what says so:

  ```csharp
  namespace Common.Application;

  public interface IDispatcher
  ```

  **IDE0055 enforces this and ADR-019 makes it an error**, so it is in the same
  class as `{` placement rather than the review-carried `[` rule — write the
  type straight under the semicolon and the build fails on
  `ProbeStyle.cs(2,1): error IDE0055: Fix formatting`. Checked against the
  compiler rather than assumed, the same way the alignment rule below was. Every
  file in the repo and every sample in the blueprint already reads this way; a
  new one that does not will not compile.
- **A single statement may omit braces; two or more always take them.** The
  statement goes on the following line — never beside the condition — and it may
  wrap:

  ```csharp
  if (amount < 0)
      throw new DomainException("Money cannot be negative.");

  foreach (IDomainEvent domainEvent in events.Where(projections.HasHandler))
      await publisher.StageAsync(domainEvent, OutboxLane.Local, ct);
  ```

  This holds across all 53 braceless bodies in the blueprint — 15 guard-clause
  `throw`s, 13 `return`s, and 25 single calls and `continue`s.
  `csharp_preserve_single_line_statements = false` keeps a format run from
  pulling any of them back up onto the condition's line. The one exception is a
  **wrapped** condition, which takes braces — see the multi-line condition rule
  below.

- **Explicit types for locals**, except where the right-hand side names the
  type.
  `var order = new Order(...)` and `var id = ProductId.New()` are fine —
  restating the type there is noise. Everything else is explicit:

  ```csharp
  IReadOnlyDictionary<ProductId, Money> priceList =
      await prices.GetAsync(productIds, command.Currency, ct);
  ProductId[] missing = [.. productIds.Where(id => !priceList.ContainsKey(id))];
  ```

  A reader of a fenced code block has no hover and no go-to-definition, and this
  blueprint's job is to teach types and contracts. The same rule governs the
  solution, so a sample and its implementation read identically. Code blocks may
  run past 80 columns and hundreds of lines do, so length alone is never a
  reason to hide a type — if a declaration turns unwieldy, split the expression.

  Four cases keep `var`, and only these four:

  | | Example |
  |---|---|
  | The RHS names the type | `var order = Order.Place(…)`, `var id = Guid.CreateVersion7()` |
  | Anonymous types | `var args = new { OrderId = orderId.Value };` |
  | Tuple deconstruction | `foreach (var (product, qty, price) in items)` |
  | Fluent resource DSLs | The whole Aspire AppHost block in §14.2 — eleven of its thirteen locals are an `IResourceBuilder<T>` whose name only repeats what the `Add*` call already said. Explicit types are possible there and read worse; keep the block uniform rather than typing part of it |
- **Target-typed `new()` only where the type is already named beside it.** This
  is the `var` rule applied to the right-hand side, and it cuts the same way: a
  reader with no hover must be able to see what is being constructed. Where the
  declaration names the type, `new()` repeats nothing and is preferred —
  `.editorconfig` asks for it
  (`csharp_style_implicit_object_creation_when_type_is_apparent = true`):

  ```csharp
  ServiceCollection services = new();
  private static readonly ConcurrentDictionary<Type, Invoker> Cache = new();
  public static Result Success() => new(null);
  ```

  Where nothing beside it names the type, spell the type out. The test is
  whether the type is visible in the declaration the expression belongs to, so
  the positions that hide it are **an argument**, **a collection expression in
  argument position**, and **the target of an indexer or property assignment**:

  ```csharp
  // Wrong — the reader cannot see what is being constructed in any of these.
  .MapHealthChecks("/health/live", new() { Predicate = _ => false })
  .AddAttributes([new("deployment.environment", builder.Environment.EnvironmentName)])
  scrubbed[i] = new(attribute.Key, "[redacted]");

  // Right.
  .MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
  .AddAttributes([new KeyValuePair<string, object>("deployment.environment", builder.Environment.EnvironmentName)])
  scrubbed[i] = new KeyValuePair<string, object?>(attribute.Key, "[redacted]");
  ```

  A collection expression assigned to a typed declaration is **not** one of
  them — the element type is right there on the left, so the elements stay
  bare. Both forms appear in `SensitiveDataRedactorTests`, which is the file to
  read if the distinction ever looks arbitrary:

  ```csharp
  KeyValuePair<string, object?>[] state =
  [
      new("NewPassword", "a"),      // fine — the array's type is on the left
      new("card_number", "b")
  ];
  ```

  **No analyser reaches the banned half**, and it is worth knowing why. IDE0090
  fires only when the type *is* apparent, so it polices the first block and has
  nothing to say about the second — turning it off would attack the form this
  rule wants to keep. Like the `[` placement rule, this half is carried by
  review and by this file alone.

  **Where naming the type breaks the 120-column budget, name it in a local —
  do not fall back to `new()`.** Spelling the type inside `AddAttributes`' own
  argument runs that line to 130 columns, and the two rules resolve in one
  move rather than trading off: a local declaration carries the type, so the
  `new` beside it needs none and both lines fit.

  ```csharp
  KeyValuePair<string, object> environment =
      new("deployment.environment", builder.Environment.EnvironmentName);
  // ...
      .AddAttributes([environment]))
  ```
- Binary operators spaced. Where a wrapped one goes is the operator-placement
  rule below, which states it once.
- **A list is on one line, or one element per line. Never a ragged middle.**
  "List" means anything comma-separated inside brackets — parameters,
  arguments, collection expressions, initialisers, tuple members. The budget is
  **120 columns** (`max_line_length` in `.editorconfig`); within it the list
  stays on one line, and past it every element gets its own:

  ```csharp
  string[] forbidden =
  [
      "Microsoft.EntityFrameworkCore",
      "MassTransit",
      "StackExchange.Redis",
      "Microsoft.AspNetCore"
  ];
  ```

  **`[` and `{` each take a line of their own**, at the column of the construct
  they open, and their closers do too. **`(` is the single exception**: it ends
  the line it opens, and `)` trails the last element — `);`, not a line of its
  own:

  ```csharp
  _deliveryLag = meter.CreateHistogram<double>(
      "messaging.delivery.lag",
      unit: "s",
      description: "OccurredAt to consumer start.");
  ```

  The exception is not arbitrary, and it is symmetric at both ends. A
  parenthesised argument list is *part of* the invocation — it belongs to the
  call syntactically, so it hugs it on the way in and on the way out. A braced
  or bracketed body is a *container* of elements, and giving the container its
  own opening and closing line puts its extent in a column the eye can scan
  without reading anything between.

  ```csharp
  options.DefaultEntryOptions = new HybridCacheEntryOptions
  {
      Expiration = TimeSpan.FromMinutes(10),            // L2, Redis
      LocalCacheExpiration = TimeSpan.FromMinutes(1)    // L1, in-process
  };
  ```

  **The two halves are enforced very differently, and it is worth knowing
  which is which.** `{` is not a review rule at all: the C# default for
  `csharp_new_line_before_open_brace` is `all`, IDE0055 reports a trailing one
  as a formatting violation, and ADR-019 turns that into a failed build. Write
  `new Options {` and the build fails — on an analyser diagnostic rather than
  a language error, which is the whole reason `.editorconfig` gets to decide
  it.
  `[` has no such backing: Roslyn has no opinion on bracket placement,
  `dotnet format` neither introduces nor removes the break, and IDE0055 is
  silent. That half is carried by review and by this file alone.

  In argument position the two rules compose rather than fight: `(` ends its
  line, arguments go one per line at + 4, and a collection expression among
  them opens at its own argument's column.

  ```csharp
  actual.ShouldBe(
      [
          typeof(LoggingBehavior<,>),
          typeof(ValidationBehavior<,>)
      ],
      "queries get logging and validation only — §6.3");
  ```

  A collection expression that fits stays on one line, `[` included — the
  budget governs it exactly as it governs any other list, and
  `IDomainEvent[] events = [.. aggregates.SelectMany(a => a.DomainEvents)];`
  is one line rather than five.

  Continuations indent **four**, never to a bracket column. A list too wide for
  one line was previously wrapped under its opening bracket
  (`string[] forbidden = ["…",` / 26 spaces / `"…"];`); that form is gone and a
  surviving one is a leftover. Two things keep it from being mechanical: a line
  comment on an element forces the broken form regardless of width, and the
  four `var` cases above still apply inside the elements.
- **A broken fluent chain puts every call on its own line**, at head + 4, never
  aligned under the receiver's dot. That includes the *first* call: if the chain
  breaks at all, nothing stays on the head's line. The head is whatever contains
  no invocation, so it is often a bare identifier sitting alone:

  ```csharp
  builder.Services
      .AddReverseProxy()
      .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

  builder
      .Property(o => o.Id)
      .HasConversion(id => id.Value, value => new OrderId(value))
      .ValueGeneratedNever();
  ```

  Unlike a list, a chain is **never joined back up to fit** — it is broken for
  reading order, not for width, and the example above is 100 columns broken
  across three lines on purpose. One exception: a short qualifier and its
  subject count as one call (`.That().ResideInNamespaceContaining(…)`,
  `.ShouldNot().HaveDependencyOn(…)` — NetArchTest's idiom reads as pairs).

  **The head is the line the chain starts on, which is not always the statement's
  first line.** When a declaration's initialiser wraps, that wrapped line is the
  head and the chain indents four past *it* — eight from the declaration:

  ```csharp
  IEnumerable<(Type Implementation, Type Service)> implementations =
      assemblies
          .SelectMany(a => a.GetTypes())
          .Where(t => t is { IsAbstract: false, IsInterface: false })
          // ... the chain continues; §6.2 carries it in full
  ```

  Measuring from the declaration instead would put `.SelectMany` level with the
  expression it is chained onto, and the chain would read as a sibling of the
  initialiser rather than as applied to it. A `})` closing a lambda mid-chain is
  a continuation, not a head — the calls after it keep the chain's indent.

  **A spread element is a head the same way.** `..` introduces the expression,
  so the chain hangs off the `.. x` line at + 4 — which lands eight from the
  declaration again, the `[` line having taken the first four:

  ```csharp
  ValidationFailure[] failures =
  [
      .. (await Task.WhenAll(validators.Select(v => v.ValidateAsync(context, ct))))
          .SelectMany(r => r.Errors)
          .Where(f => f is not null)
  ];
  ```

  **"Contains no invocation" means no *dotted* call, and a receiver never
  outranks that.** `Types`, `app`, `_lines`, `from` and `Enumerable` all sit
  alone as heads, static classes and fields alike — `Types.InAssembly(a)` and
  `Enumerable.Range(0, n)` break after the receiver just as `builder.Services`
  does. What stays on the head is whatever has nothing to strand in front of
  it: object creation (`new MsSqlBuilder()`), a call with no receiver
  (`When(OrderPlaced)`, `GetInvoker<TResult>(…)`, `BuildServices()`), and a
  parenthesised expression (`(await Task.WhenAll(…))`). Splitting those would
  leave a `new` or a bare `(` on a line of its own, which is the thing the rule
  is trying to avoid.
- **A lambda body that is itself a call goes on its own line**, at + 4, rather
  than trailing after the `=>`. A bare parameter re-mention is not a call and
  stays — `p => p` heading its own chain is the fluent-DSL idiom, and moving it
  down would strand a single letter on a line:

  ```csharp
  builder.Services
      .AddCors(o =>
          o.AddDefaultPolicy(p => p
              .WithOrigins(builder.Configuration.GetRequiredSection("Cors:Origins").Get<string[]>()!)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()));
  ```

  So `o => o.AddDefaultPolicy(…)` breaks and `p => p` does not, in the same
  statement. Each nesting level is then one indent, and the reader can see which
  builder each call belongs to. This applies only when the construct is already
  broken across lines; inside 120 columns it stays on one.

  An **expression-bodied member** is not a lambda for this purpose:
  `public Money Total => _lines.Aggregate(…)` keeps its body on the `=>` line,
  because there is no nesting for the break to clarify. When such a member's
  body *does* need its own line, `=>` still trails the signature — it is an
  operator, and the rule above applies to it too:

  ```csharp
  public Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken ct = default) =>
      GetInvoker<TResult>(command.GetType(), typeof(CommandInvoker<,>))
          .InvokeAsync(services, command, ct);
  ```

  The body sits at the **declaration's** indent + 4, which is not necessarily
  the `=>` line's + 4: a signature that itself wraps puts its parameters at + 4
  already, and measuring from there would indent the body to match them. Join
  the signature where it fits inside 120 and the question does not arise.
- **Break at the outermost bracket, never a nested one.** When a call's argument
  is itself a call, it is the outer parenthesis that opens the line — reaching
  past it to break the inner one leaves the outer call glued to its argument and
  reads as though the inner list were the outer's:

  ```csharp
  // Wrong — the break is inside OrderPlacedDomainEvent, and Raise( is stranded.
  order.Raise(new OrderPlacedDomainEvent(
      order.Id, customerId, order.Total, order.SnapshotLines(), now));

  // Right.
  order.Raise(
      new OrderPlacedDomainEvent(order.Id, customerId, order.Total, order.SnapshotLines(), now));
  ```

  The argument moves to the next line **whole**; break it further only if it
  still does not fit, and then one element per line as usual. This is the same
  principle as the lambda rule above — a nested construct starts its own line —
  and it is why `.Send(queue, ctx => new CancelOrder(…))` breaks after `ctx =>`
  rather than after `CancelOrder(`.
- **A multi-line condition takes trailing operators, four-space continuations,
  and braces on its body.** The braces are what make four safe: without them the
  last `&&` line and the body sit in the same column and the reader cannot see
  where the condition stops.

  ```csharp
  if (currentUser.IsAuthenticated &&
      order.CustomerId.Value != currentUser.Id &&
      !currentUser.HasPermission("orders:admin"))
  {
      return Result.Failure(OrderErrors.NotFound);
  }
  ```

  This is the **one exception to the braces rule above** — a single statement may
  omit braces, unless the condition it hangs off is wrapped. Prefer not to wrap
  at all; joining is the better fix, and it has already been applied everywhere
  it fits. The block above is the corpus: one wrapped header, in §11.4, kept
  because the three clauses of an ownership check do not join inside 120. A
  second one appearing is a signal to join, not a precedent.
- **Operators go at the end of the line they continue from**, not the start of
  the next (`dotnet_style_operator_placement_when_wrapping = end_of_line`). Each
  line then ends by announcing that more is coming. This holds for `&&`, `||`,
  `??` and `+`, in conditions and in expressions alike; a leading `&&` or `??` is
  a leftover from the previous style. It governs wrapped lambda predicates as
  much as `if` headers.
- A base-type list is the one continuation that is **not** covered by any of the
  above: it has no bracket to hang off and no operator, and it is already one
  entry per line. Leave it aligned under the `:`.
- Prefer collection expressions, `is null` over `ReferenceEquals`, null
  propagation, compound assignment, simplified interpolation, primary
  constructors.
- **Materialise with a spread, not a terminal `.ToArray()` or `.ToList()`.**
  A sequence being fixed into an array or list target is written
  `[.. sequence]` — one space after the `..`, as `[.. record.Attributes]` and
  `[.. assemblies]` already had it. There are no `.ToArray()` or `.ToList()`
  calls left in the corpus, and a new one is a site this rule missed:

  ```csharp
  ProductId[] missing = [.. productIds.Where(id => !priceList.ContainsKey(id))];
  ```

  The reason is that the spread states the target and the terminal call states
  a conversion, and only one of those is what the line is for. `ProductId[]` on
  the left already fixes the type; `.ToArray()` on the right repeats it in a
  second vocabulary, and repeats it *last*, so the shape of the result is the
  final thing a reader learns rather than the first.

  Two consequences worth stating, because both changed real sites in this
  sweep. Dropping `.ToArray()` often leaves a **single** call, and a single
  call is not a broken chain — join it (`[.. e.Lines.Select(…)]`, not `..
  e.Lines` over two lines). And a spread frequently brings the whole statement
  back under 120, in which case the one-line rule applies and the `[` does not
  get its own line after all.
- **One space before `=`, `=>` and `{` — never a column of them.** Padding a
  token out to line up with the one above it fails the build: IDE0055 reports
  it as a formatting violation and ADR-019 makes that an error. This was found
  the only way it could be, by compiling a sample that had been written the
  other way since before there was a compiler in this repo:

  ```csharp
  // Fails the build. Every line but the longest carries the diagnostic.
  public required Guid MessageId       { get; init; }
  public required decimal TotalAmount  { get; init; }

  // Correct.
  public required Guid MessageId { get; init; }
  public required decimal TotalAmount { get; init; }
  ```

  **A trailing `//` comment is the carve-out, and it is a real one.** IDE0055
  does not govern the whitespace in front of a comment at all, so a comment may
  sit in whatever column the block reads best in — the sweep that removed 133
  alignments left every comment column exactly where it was:

  ```csharp
  options.Retry.MaxRetryAttempts = 2;            // 3 attempts in total
  options.Retry.BackoffType = DelayBackoffType.Exponential;
  ```

  The carve-out stops at the end of the line, and PR-07 found the boundary by
  compiling it. A trailing comment too long for one line cannot wrap into a
  second `//` line aligned under the first: a line that *starts* with `//` is
  a whole-line comment, its leading whitespace is indentation, and indentation
  IDE0055 does govern — the continuation fails the build at the statement's
  own column ±0. §4.2's `Program.cs` sample carried exactly that form on
  `UseExceptionHandler` and was amended in the same change; a trailing comment
  that will not fit is shortened, or moved above the statement whole.

  `dotnet format` agrees with both halves — it collapses the code padding and
  leaves the comment column untouched — so a format run neither introduces this
  nor undoes it, and nothing has to be pinned to keep it idempotent. Checked
  against the tool rather than assumed.

  **Two places the analyser does not reach, and they are opposites.** Padding
  between a type and its identifier — `private readonly Counter<long>   _placed;`,
  `long   start = …` — is *not* reported, and was swept anyway: one dialect
  beats a rule that stops halfway, and that half is carried by review, like the
  `[` rule above. **SQL is the other way round and keeps its alignment.** It
  lives inside raw string literals, which no analyser and no formatter reads,
  and the SQL section below argues its columns on their own terms rather than
  by parity with C#.

- **No `#pragma` suppressions** — there are none in the corpus and a sample that
  needs one is a sample whose design is wrong. If a suppression is genuinely
  warranted in source, it belongs in `Directory.Build.props` with a comment
  saying why, never inline.
- **No real credentials** — no production connection string, key or certificate
  path, in a sample or in source. Bind configuration or read the environment;
  deployed secrets come from a vault (§15.4).

  The deliberate exception is local development, and it is not a defect to be
  tidied away: §14.1's Compose file carries
  `${SQL_PASSWORD:-Local_Dev_Pa55w0rd!}` and
  `${BFF_CLIENT_SECRET:-local-dev-secret}`, and documents Keycloak as
  `admin/admin` and RabbitMQ as `guest/guest`. Those defaults are what make
  `docker compose up` work with no prior setup; the environment variable in
  front of each is the seam that keeps them out of anything deployed.

**Settled choices — do not "fix" them:**

| | |
|---|---|
| Namespaces | File-scoped (`namespace X;`), never block-scoped |
| Extension declarations | C# 14 `extension(T receiver)` blocks where a class groups several extensions on one receiver — `Common.Application.DependencyInjection` is the worked example. **The corpus is currently split**: `Common.Web`'s six extension classes still use the classic `this`-parameter form. Four extend a receiver nothing else does — `IApplicationBuilder`, `IServiceCollection`, `IEndpointRouteBuilder`, `Result` — but `ObservabilityExtensions` and `CommonWebDefaultsExtensions` **both** extend `IHostApplicationBuilder` and could therefore be grouped. Whether to group that pair is open and deliberately unsettled: they are separate files because one composes the other, and merging them would put a caller-facing entry point in the same block as a piece it calls. Converting anything here is a decision about the whole corpus, not about the file in front of you |
| Expression-bodied members | Used for one-line members, not for constructors |
| Braces | Optional for a single statement, required for two or more |
| Target framework | .NET 10 (LTS), C# 14 |

Each of these is a house style a reviewer might otherwise read as an oversight
and "correct". They are consistent across all ~135 existing C# blocks, and the
solution will be written the same way. Changing one is a decision about the
whole corpus, not about the file in front of you.

**Fence languages in use:** `csharp`, `sql`, `yaml`, `bash`, `json`, `mermaid`,
`dockerfile`, `xml`, and bare ``` for trees and console output. Always tag a
fence that contains a real language.

## SQL style

Most of the SQL lives inside C# raw string literals rather than `sql` fences, so
"the statement's own left margin" below means the literal's indent, not column
zero.

**Clause keywords start their own line at that margin, one space before what
follows; continuations indent four.** Continuations are the connectors that
extend a clause — `AND`, `OR`, and a `MERGE`'s `ON`. Each predicate gets its own
line: a chain packed onto one line hides the shape of the condition, which in
this blueprint is usually the point being made.

**An `AND` extending an `ON` aligns with the `ON`**, rather than indenting
another four. `ON` is itself a continuation — of `MERGE … USING` or of a
`JOIN` — so its predicates sit at the level it already occupies, and a second
indent would imply a nesting that is not there:

```sql
MERGE ordering.ProductPrices AS target
USING (SELECT ProductId = @ProductId, Currency = @Currency) AS source
    ON target.ProductId = source.ProductId
    AND target.Currency = source.Currency
```

This is the one place `AND` does not indent past the keyword it extends; under
`WHERE`, which is a clause keyword at the margin, it still does.

```sql
UPDATE ordering.OrderSummaries
SET FulfilmentCounted = 1
OUTPUT inserted.PlacedAt, inserted.ConfirmedAt
WHERE OrderId = @OrderId
    AND PlacedAt IS NOT NULL
    AND ConfirmedAt IS NOT NULL
    AND FulfilmentCounted = 0;
```

One space means one, `WHERE` included — it used to be written `WHERE  ` and no
longer is.

`UPDATE <table>` and `SET` are separate lines. The exception is `MERGE`'s
`UPDATE SET`, which names no table and stays one token.

**A `SET` that breaks keeps nothing on the keyword's line.** Like a fluent
chain, if the assignment list does not fit it goes below whole — the first
assignment does not stay up beside `SET` with the rest hanging under it:

```sql
UPDATE SET
    CustomerId  = @CustomerId,
    TotalAmount = @Total,
    Currency    = @Currency,
    LineCount   = @LineCount,
```

The `=` signs line up in a column here, and that alignment is deliberate. It
used to be justified by parity with the C# initialisers; that argument is gone,
because IDE0055 forbids the C# form and the C# section above now says so. SQL
keeps the column on its own merits: a statement inside a raw string literal is
invisible to every analyser and formatter in the toolchain, so nothing will
fight it, and one assignment per line with the names in a column is what makes
`SET` read as the row shape it produces rather than as a wrapped list.

**A SQL list obeys the same rule as a C# one: one line, or one element per
line, never a ragged middle.** That covers the column list after `INSERT`, the
values after `VALUES`, the columns after `OUTPUT` and `GROUP BY`, and a
function's arguments. The budget is the same 120 columns, measured from the
literal's margin:

```sql
INSERT (OrderId, CustomerId, Status, TotalAmount, Currency, LineCount, Products, PlacedAt, UpdatedAt)
VALUES (@OrderId, @CustomerId, @Status, @Total, @Currency, @LineCount, @Products, @PlacedAt, @UpdatedAt)
```

Past the budget the `(` ends its line and each element takes one, indented four
— **not** aligned under the first argument. `DATEADD(second, …)` wrapping its
arguments into a column under `second` is the keyword river again, one scope in.

The single exception is a **DDL body**, where alignment is a table rather than a
wrapped list: `CREATE TABLE` and `CREATE INDEX` keep their aligned type and
constraint columns, because there the columns carry the meaning.

A parenthesised sub-expression short enough for one line stays on one line;
`AND (LockedUntil IS NULL OR LockedUntil < SYSDATETIMEOFFSET())` is one
predicate, not two. A parenthesised group that *does* break indents its `OR`s a
further four, so nesting depth is visible:

```sql
WHERE o.CustomerId = @CustomerId
    AND (@AfterPlacedAt IS NULL
        OR o.PlacedAt < @AfterPlacedAt
        OR (o.PlacedAt = @AfterPlacedAt AND o.Id < @AfterId))
```

**Column aliases are assignments, not `AS`** — `Total = o.TotalAmount`, never
`o.TotalAmount AS Total`. The name being defined then starts the line, so a
projection reads as the row shape it produces, and the `=` column lines up the
way `SET`'s does. This is the SELECT list only: `MERGE … AS target`,
`USING (…) AS source`, `WITH claimable AS (` and `CAST(x AS varchar(10))` are
required syntax and keep `AS`.

**`INNER JOIN` is spelled in full, and its `ON` gets its own line** at + 4. The
join condition is a predicate and belongs where predicates go, not trailing off
the end of a table name:

```sql
FROM ordering.Orders o
INNER JOIN ordering.OrderLines l
    ON l.OrderId = o.Id
```

An alias follows its table after one space. Padding table names into a column
(`FROM ordering.Orders      o`) is the old keyword river in another costume.

This replaced a right-aligned keyword river (`FROM   `, `WHERE  `, `  AND  `,
every argument at column 7). If you find one, it is a leftover — convert it.

## Working in this repo

- **Read before you edit.** Chapters run to 2,000 lines; the claim you are about
  to change is usually stated more than once.
- **Changing the chapter set** means updating four places: the file itself, the
  chapter table in `docs/backend-architecture/README.md`, the nav footers of
  both neighbours, and any `§n` cross-references that shift.
- **New ADRs** append to `appendix-a-adrs.md` with the next free number
  (currently ADR-020) and keep the
  `**Decision.** / **Why.** / **Consequences.**` three-part form. ADRs are
  never renumbered; supersede rather than rewrite.
- **New dependencies** — whether mentioned in a chapter or added to
  `Directory.Packages.props` — must reach the licence register in
  `appendix-b-licences.md` with their licence and role. Versions live in
  `Directory.Packages.props`, not the register; state one there only where the
  version *is* the point, as with MassTransit 8.x. A package in a sample or a
  csproj but not the register is a known drift class — NetArchTest and Aspire
  were both missing — and PR-01 wires a licence allow-list gate into CI that
  will fail on it.
- **Commit messages** are semantic and present-tense: `docs:`, `feat(<scope>):`,
  `fix:`, `chore:` — the delivery plan in Appendix C already names each PR in
  this form, so use its title verbatim when you implement one.
- **Uncommitted work in the tree belongs in the PR being worked on.** When a
  change appears that nobody in the current task wrote — an edit made directly
  by the repo owner, most often — it is not stray churn to be reverted or left
  behind for someone else to notice. Commit it as part of the current PR, in
  its own commit, with a body that argues it like any other. **Never revert it
  to clean the tree**: that has happened once, and only a saved diff kept the
  work. If it genuinely does not belong in this PR, say so and ask — do not
  decide by deleting. The same reconciliation rule applies to it as to
  everything else, so a hand edit that contradicts a chapter takes the chapter
  with it in the same commit.
- `.remember/` is session state, not content. Never edit it as part of a change.

Once code is present, additionally:

- **TDD is the stated method** (§12), not a preference. Tests ship in the same
  PR as the code they cover — the convention starts at PR-02 and there is no
  PR in the plan that adds tests afterwards.
- **Follow the delivery plan's order.** Appendix C sequences 26 PRs with
  explicit dependencies, and the service order (Catalog → Ordering →
  Inventory and Payments → Shipping → Notifications) is deliberate. Building
  out of
  order is a design decision, not a shortcut — raise it rather than taking it.
- **The architecture tests are the enforcement mechanism**, not review.
  NetArchTest gates land at PR-07: domain isolation, Application ↛ EF Core,
  endpoints ↛ Infrastructure, Application and Domain ↛ MassTransit. If a
  change needs one of those gates relaxed, the gate is probably right and the
  design is probably wrong.
- **`Program.cs` in each `*.Api` is the only composition root** (§4.2). Wiring
  belongs in `AddXApplication()` / `AddXInfrastructure(config)`, not scattered.
- **`main` stays green.** Every PR in the plan is specified to leave it building
  and passing.

## Available commands

Content:

| | |
|---|---|
| `/validate-blueprint` | Multi-pass self-consistency audit across the blueprint and `docs/roadmap.md`; also code ↔ docs drift once `src/` exists |
| `/check-links` | Link, cross-reference and nav-footer integrity |
| `/new-chapter` | Scaffold a chapter and rewire its neighbours |
| `/new-adr` | Append an ADR in the established form |
| `/style-pass` | Apply one corrected code form corpus-wide, then record it in `CLAUDE.md` and `.editorconfig` |

Delivery:

| | |
|---|---|
| `/ship` | Run the three below in sequence — the first of them forking the PR's own worktree — resuming where a previous run stopped; once the PR is open, loop `/review-branch` (run by Grok) and `/review-grok` until two consecutive passes leave no `suggestions.md` — or until a Grok usage-limit skip hands over early, owing Grok a later re-entry — then loop a requested Copilot review and `/review-copilot` until one review lands with no new findings and no unresolved threads |
| `/branch` | Start a correctly named branch — **in a sibling worktree** the session moves into, from a clean `main`; in place when the tree is dirty (carrying the work off `main`) or the parent is not writable |
| `/commit` | Split the working tree into semantic commits with arguing bodies |
| `/pr` | Open a PR in the house body form |
| `/review-copilot` | Triage Copilot's PR comments — verify each before acting, then close every thread with a `done` or `rejected` marker and resolve it |
| `/review-grok` | Triage an external review into a resolution record |
| `/review-branch` | Review the branch (or working tree) against `main` for contradictions; writes `suggestions.md` and rechecks it on the next run |
| `/security-sweep` | Loop a defensive security audit up to seven rounds in a throwaway worktree, filing a GitHub issue per confirmed medium-or-above finding, until a round surfaces nothing new |

**Every PR gets its own worktree, and `/branch` is where it comes from.** A new
branch is cut into a sibling directory — `../ashamray-<slug>`, the shape
`ashamray-groklimit` is already on disk in — and the session moves into it, so
the whole of `/ship` runs there and this checkout stays on `main`. Outside the
repository tree is the load-bearing half rather than the naming: a worktree
under `.claude/worktrees/` would be untracked content inside the checkout,
which puts it in front of every `git status` the chain reads and inside
`grok-review.sh`'s clean-tree refusal. A sibling needs no `.gitignore` entry
because there is nothing to ignore.

**`/security-sweep`'s worktree is not this one, and neither is the other's
precedent.** That one is detached, lives under `mktemp -d`, carries no branch
and is removed at the end — and it refuses a sibling *by name*, because a
root-level or container layout has no writable parent, "both of which this repo
runs under". This one holds a branch that a PR, two review loops and a person
come back to, so it wants a stable named directory. The two commands are
answering different questions; making one match the other would break whichever
was changed. What the sweep's argument does carry across is the precondition:
where the parent is not writable there is no sibling to create, so `/branch`
names that case and branches in place rather than failing mid-`/ship`.

The review boundary is unchanged by any of this, and that was checked rather
than assumed: `git clone --no-hardlinks .` **out of** a linked worktree works
and the clone carries the branch, so `grok-review.sh` behaves the same there as
here. Note the two senses of the word, because they sit one paragraph apart —
the script refuses to *build* a worktree for the container, since a worktree's
`.git` is a file pointing back into the checkout and that path is the one the
container must not mount. Running *inside* one is a different thing entirely.

**A dirty `main` is the other case that branches in place**, and the reason is
that nothing can carry the work across: a worktree is a fresh checkout of
committed state, so uncommitted changes would need a stash or a patch — the
first is refused here because it hides work the user can see, the second
because it is lossy about untracked files and about line endings in a
repository that pins `*.cs` to CRLF and leaves everything else to the platform.
The command says which of the two happened rather than deciding quietly.
Committing the carried work first and re-entering `/branch` is the move if the
worktree is wanted, and it is the user's to make.

`/pr` pushes the branch itself, and `/ship` therefore runs past the open PR
and into two review loops. First Grok: `grok-review.sh` runs `/review-branch`
headlessly **in a container, over a throwaway clone** — the reviewer's grant
lands in a copy that is removed afterwards, and only `suggestions.md` crosses
back, never through a symlink. Isolation by construction rather than by a
post-run status check.

**The reviewer runs in a container**, `.claude/sandbox/Dockerfile`, and that is
the boundary — not the tool grant. The worktree only ever bounded *edits*: the
process still held this host's filesystem, network and credentials, and with
`gh` authenticated here it could reach remotes that `.claude/settings.json`
refuses to the session that spawned it. A deny list bounds the session, never
the subprocess. Copilot raised it against PR-15; the container closes it.

`bypassPermissions` is still passed and is no longer the risk it was, because
the blast radius is the box. **Flags were tried first and cannot do this**: a
`Bash(...)` rule does map onto grok's `run_terminal_command`, but
`/review-branch` reaches for commands outside any list narrow enough to be
worth having, and every miss is a *cancelled* review rather than a smaller one.

Three things are worth knowing before touching it. It **clones** rather than
building a worktree — a worktree's `.git` points back into the host checkout,
the one path the container must not mount. The grok version is **pinned** to
the host's, because an unpinned image took 1.0.0 against a 0.2.118 host and
resolved the same session to a new team with no credits. And the OAuth fallback
copies **three** files — `auth.json`, `agent_id`, `config.toml` — because with
only the first the container takes itself to be a first run and registers a new
team; `XAI_API_KEY` is preferred but preflighted, since a key set with no
credits on its team breaks a loop the session beside it would have run.

**Egress is the remaining residual** — the container reaches the network, and
confining it to `api.x.ai` needs an allow-list proxy Docker cannot supply on
its own. What keeps the loop honest either way is the verdict check rather than
the grant: a cancelled run exits non-zero and leaves `suggestions.md`
untouched, so a review that never happened cannot report as clean.

`/review-grok` triages whatever file it leaves, and the loop repeats until
two consecutive passes leave none. One exit skips rather than stops: the
helper's usage-limit preflight (exit 12) hands over to Copilot without a
clean Grok pass — reported
as skipped-on-limits, never as a verdict, and owing Grok a re-entry on a later
`/ship`. The split of ownership matters: Grok's half owns the
`suggestions.md` lifecycle — writing it, rechecking it, removing it when
clean — and the triage half never creates or deletes that file, only fixes
what it names. Then Copilot: a review is requested through
the REST reviewers endpoint — `Copilot` and
`copilot-pull-request-reviewer[bot]` both work as the request target — and
on every round after the first the reviewer is **removed and re-added**,
because a landed review leaves a stale-reviewer state in which a plain POST
returns 200 and registers nothing. The timeline's `review_requested` event,
never the status code, is what proves a request took. `/review-copilot`
triages what lands — suppressed comments included, which is where every real
finding against the loop's own machinery has arrived — and the loop repeats
until one review posts with no new findings and no unresolved threads. The
review's depth is
the account's Copilot settings, not a request parameter; the full tier, not
a lite one, is the one the loop wants. **`ship.md` owns the stopping
condition**, and the two loops no longer share one: Grok ends on **two
consecutive clean passes**, Copilot on the **first** clean round, and each
carries its own ceiling of twelve rounds per PR. Either loop also stops early
on the finding class that is the user's: `Needs a decision` from the Grok
triage, an open `Ask` thread from the Copilot one.

**The asymmetry is a decision with its cost on the record.** Two rather than
one was Grok's rule for the reason below, and it holds there: one clean pass is
not convergence, and requiring two also means the loop can never end on a round
whose findings were just fixed. Copilot gives that up for speed, so everything
now rests on the definition of clean — no inline comments, an empty suppressed
block, no unresolved threads — and on the ceiling behind it. Read the
suppressed block every round; under a one-round rule an unread one ends the
loop.

That state has a name and a durable home: **all-resolved**, which is the last
landed Copilot review carrying zero of all three, **pinned to the `commit` oid
it read**. A resumed `/ship` compares that oid against the pushed head and does
not re-request when they match — where the Grok half must re-enter, because
`suggestions.md` is absent both before the first review and after a clean one
and the two states are indistinguishable. The review says which commit it read;
a missing file never could.

The ceiling was three until PR-11, and the numbers are why it moved: the first
seven Copilot rounds went 10 → 4 → 3 → 1 → 1 → 3 → 1 with every finding
accepted, and rounds four to seven caught a documented-but-unenforced
constraint, an assertion that could not fail in one direction, and a fail-open
in a manifest check. Three would have shipped all three. Copilot's late rounds
surface findings in the **suppressed** block under a "generated no new
comments" heading, so a clean inline verdict is not convergence — and round
eight came back clean with every round after it finding more, which is the case
the Copilot loop's one-round rule now trades away and the Grok loop's two still
catches.
What `.claude/settings.json` still denies is the narrow set
that is a decision rather than a step: `--force`, `-f`, `--delete`, and any
push to `main`. A branch wanting one of those is raising a question, not
running a command. `gh pr create`'s own offer to push is not used either — it
is the same action by a route that skips the upstream check `/pr` makes
first, so it reaches the remote without reporting that it did.

This replaced a blanket `Bash(git push:*)` deny, under which `/pr` stopped and
asked the user to push. Worth knowing what that cost: the stop was the last
moment the work was still cheap to change, and **the checks in `/ship` step 2
now carry that weight** — with the two review loops behind the PR as the
second net. A check finding, a `Needs a decision` triage row and an open
`Ask` thread are what halt the chain.

**File permission rules take `Edit(...)`, never `Write(...)`.** `Edit(path)`
covers every file-editing tool, `Write` included; a `Write(path)` rule matches
nothing and Claude Code refuses to start until it is removed:

```
Permission deny rule (.claude\settings.json): Write(.remember/**) is not matched
by file permission checks — only Edit(path) rules are.
```

So `Edit(.remember/**)` and `Edit(./.remember/**)` are the whole of the
`.remember/` protection, and the absence of a `Write` twin beside them is
correct rather than a gap. This has now been "fixed" twice by adding the twin
back — once by an external reviewer reading the deny list as incomplete, once
by acting on that review — and both times it broke startup. A reviewer who has
not run the harness cannot see this; check a permission claim against the
harness before acting on it.

`Edit(.claude/scripts/**)` **and `Edit(.claude/sandbox/**)`** follow the same
two-spelling pattern and bind the agent's own tooling: the review loops grant
those helper scripts by name, and a session that could rewrite a helper before
invoking it would make the fixed endpoints a fiction. Changing either is
therefore a human's edit, made with the deny lifted. Like the push denies, it
is defence in depth — `Bash` redirection can still write a file — but it
removes the quiet path, which is the session's own editing tools acting on
reviewed grants.

**`sandbox/` is on that list for a reason worth keeping.** It arrived guarded
by nothing, and a review caught it: the Dockerfile is a *build input to the
security boundary*, so a session able to edit it can add an entrypoint that
reads the credentials the following `docker run` mounts in. Guarding the
runner and leaving its image editable protects the door and not the wall —
and the reasoning that put the image in `.claude/` at all was the reasoning
that should have guarded it, since a helper the loop invokes by name is
exactly what the Dockerfile had become.
