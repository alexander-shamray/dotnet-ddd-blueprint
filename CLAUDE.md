# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this repo is

`dotnet-ddd-blueprint` is a monorepo for an ASP.NET Core microservices platform
built with DDD, CQRS and TDD. **PR-01 through PR-17 have landed**, so the repo
is the blueprint under `docs/backend-architecture/`, the foundation that
blueprint specifies — SDK pin, central package management, the solution file,
CI and the licence gate — all five building blocks: `Common.Domain`,
`Common.Application`, `Common.Contracts`, `Common.Infrastructure` and
`Common.Web`, each with a test project since PR-15 gave Contracts
`Platform.IntegrationTests` — §14.1's Compose
infrastructure, with the CI smoke that proves
it — Catalog as the first real service: §4.1's five projects, §4.2's gates
live, PR-08's persistence, PR-09's transaction behaviour, and PR-10's
vertical slice with its containers — PR-11's scaffold under
`tools/new-service/` — PR-12's Redis helpers, §8 as code — PR-13's bus
registration, PR-14's outbox and PR-15's inbox, consumers and retention purge,
all three of §9's instalments — PR-16's security: §11.3's token
validation, §11.4's policies and port, and the Keycloak realm they validate
against — and PR-17's gateway, the second host in the solution and the first
project outside `src/Services`: §10.2's whole route file, §10.3's limiter,
§4.2's edge pipeline. The phase
section below carries the current state; this sentence only names the shape.

**The C# solution will land in this repo.** The blueprint is the specification
for it, and Appendix C sequences that code into 27 pull requests starting with
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
  appendix-a-adrs.md             ADR-001 .. ADR-020
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
Platform.slnx                    the thirty projects below
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
                                 .env.example, PR-16's realm export in place
                                 of the placeholder, the collector config, a
                                 ports README; PR-10 added the first application
                                 pair (catalog-migrator + catalog-api, port
                                 5102) and the infra-only override, PR-17 the
                                 gateway on 5000 — no migrator beside it, the
                                 edge owning no database — later
                                 blocks arrive with their services

src/BuildingBlocks/
  Common.Domain/                 Entity<TId>, AggregateRoot<TId>, IDomainEvent,
                                 IHasDomainEvents, IAggregateRoot,
                                 DomainException — no packages
  Common.Application/            Result, Result<T>, Error, ErrorType; the §6.2
                                 dispatcher and its three behaviours, plus
                                 RequestMetrics, PluggableInterfaces,
                                 InvariantViolationException, §6.5's
                                 CursorPage<T> and Cursor codec, and §7.5's
                                 whole flow since PR-14: IUnitOfWork (§6.3),
                                 IDbConnectionFactory (§6.5),
                                 IDomainEventCollector — the type that finally
                                 drew the Common.Domain edge —
                                 IIntegrationEventMapper,
                                 IIntegrationEventPublisher, OutboxLane,
                                 IProjectionHandler<T>, and the internal
                                 DomainEventDispatcher and ProjectionRegistry
                                 behind AddDomainEventDispatcher(). PR-15 added
                                 the consume side's two ports —
                                 IIntegrationEventHandler<T> and
                                 ICommandMessageMapper<TMessage,TCommand> —
                                 with ContractMappingException beside the
                                 second, which completes PluggableInterfaces.All
                                 at five. PR-16 added §11.4's ICurrentUser,
                                 which is a port like the rest and not a
                                 pluggable one: it is registered by name, in
                                 Common.Web, rather than scanned for
  Common.Contracts/              §4.3's one assembly that crosses a service
                                 boundary, and complete since PR-15:
                                 IIntegrationEvent (§9.1's envelope, the type
                                 Stage reads) over five versioned namespaces —
                                 Catalog, Ordering, Inventory, Payments and
                                 Shipping V1 — holding every name in §3.2's
                                 Publishes and Accepts columns. Commands carry
                                 no envelope, deliberately (§9.1). No packages
                                 and no project references, and
                                 both absences are the point — everything this
                                 referenced would travel into every service
  Common.Infrastructure/         §8 as code and §9's outbox, inbox and
                                 consumers, four folders:
                                 RedisConnections (keyed names, spelled like
                                 the configuration keys), AddRedisConnections
                                 (two keyed multiplexers read eagerly,
                                 HybridCache over the cache one via
                                 ConnectionMultiplexerFactory, InstanceName
                                 from RedisKeys, the Redis tracing
                                 instrumentation with both connections),
                                 RedisKeys, and IDistributedLockFactory —
                                 SET NX PX, mandatory TTL, token-checked
                                 release. Outbox/ is PR-14's: OutboxMessage
                                 and OutboxClaim (§9.4's two types for one
                                 table), MessageTypeMap over a mutable
                                 MessageTypeSource, OutboxJson taking its
                                 converters, OutboxTable, ProjectionInvoker
                                 and the OutboxDispatcher itself. Inbox/ is
                                 PR-15's: InboxMessage, InboxTable and the
                                 InboxFilter<T> that takes a DbContext rather
                                 than a service's derived type. Messaging/
                                 holds MessagingMetrics — complete at three
                                 instruments since PR-15 —
                                 IntegrationEventConsumer<T>,
                                 CommandConsumer<TMessage,TCommand>,
                                 RetentionPolicy and RetentionPurgeService,
                                 with SqlSchema at the root as the one
                                 identifier check both table types share. The
                                 project references it lacked until PR-14 are
                                 all here now — Application, Domain, Contracts
                                 — and MassTransit with them, plus PR-15's
                                 Microsoft.EntityFrameworkCore: the base
                                 package only, because the filter shares the
                                 handler's transaction and common code may
                                 name a DbContext but never a provider
  Common.Web/                    UseCorrelationId, AddCommonProblemDetails
                                 (which also registers §10.5's
                                 ValidationExceptionHandler — the 400 row's
                                 executor — and PR-18's
                                 ConcurrencyExceptionHandler, the 409's, which
                                 is why this block takes the EF Core base
                                 package and the gateway restores it for an
                                 exception it cannot raise),
                                 ToHttpResult, AddObservability,
                                 MapCommonHealthEndpoints, SensitiveDataRedactor,
                                 BuildInfo and the AddCommonWebDefaults that
                                 composes them — the only building block with a
                                 FrameworkReference (Catalog.Api's rides in
                                 with Sdk.Web). PR-16 added §11.3's
                                 AddJwtAuthentication with the Audience and
                                 AuthorityKey constants, the "authenticated"
                                 policy, PermissionClaim, RequirePermission and
                                 HttpContextCurrentUser — the last two here
                                 rather than per-service, because the
                                 FrameworkReference is what IHttpContextAccessor
                                 needs and this is the block that has one. Its
                                 reference to Common.Application was the only
                                 edge between building blocks until PR-14 drew
                                 three more
src/Gateway/
  Gateway.Api/                   PR-17's edge, and the second host in the
                                 solution: one project, one ProjectReference
                                 (Common.Web), no Application and no
                                 Infrastructure — §10.1 gives it no domain and
                                 no database, so there is nothing for either
                                 layer to hold. appsettings.json is the
                                 deliverable as much as Program.cs is: §10.2's
                                 four routes and four clusters, the first
                                 configuration file in the repo that is itself
                                 under test. GatewayPermissions
                                 (inventory:admin, the one permission a ROUTE
                                 names rather than an endpoint) and
                                 GatewayRateLimiterPolicies beside it, plus
                                 PR-27's GatewayLimits — the platform's one
                                 body ceiling, and the last two entries of
                                 §10.1's "It does" list: the Kestrel limit and
                                 ADR-020's response compression, three
                                 statements in Program.cs and a PR's worth of
                                 argument in front of them
src/Services/Catalog/
  Catalog.Domain/                the first aggregate: Product (Publish factory,
                                 ProductPublishedDomainEvent), ProductId,
                                 Catalog's own Money, IProductRepository —
                                 Add only, argued in the file. Product is the
                                 gates' typeof anchor; AssemblyMarker is gone
  Catalog.Application/           AddCatalogApplication: the §6.2 scan, the
                                 dispatcher, AddDomainEventDispatcher() (§4.2
                                 registers it in Application; PR-10's
                                 NullDomainEventDispatcher is deleted, not
                                 disabled), Integration/
                                 CatalogIntegrationEventMapper — §9.3's
                                 allow-list, one entry — the clock,
                                 RequestMetrics, the three behaviours in
                                 pipeline order, the §4.2 validator scan;
                                 two slices —
                                 Products/PublishProduct (command, validator,
                                 handler) and Products/GetProducts (§6.5's
                                 Dapper keyset query over (PublishedAt, Id))
  Catalog.Infrastructure/        AddCatalogInfrastructure(IConfiguration): the
                                 §6.2 scan, the sealed CatalogDbContext with
                                 §7.2's conventions, EfUnitOfWork,
                                 ProductConfiguration, ProductRepository,
                                 SqlConnectionFactory over the runtime key,
                                 §13.5's SQL readiness check, four migrations —
                                 InitialCreate (hand-written EnsureSchema),
                                 AddProducts, AddOutbox and PR-15's AddInbox
                                 (generated DDL, hand-dressed only) — the
                                 OutboxMessageConfiguration and
                                 InboxMessageConfiguration beside them, the
                                 DbContext alias the filter resolves through,
                                 the InboxTable and RetentionPolicy registered
                                 from one schema literal, and, since PR-13,
                                 Messaging/
                                 AddMassTransitMessaging: the RabbitMQ bus,
                                 eager ConnectionStrings:RabbitMq read, usage
                                 telemetry off, and still no consumer or
                                 receive endpoint — asserted, not assumed
  Catalog.Migrator/              §7.4's job host: MigratorHost builds it,
                                 MigrationRunner migrates and returns 0 or 1.
                                 Reads ConnectionStrings:CatalogMigrator and
                                 never the runtime key (§7.1). Has a
                                 Dockerfile since PR-10, as does Catalog.Api
  Catalog.Api/                   the composition root of §4.2, complete since
                                 PR-16: health probes, OpenAPI,
                                 UseAuthentication/UseAuthorization, the one
                                 catalog:write policy over CatalogPermissions,
                                 and Endpoints/ProductEndpoints — POST and GET
                                 /v1/catalog/products, the group failing closed
                                 with RequireAuthorization() and the GET saying
                                 AllowAnonymous() out loud, because §10.2's
                                 catalog-public route is GET-only and carries
                                 no policy
src/Services/Ordering/           PR-18's, and the scaffold's own output plus
                                 one domain. Same five projects as Catalog,
                                 rendered by tools/new-service and then given
                                 an aggregate — nothing about the wiring was
                                 hand-written or reconciled
  Ordering.Domain/               §5's Order whole: Place, ConfirmStock,
                                 ConfirmPayment, MarkShipped, Cancel, five
                                 domain events, OrderLine and its own
                                 OrderLineId (Entity<TId> compares the type,
                                 so a shared key type would make a line equal
                                 to its order), Ordering's own Money and
                                 Address, CustomerId with deliberately no
                                 New(), IOrderRepository — GetAsync and Add.
                                 AssemblyMarker is gone; Order is the gates'
                                 anchor, and the domain allow-list is four
                                 entries because the first event earned
                                 System.Collections and Money.Of earned
                                 System.Linq
  Ordering.Application/          AddOrderingApplication, and two slices:
                                 Orders/PlaceOrder (§6.4 — no CustomerId on
                                 the command, the handler reads
                                 ICurrentUser.Id) and Orders/CancelOrder
                                 (§11.4's fail-closed ownership check, with
                                 CommandOrigin.User the zero value so an
                                 unset origin checks the owner). OrderErrors,
                                 CancellationReasons over Common.Contracts'
                                 CancelReasons, IProductPriceReader. Handlers
                                 are public — §6.2's scan is public-only, and
                                 internal ones registered silently as nothing
  Ordering.Infrastructure/       AddOrderingInfrastructure: OrderConfiguration
                                 and OrderLineConfiguration (a related entity,
                                 not an owned collection, because an owned
                                 builder has no ComplexProperty), the
                                 repository, ProjectedPriceReader over
                                 ordering.ProductPrices, four JsonConverters
                                 for the value objects the events carry, and
                                 six migrations — the scaffold's four plus
                                 AddOrders and AddProductPrices
  Ordering.Migrator/             §7.4's job host, as rendered
  Ordering.Api/                  OrderingPermissions (§11.4: policies only —
                                 orders:admin is a claim the handler reads),
                                 the two registered policies, and
                                 Endpoints/OrderEndpoints — POST /v1/orders
                                 and POST /v1/orders/{id}/cancel, the group
                                 failing closed and nothing anonymous, because
                                 an order belongs to somebody
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
                                 IntegrationCollection. PR-15 added §9.4's two
                                 consumers here rather than to a service's
                                 suite — they cover common types and name no
                                 Catalog one — over the in-memory harness,
                                 with RecordedMeasurements reading the
                                 instruments back off a MeterListener
  Common.Web.Tests/              + Microsoft.AspNetCore.TestHost; TestPipeline.cs
                                 starts the real middleware pipeline in memory.
                                 PR-16 added the JWT options pin, the
                                 ICurrentUser suite, the four
                                 UseAuthentication assertions (the fourth being
                                 that reversal is not what deletion is), the
                                 permission-policy suite, the inbound
                                 claim-mapping pair and
                                 RealmImportTests — which reads
                                 deploy/compose/keycloak/realm-export.json by
                                 walking up to Platform.slnx. It was the only
                                 test in the repo that read a repository file
                                 until PR-17's GrantablePermissionTests made
                                 the same walk to the same file, from the side
                                 that owns the constant — so the walk is a
                                 shape two suites share rather than one suite's
                                 peculiarity
  Gateway.Api.Tests/             PR-17's, and a new row in §12.1's pyramid —
                                 edge configuration, no containers. Reads the
                                 shipped appsettings.json through the host's
                                 own IConfiguration rather than off disk, and
                                 asks the built host what it accepted
                                 (IProxyStateLookup). StubDestination is a real
                                 Kestrel server on an ephemeral loopback port,
                                 which is what makes the prefix strip and the
                                 100-request rate-limit window observable at
                                 all. No TestSupport beside it: §4.1 gives one
                                 to a service because two suites share a
                                 fixture, and the gateway has one suite. Its
                                 TestAuthHandler is therefore a second copy of
                                 Catalog's, deliberately — §4.3 permits one
                                 assembly to cross a boundary and it is not a
                                 test library. PR-27 added the first tests in
                                 the repo that need a REAL SERVER rather than
                                 TestServer: RequestSizeLimitTests drives the
                                 factory over UseKestrel(0), because
                                 ConfigureKestrel binds under TestServer and
                                 governs nothing. Run there the suite goes RED
                                 — two 413s arrive as the stub's 204 — and the
                                 one test that passes is the acceptance one, so
                                 a suite asserting only that side would be
                                 green against no limit at all.
                                 StubDestination answers 204 until a query
                                 string asks it for a body — per-request rather
                                 than a settable property, so nothing has to be
                                 reset between tests in a class and no test
                                 depends on its order. NOT for independence
                                 between classes: IClassFixture already gives
                                 each class its own instance, which is what
                                 this line claimed until a review checked the
                                 lifetime.
                                 ForwardedSchemeCompressionTests is the only
                                 test anywhere that drives the forwarded scheme
                                 into a response-side decision, and it exists
                                 because a review found ADR-020 arguing from
                                 the hop's scheme rather than the one the
                                 middleware reads
  Catalog.TestSupport/           NOT a test project (§4.1): ServiceFixture —
                                 SQL and RabbitMQ containers, real migrator
                                 run, Respawn reset — CatalogApiFactory (both
                                 connection strings, the unreachable authority,
                                 and a virtual ConfigureAuthentication one
                                 factory overrides off) and PR-16's
                                 TestAuthHandler, shared by the two suites
                                 below, which cannot reference each other
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
                                 bus-connect readiness poll, and PR-15's inbox
                                 filter and retention purge against the real
                                 tables — with
                                 Common.Infrastructure.Tests above, three
                                 projects need Docker, one collection each
  Ordering.TestSupport/          NOT a test project (§4.1), same as Catalog's:
                                 ServiceFixture with §12.4's SeedOrderAsync,
                                 OrderingApiFactory and TestAuthHandler
  Ordering.Domain.Tests/         the §4.2 gates re-anchored on Order, plus
  Ordering.Application.Tests/    the aggregate's 39 tests; the registration
  Ordering.Api.Tests/            surface and §12.4's Local-lane round trip —
                                 which could not be copied from Catalog
                                 unchanged, because a record's equality
                                 compares an IReadOnlyList by reference and
                                 three of these events carry one; then
                                 GrantablePermissionTests, the authorization
                                 policy suite, PlaceOrderTests and
                                 OrderOwnershipTests, which is PR-16's
                                 deferred 404 over HTTP. Ordering.Api.Tests
                                 needs Docker too, so four projects do
  Platform.IntegrationTests/     §12.6, and nothing else (§4.1). References
                                 Common.Contracts alone today — "the only
                                 suite that references every service" grows a
                                 reference as services arrive. ContractSamples
                                 is hand-written, one entry per contract, and
                                 both directions of that registry are asserted
```

The second block is PR-01's, the third PR-02's through PR-05's, the
compose tree PR-06's, the Catalog trees PR-07's, their persistence
PR-08's, the third behaviour with its two ports PR-09's, the slices,
endpoints, TestSupport and container half PR-10's,
`Common.Infrastructure` with its tests PR-12's, the bus registration PR-13's,
the outbox with `Common.Contracts` beside it PR-14's, and the rest of the
contracts, the inbox, the two consumers, the retention purge and
`Platform.IntegrationTests` PR-15's. PR-16's is the security half spread
across three of those trees rather than a block of its own: §11.3's
`AddJwtAuthentication` with §11.4's `RequirePermission`, `ICurrentUser` and
`HttpContextCurrentUser` in the building blocks, Catalog's one policy and its
two endpoint decisions, `TestAuthHandler` in `Catalog.TestSupport`, and the
realm export the whole of it validates against.

Three edges exist between building blocks, and every one of them waited for a
type that could not be written without it:

- `Common.Application → Common.Domain`, drawn by PR-14. §4.2 permitted it from
  the start; what it lacked was a member naming a domain type, and an unused
  project reference is a claim about the dependency graph that nothing makes
  true. §7.5's `IDomainEventCollector` returns `IReadOnlyList<IDomainEvent>`
  and settled it.
- `Common.Infrastructure → Common.Application`, `Common.Domain` and
  `Common.Contracts`, all three drawn by PR-14's outbox. `MessageTypeMap`
  selects on `IDomainEvent` **or** `IIntegrationEvent`, which is why the last
  two arrive together.
- `Common.Web → Common.Application`, the oldest, because `ToHttpResult` maps
  an `Error` and cannot be written without one.

**Which PRs did *not* draw the first edge is worth keeping, because the
argument still binds the two files concerned.** PR-08's `IUnitOfWork` could not:
no member of it names a domain type, which is the whole reason `ExecuteRawAsync`
takes `string` and `object`. **Nor was it PR-09's**, though this file and two
comments beside it said so until a review checked the claim — §6.3's behaviour
reads `ModifiedAggregateCount` as an `int`, and the `is IAggregateRoot` test it
is derived from lives in `EfUnitOfWork`, on Infrastructure's side of §4.2.
Counting behind the port is precisely what keeps it there, and **the reference
now existing is not permission for either of them to stop**.

The licence gate lives under `.github/` rather than a `build/` directory
because it is CI-only and §4.1 draws no such tree. It is stdlib Python, reads
`Directory.Packages.props` and Appendix B as text, and needs no restore — the
reason §15.1 can put it ahead of the build. **Adding a package means adding its
backticked identity to Appendix B in the same change**, or the gate fails the
build before anything compiles.

`docs/roadmap.md` sits outside the blueprint tree deliberately — it is a
schedule, not a specification, and it goes stale on a different clock. Nothing
in it states a requirement: it prices Appendix C's 27 PRs in ideal
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

**One class of breakage is silent, and only compiling the output catches it.**
The suite runs on stdlib Python with no SDK, so it renders a service and
inspects the text — it never builds one. A Catalog test that uses a helper the
scaffold *removes* therefore renders into a service that does not compile,
with all 76 tests green: PR-14 wrote a dispatcher test over
`OutboxRows.Broker`, which leaves with the first contract, and nothing said a
word. **A change touching `tests/Catalog.*` is not verified until a scaffolded
service has been built**, which is four commands and a cleanup:

```bash
python tools/new-service/new_service.py Yankee --port 5199
dotnet build tests/Yankee.Api.Tests/Yankee.Api.Tests.csproj
rm -rf src/Services/Yankee tests/Yankee.*
git checkout -- Platform.slnx deploy/compose/
```

The scaffold edits five tracked files as well as creating its own, so the
`git checkout` is part of the procedure rather than tidying after it.

**The probe used to be `Ordering` at 5103, and PR-18 is why it is not.**
Ordering is a real service now, so the create refuses a name and a port that
are taken, and the `rm -rf` — followed literally by anyone reading this block
after the merge — deletes it. `Yankee` at 5199 is one of the probes the
scaffold's own suite uses, chosen because a probe cannot quietly become a
service later. The same trap caught `tools/new-service/README.md`, which named
the same command.

Planned, per §4.1 — do not invent a different shape for it. All five building
blocks are shown above; the tree below is the target shape, and its
annotations mark what has already landed:

```
src/BuildingBlocks/   all five exist — .Contracts since PR-14, complete at PR-15
src/Gateway/          Gateway.Api (YARP) — landed with PR-17
src/BFF/              Web.Bff
src/Services/         Catalog, Ordering, Inventory, Payments — five projects each:
                        Domain, Application, Infrastructure, Migrator, Api
                        (Catalog's five landed with PR-07, as shells;
                        Ordering's five with PR-18, rendered rather than
                        written — the scaffold's dogfood)
                      Shipping — the same five, but Worker in place of Api
                      Notifications — four: no Domain, and a Worker
tests/                <Service>.Domain.Tests, .Application.Tests, .Api.Tests,
                      .TestSupport, plus Platform.IntegrationTests
                        (Catalog's first three landed with PR-07; TestSupport
                        with PR-10, when the container-backed handler tests
                        became §4.1's second consumer — "referenced by the two
                        above, which cannot reference each other";
                        Platform.IntegrationTests with PR-15, when §12.6's
                        rules arrived with the assembly they constrain;
                        Ordering's four with PR-18, all of them scaffolded —
                        TestSupport included, which arrives with the service
                        rather than waiting for a second consumer the way
                        Catalog's did)
                      Gateway.Api.Tests is outside that pattern and landed
                        with PR-17 — the gateway is a host and not a
                        <Service>, so it has an .Api.Tests and none of the
                        other three; §4.1's tree and §12.1's pyramid each
                        gained a row for it
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

`Platform.slnx` holds thirty projects and `dotnet test` runs 621 tests, so
the build rules and the drift rules below are live and a green run now means
something. Since PR-11 there is a second suite with a second runner:
`py -3.12 -m unittest` in `tools/new-service` runs 81, and CI has a `scaffold`
job for them beside `licence-gate`. **PR-18 has landed** — Ordering is the
second service, rendered by the scaffold with no reconciliation owed to it,
and it carries PR-16's deferred security test: user A *cancelling* user B's
order → 404, §11.4's ownership check, which needed the first resource in the
platform that has an owner. **PR-27 has landed too** — out of numerical order
and in sequence, because Appendix C numbers it last and makes it depend on
PR-17 alone, so it may land at any point after the gateway; it closes §10.1's
"It does" list and the gateway is finished. **PR-19 (the BFF) and PR-20
(Ordering's Catalog projection) are next**, and PR-20 is what fills the
`ordering.ProductPrices` table PR-18 shipped with its reader and no producer.
PR-07 landed the Catalog skeleton, so §4.2's architecture rules are a
build failure — each gate was observed red against a deliberately added
forbidden reference before it was trusted, and since PR-10 the endpoints gate
judges a real type (`ProductEndpoints`) rather than passing vacuously.

PR-27 landed the last two entries of §10.1's "It does" list — the body ceiling
and ADR-020's response compression — and five of its decisions bind what comes
after:

- **`EnableForHttps = true` is what makes the edge compress at all, and this
  file argued the exact opposite first.** The claim was that TLS terminates at
  the ingress (§10.1), so the gateway is served plain `http`, so the flag never
  fires and setting it true merely says out loud what happens anyway. Every
  clause true, conclusion inverted: §4.2's forwarded-headers block enables
  `XForwardedProto`, `UseForwardedHeaders` rewrites `Request.Scheme` from the
  ingress's header, and the compression middleware decides at the first
  **write** — below the whole pipeline — so the scheme it reads is the
  rewritten one. At the default, a gateway behind an HTTPS ingress compresses
  **nothing** and no response says why. Copilot round 1 found it;
  `ForwardedSchemeCompressionTests` is the measurement, red against the
  property removed.

  **The lesson is not about compression.** A middleware that acts on the
  response decides *after* everything below it has run, so reasoning about
  what it "sees" from the position of its `Use` call is reasoning about the
  wrong moment. `UseResponseCompression` sits above `UseForwardedHeaders` and
  still reads the header `UseForwardedHeaders` wrote. Any claim of the form
  "this middleware runs before that one, so it cannot see X" is worth
  measuring rather than reading off the pipeline order.

  What survives the correction is the *decision* and the shape of its
  argument: the flag cannot be argued from the scheme in either direction —
  the response reaches the browser over TLS whatever the inner hop was — so
  ADR-020 argues it from content. No body crossing this edge pairs a secret
  with reflected input.
- **The one body that reflects a client-supplied value is the one the default
  MIME list omits, and that is luck rather than design — so a test pins it.**
  §10.5's problem+json carries the `X-Correlation-Id` the caller may have
  chosen (§10.4), which is the input half of BREACH; `application/problem+json`
  is absent from `ResponseCompressionDefaults.MimeTypes` and therefore travels
  plain. Nothing in this solution decided that, so `CompressedResponseTests`
  asserts both directions from the wire. **Adding a type to
  `CompressibleContentTypes` is re-taking ADR-020**, not a tuning change.
- **The 413 needed no exception handler, and the 400 and 409 rows each did.**
  Kestrel throws `BadHttpRequestException` carrying the status and
  `ExceptionHandlerMiddleware` reads it off the exception instead of defaulting
  to 500, so §10.5's shape arrives with `correlationId` and `traceId` for free.
  Measured over both framings — a declared `Content-Length` and a chunked body
  with none — because the plausible failure was the opposite one: YARP's
  forwarder absorbs client-body faults into its own 400, and it does not absorb
  this.
- **`ConfigureKestrel` is a silent no-op under `TestServer`, so the limit is
  the first property in the solution that a real server has to serve.**
  `WebApplicationFactory.UseKestrel(0)` is the seam, and its ordering is
  load-bearing: it throws once the host is initialised and `CreateClient` is
  what initialises one, so a factory whose client is taken first is a
  `TestServer` again with no failure to say so. The general rule is worth
  carrying past the gateway — drive `TestServer` for what the *application*
  decides, a real server for what the *server* decides, and the two are
  indistinguishable from the test.
- **The compression middleware has no ordering rule a test can catch, and
  saying so is the point.** Moved below the auth pair and the limiter, every
  test in `Gateway.Api.Tests` stays green — because the only bodies those
  middlewares produce are problem+json, which is not compressed. Its *absence*
  is caught immediately, which is the failure mode that matters:
  `AddResponseCompression` succeeds and compresses nothing without
  `UseResponseCompression`, exactly the shape §10.3's registration has. Both
  halves measured, in the habit PR-16 and PR-17 established — do not write down
  an ordering claim a test is not making.

**One test claim was found false by asserting it**, and it is the sharper
finding of the two: the chunked-body case was a second copy of the
`Content-Length` case, because `StreamContent` over a `MemoryStream` reads the
stream's length and sends the header anyway. It passed, for the wrong reason,
and only `ContentLength.ShouldBeNull()` told the difference. **A test named for
a case is not a test of it** — the streaming path is the one an attacker
chooses, since omitting a header costs the sender nothing.

**"A test that would pass" is this PR's most repeated error, and it was written
four times before anything checked it.** Copilot round 2's suppressed block —
which carried five findings under a heading saying no new comments were
generated — caught the same inversion at four sites: that the size-limit suite
"would pass" over `TestServer`, that a decompressing client would "leave every
assertion passing", and that a test carrying its own copy of the ceiling "would
pass" against a differently configured gateway. **All three are the opposite of
what happens.** Measured for the first: over `TestServer` the suite goes red,
two of three, because the oversized bodies reach the destination and answer 204
where 413 was expected.

The useful half is what the measurement added. Exactly **one** test passes
there — the one asserting a body *at* the ceiling is forwarded — so the silent
outcome is real but belongs to a suite written from the acceptance side alone.
Asserting the boundary from both sides is what converts it into a loud failure,
which the suite already did and the prose had not noticed. **A hazard framed as
"this would pass" is a claim about a run nobody performed**; this repository
already says not to write down an ordering claim a test is not making, and this
is the same rule for a counterfactual.

**ADR-020's escape hatch was named wrong too, and PR-19 is who it costs.** The
first version told the BFF to protect a secret-bearing response by *encoding*
it itself, on the ground that the middleware skips a response already carrying
a `Content-Encoding`. The mechanism is right and the instruction is useless:
gzip opens the same length side channel wherever it is applied, so a
BFF-compressed secret leaks exactly as a gateway-compressed one does. The
opt-out is `Content-Encoding: identity` — "no transformation applied", skipped
by the same header check and readable on the wire. Copilot round 1 again, and
it is worth noticing that both of its findings were **the argument being
wrong while the code was right**: the flag and the header check were correct
in `Program.cs` throughout. A review that only diffs code would have found
neither.

**`Cache-Control: no-transform` was the opt-out all along, and the gateway was
violating RFC 9111 by ignoring it.** Round 3 proposed the directive and was
half right; round 5 pressed the other half and was fully right. The framework
does **not** implement it — measured twice, an 8 KiB body coming back gzipped
with the directive intact — but §5.2.2.6 says an intermediary "regardless of
whether it implements a cache" MUST NOT transform the content, a content coding
is such a transformation (RFC 9110 §7.7), and a YARP gateway is an
intermediary. So the gateway now carries
`NoTransformResponseCompressionProvider`, a subclass of the framework's own
with one case in front of `ShouldCompressResponse`, registered by `Replace`
rather than by sitting above `AddResponseCompression`'s `TryAddSingleton`.

**The intermediate state is the lesson, and it lasted two rounds.** Having
measured that the framework ignores the directive, this file recorded the
measurement as though it settled the question — pinning the violation in a test
and telling PR-19 to use `Content-Encoding: identity` instead. A measurement
says what the code *does*; it never says what it *may* do. The specification
was one fetch away and nothing had read it.

PR-17 landed the gateway — §10.2's routes, §10.3's limiter, §4.2's edge
pipeline — and fourteen of its decisions bind what comes after:

- **An unresolvable policy name stops the gateway; it does not silently drop
  the route, and four sites said it did.** §10.2, §4.2's sample, §11.4's
  callout and Appendix C's PR-17 row all described a per-route drop that leaves
  the host "up healthy serving whichever routes happened to validate".
  Measured: `ProxyConfigManager.InitialLoadAsync` throws out of
  `MapReverseProxy()` with an `InvalidOperationException` naming the policy and
  the route, for **both** registries — the authorization one and the rate
  limiter's. All four were amended. The correction runs the reassuring way, and
  the consequence worth carrying is that **the gateway is the one host where an
  unregistered policy name fails better than in a service**, where §11.4's
  endpoint still throws on the first request that reaches it.
- **The whole route file ships, three of its four services ahead of
  themselves.** This is the opposite of the Compose rule and the asymmetry is
  in what each costs: a Compose block naming an absent image fails `up`, a
  route to an absent destination 502s one path. What buys it is that PR-17's
  two config tests say nothing over a single route — §11.4 names a vacuously
  passing policy test as its own defect — and that delivering the file a route
  at a time makes each later PR re-decide the policies, which is §10.2's
  dual-version trap. **It is not licence to invent routes**: a `/api/v2/orders`
  route would fail the forwarded-path assertion, correctly, and the
  dual-version pair stays an example in the chapter.
- **The forwarded path is a prefix of the service's group, not an equality**,
  and Catalog is the counterexample that settled it: `/api/v1/catalog/{**}`
  strips to `/v1/catalog` while `ProductEndpoints` maps
  `/v1/catalog/products`. Appendix C said "equals" and was amended. The
  registry the assertion reads is hand-written, one entry per cluster, both
  directions asserted — `ContractSamples`' shape — because reading it from the
  services would mean the gateway's suite referencing every service, which is
  the coupling §10.1 exists to prevent.
- **A stub destination that answers beats an address that refuses, and the
  measurement is the argument.** Pointing the clusters at `127.0.0.1:1` cost
  ~2 s a request on this host, so exhausting §10.3's 100-request window took
  three and a half minutes, the window replenished, and the rate-limit test
  failed while the limiter worked. A Kestrel server on an ephemeral loopback
  port is faster *and* is the only thing that can observe the forwarded path,
  which is the assertion §10.2 says nothing else in the solution can make.
- **Both conditional reads are hoisted out of their options callbacks.**
  §4.2 printed `GetRequiredSection("Cors:Origins")` inside `AddCors`'s lambda,
  which runs when the CORS options are first resolved — on a request. "Enabled
  but unconfigured" then throws at a request rather than at a deployment, which
  is the exact deferral the flag pair exists to avoid. Both reads moved above
  their registrations and `ConditionalBlockTests` holds all four states.
- **§4.2's forwarded-headers block did not compile at this pin.**
  `KnownNetworks` carries `ASPDEPR005` in .NET 10 — an error under ADR-019, not
  a warning — and its replacement `KnownIPNetworks` takes `System.Net.IPNetwork`
  while the bare name binds to `Microsoft.AspNetCore.HttpOverrides.IPNetwork`,
  brought into scope by the `using` the `ForwardedHeaders` flags need. Two
  wrong spellings on one line, found by compiling it.
- **The 429 is written through `IProblemDetailsService`.** §10.3 printed
  `WriteAsJsonAsync`, which emits `application/json` and runs none of §10.5's
  customisation — so the one response a client is most likely to handle
  programmatically would carry neither the right media type nor
  `correlationId`, on a platform whose stated promise is one error shape.
- **`Retry-After` rounds up, and the rule needed a type to be testable at
  all.** The obvious `(int)remaining.TotalSeconds` truncates, so a lease with
  0.8 s left advertises `Retry-After: 0` — not a lost fraction but an
  instruction, sending a well-behaved client back into a limiter still
  refusing. What makes it interesting is the second half: the 429 test asserted
  a floor on the header and **passed with the truncating cast**, because the
  window is a minute long and a rejection carries tens of seconds. Reaching the
  defect through HTTP means holding a window open for fifty-nine seconds.
  `RetryAfterHeader` exists so three rows of a theory can do it instead — and a
  comment claiming the HTTP test caught it was written, and was wrong, before
  this was measured.
- **The authenticated rate-limit policy had no test, and the one added does
  not catch §4.2's ordering rule.** Only the anonymous window was ever driven
  to rejection, so the subject partition — the thing making a per-user quota
  per-user — rested on nothing. The new test proves two subjects hold
  independent buckets; run against a pipeline with `UseRateLimiter` moved above
  `UseAuthentication` it still passes, as does every other test in that
  project. The limiter is
  live under the reversal (the anonymous window still rejects), so the "degrades
  to per-IP" mechanism is reasoned and unobserved while the "silently" half is
  measured. §4.2 now says which is which. **PR-16's lesson repeated exactly**:
  keep the line, and do not believe a test is watching it.
- **The forwarded-headers block had no positive test, and the limiter's
  ordering row still has none — the contrast is the point.** Both are "this
  middleware must run before that one" claims about the same pipeline, and
  only one of them turned out to be observable. `ForwardedHeadersTests` spends
  one forwarded address's window, proves it is refused, and shows a second
  address still served; moved below `UseRateLimiter`, the two collapse onto the
  one connection the gateway can see and it goes red. The limiter-vs-
  authentication row reversed the same way and **nothing failed**. So a
  middleware-order rule is testable or it is not, case by case, and which is
  which has to be measured rather than assumed from the shape of the claim.
  Under `TestServer` the peer address is null, so the test installs an
  `IStartupFilter` to give the request one — the only seam that gets in front
  of a `Program.cs` a test may not edit.
- **"Blank counts as missing" had to be learned twice, and the second time
  was a review finding.** PR-16 wrote it into `AddJwtAuthentication` for
  `Identity:Authority` and this file records the argument — an environment
  variable set to the empty string reaches `Configuration` as `""`, not null.
  The gateway's `Cors:Origins` then shipped guarded by `GetRequiredSection`
  alone, which proves a section *exists*: `Cors__Origins__0=` binds to an array
  holding one empty string, `WithOrigins` accepts it, the host starts, and
  every browser request is refused by a policy matching no origin. **A lesson
  recorded in prose is not a lesson applied**; the guard is now a check on the
  bound values with a test behind it, which is the form that travels.
- **The fix that lands in code and not in the sample is this repository's
  most reliable defect, and PR-17 produced five of them.** The rule at the top
  of this file already says a code change contradicting a chapter is not done
  until the chapter moves with it; what PR-17 adds is the direction it actually
  fails in. Not code drifting from a written spec — a *correction* landing in
  `Program.cs` or a test and never reaching the sample it was copied from. The
  CORS guard grew four clauses over four review rounds and §4.2's sample
  tracked it a round late every time; the stub-path assertion was tightened in
  `ProxiedRouteTests` and left weak in §12.4. **Each one re-arms the defect for
  whoever builds the next host from the chapter**, which is precisely who the
  chapter is for. The habit that catches it is mechanical: after fixing a line
  that came from a sample, grep the blueprint for the line you replaced, not
  for the topic.
- **401 and 403 carried no body at all, in every host, since PR-16.** §10.5
  opens by promising one error shape "regardless of which service produced
  it", and its own table lists both statuses — but a challenge and a forbid are
  written by the middleware before any endpoint runs, and
  `AddCommonProblemDetails` only supplies a writer that nothing on that path
  was calling. So the two statuses a client meets first were the two that broke
  the promise. **`app.UseStatusCodePages()` is the whole fix** — since .NET 8
  it writes through `IProblemDetailsService` — and it is one explicit line per
  host rather than something `AddCommonWebDefaults` can add, because it is
  middleware and §4.2 keeps middleware order visible at the composition root.
  Found by asserting the media type on a gateway 401, which is the assertion
  nobody had written: `ShouldBe(HttpStatusCode.Unauthorized)` passes just as
  happily on an empty response.
- **A permission a *route* requires obeys §11.4's rule exactly as an
  endpoint's does, and the realm role arrives in the same change as the
  constant.** PR-17 registered `inventory:admin` and named it on a route
  without adding the role to the realm's `commerce-api` client, so
  `/api/v1/inventory` was 403 for every principal Keycloak could issue — not
  a wrong answer a test would catch, a path nobody could reach. **Neither
  existing guard could see it**: §11.4's constant makes a *misspelling* a
  compile error and says nothing about a name the provider has never heard of,
  and `RealmImportTests`' closed-set assertion compares against a literal
  because `Common.Web.Tests` is a building block's suite and may not reference
  a host to read its constants. So the check lives with the constant —
  `GrantablePermissionTests` in `Gateway.Api.Tests`, observed red against a
  renamed role — and **Catalog owes the same test**: `catalog:write` is
  grantable today because PR-16 happened to add both halves at once, not
  because anything checks that it did. Verified in a live Keycloak rather than
  by reading the export: both roles present, `demo` still carrying exactly
  `catalog:write`, `browser` still carrying no `permission` claim at all, and
  `sub`, `email` and `realm_access` all intact — the negative half being the
  one §11.5 says matters most.

PR-16 landed security — §11.3's JWT validation in `Common.Web`, §11.4's
policies and port, the realm import — and seven of its decisions bind what
comes after:

- **`ICurrentUser` and `HttpContextCurrentUser` are common, not per-service,
  and §11.4 was amended.** The chapter wrote `Ordering.Application` and
  `Ordering.Infrastructure` for the same reason §9.4 wrote
  `ordering.OutboxMessages` — it is Ordering's viewpoint. Nothing in either
  type names a service. The implementation could not go in
  `Common.Infrastructure` in any case: that project takes no
  `FrameworkReference` and `IHttpContextAccessor` arrives with one, so
  `Common.Web` is the only building block that can hold it. Both are
  registered by `AddCommonWebDefaults`, beside the `AddHttpContextAccessor()`
  without which `ValidateOnBuild` fails instead of the first ownership check.
- **`Identity:Authority` is an eager read that throws naming the key, not an
  options type.** §15.4 says `ServiceIdentityOptions` is deliberately the
  *only* options type in the solution and argues why; a second bag bound to a
  section holding one value is the shape that rule forbids. §12.4's fixture
  comment claimed `OptionsValidationException` here and was amended. The
  audience is a **constant** for the neighbouring reason — §11.5 gives the
  platform one audience, so the value never varies between environments, which
  is §15.4's own test for what is not configuration.
- **The GET stays anonymous, permanently, and says so.** PR-10's README named
  the whole slice as a temporary gap; only the write path was one. §10.2's
  `catalog-public` route matches GET alone and carries no `AuthorizationPolicy`,
  so a product listing is public at the edge and public here. The group fails
  closed with `RequireAuthorization()` and the GET adds `AllowAnonymous()`
  explicitly — absence and decision must not look the same.
- **`WebApplication` adds the authentication middleware itself, so no test can
  catch `app.UseAuthentication()` being deleted.** §4.2's ordering table said
  its absence 403s every authenticated request and §12.4 named a 401 test as
  the thing that catches it; both were checked by deleting the line, after
  which every test in the repository still passed. Keep the explicit calls —
  they are about **order**, they are required by any host that is not a
  `WebApplication`, and an implicit pipeline is unreviewable — but do not
  believe a test is watching them.

  **The claim stops at deletion, and a review round found the table promising
  more than that.** Auto-insertion is suppressed by the markers the explicit
  calls set, so it repairs an *omission* and not an *ordering*: both calls
  present in the wrong order means authorization evaluates against a `User`
  nothing has populated, and every authenticated request 401s. Measured through
  a real `WebApplication` over three pipelines — correct 200, **reversed 401**,
  neither 200. So the framework protects a host from forgetting a line and not
  from misplacing one. `Common.Web.Tests` carries all four claims, the third
  being a regression guard on the framework and the fourth this one.
- **The realm is a full Keycloak export and shrinking it is a silent
  catastrophe.** A hand-written import naming only the `commerce-api` client
  scope is the obvious first attempt; Keycloak treats `clientScopes` as the
  **complete** set, so the built-ins are never created and the token loses
  `sub`, `preferred_username`, `email` and `realm_access` at once. `sub` is the
  one that matters — `ICurrentUser.Id` reads it. Found by importing exactly
  that file into a container and reading a token, which is also how the shipped
  realm was verified. **Build a realm through the admin API and export it; do
  not write one.**
- **Permissions are client roles on a `commerce-api` client, not realm roles.**
  Measured, not assumed: a realm-role mapper also emits `offline_access`,
  `uma_authorization` and `default-roles-commerce` into the `permission` claim,
  which puts Keycloak's internals into the platform's vocabulary and makes it
  open-ended. The negative half is what the verification turned on — an
  ungranted user must carry **no** `permission` claim at all.
- **`TestAuthHandler`'s constant is `SchemeName`.** `AuthenticationHandler<T>`
  already declares a protected `Scheme`, so §12.4's printed `public const
  string Scheme` hides it, and CS0108 is an error under ADR-019. The sample had
  been unbuildable since it was written; the same collision bit a second time
  inside a nested probe handler, where `Scheme` silently bound to the base
  property instead of the enclosing constant.

**Three more arrived from the review loops, and all three are about things no
test in the repository was watching.**

- **A `ProjectReference` is a `COPY` line in two Dockerfiles, and forgetting it
  breaks the images silently for as long as nobody runs one.** `dotnet restore`
  writes each project's own `obj/project.assets.json`, so a csproj absent when
  it runs is not restored and the `--no-restore` publish fails four steps later
  with `NETSDK1004` naming a project the Dockerfile never mentions. PR-14 drew
  `Catalog.Infrastructure → Common.Contracts` and `→ Common.Infrastructure`
  without the two lines, and **both images were unbuildable from PR-14 until
  PR-16 found it by running the stack**. `dotnet build Platform.slnx` cannot
  see this, and neither can CI: the compose smoke is the only job that builds
  these images and it is path-filtered on `deploy/compose/**`, while a
  reference lands under `src/`. Fixing the filter is a real option and a wider
  change than this PR; the honest state is that the gap is named in both
  Dockerfiles and in §15.2, and carried by whoever adds the next reference.
- **Keycloak's issuer follows the request host unless `KC_HOSTNAME` says
  otherwise, and both halves of the fix are load-bearing.** A token minted
  through `localhost:8080` and a discovery document read through
  `keycloak:8080` disagree about `iss`, so `ValidateIssuer` rejected the exact
  token `deploy/compose/README.md` tells a developer to obtain — on a stack
  where every container reported healthy. `KC_HOSTNAME` pins the frontend
  issuer and `KC_HOSTNAME_BACKCHANNEL_DYNAMIC` keeps the JWKS URI
  container-reachable; **one without the other trades one broken flow for
  another**, which is why they arrive together. Measured on the master realm
  rather than argued.
- **A host-run service is Production, and that is what breaks the inner
  loop.** No project ships a `launchSettings.json`, so `dotnet run` selects
  Production, where `RequireHttpsMetadata` is on — and against a plain-HTTP
  local authority the host never fetches the discovery document at all.
  `ASPNETCORE_ENVIRONMENT=Development` leads **every host-run block that names
  an authority** — Catalog's and, since PR-17, the gateway's, but not the
  migrator's, whose job never sees a token. This line said "both host-run
  blocks" and PR-17 made it false by adding a third: the gateway snippet went
  out without the export and did not start when pasted into a clean shell,
  which is what a rule stated as a count rather than as a reason costs. The
  containers set it, which is precisely why the Compose path never showed it.
- **`ICurrentUser`'s implementation reads one authenticated projection, not
  `HttpContext.User`.** Claims and authentication are independent: a
  `ClaimsIdentity` with no authentication type carries claims perfectly
  happily and still reports `IsAuthenticated` false, so members reading the
  principal directly answered a subject and granted a permission for a caller
  the interface denies. Nothing reaches it today — `JwtBearerHandler` produces
  an authenticated principal or an empty one — which is the argument for
  fixing a fail-closed contract while it is still theoretical rather than the
  argument against.

**One finding against this file's own procedure**, worth keeping because it
cost work: the scaffold cleanup CLAUDE.md prescribes ends with
`git checkout -- Platform.slnx deploy/compose/`, which is correct only while
the PR does not itself change `deploy/compose/`. PR-16 changes all three files
in that tree, and the cleanup reverted them. **Commit before dogfooding the
scaffold**, or restore the tree's own changes afterwards.

PR-15 landed the consume side — §9's remaining contracts, §9.5's inbox, §9.4's
two consumers and one retention purge over both tables — and eight of its
decisions bind what comes after:

- **The contract assembly is complete, and §3.2 is what decided that.** Five
  versioned namespaces, twenty-six records and two static vocabularies —
  every name in §3.2's Publishes and Accepts columns plus the payload types
  §9.1 and §9.6 give them. This suspends the usual rule that a record belongs
  in the PR whose code publishes it, and Appendix C is what suspends it: the
  §12.6 suite constrains the assembly as a whole, so the rules "arrive with the
  assembly they constrain". **It is not licence to keep adding.** A sixth
  service's contracts arrive with that service.
- **`InboxFilter<T>` and both consumers are `Common.Infrastructure`, not
  per-service, and the chapters were amended to match.** §9.4 and §9.5 write
  `namespace Ordering.Infrastructure.Messaging` for the same reason §9.4 used
  to write `ordering.OutboxMessages` — the chapter is Ordering's viewpoint.
  Nothing in any of the three is per-service; what *is* per-service is which
  endpoint binds which contract, and that stays in each service's
  `AddMassTransitMessaging`.
- **The filter's `DbContext` is an alias, and the delegate in it is
  load-bearing.** `AddScoped<DbContext>(sp => sp.GetRequiredService<CatalogDbContext>())`
  is the registration; `AddScoped<DbContext, CatalogDbContext>()` compiles,
  resolves, and builds a **second** context in the same scope — so the inbox
  row commits in its own transaction and §9.5's atomic row silently becomes its
  non-atomic one. Nothing fails, which is why a test asserts the two
  resolutions are one instance.
- **Catalog binds no receive endpoint, and that is asserted rather than
  assumed.** §3.2 gives it one Consumes cell — `StockLevelChanged`, Inventory's
  — and no `IIntegrationEventHandler` for it exists until §8.4's cache
  invalidator has a cached query to invalidate. Binding a type with no handler
  is one of the two sites §9.4 says must throw, so the endpoint would fault
  every message it received. This is PR-14's `Local`-lane shape exactly: the
  consumers are proven by the in-memory harness in `Common.Infrastructure.Tests`,
  and the inbox and purge by container tests over the real host.
- **The inbox table ships to every service anyway, for `AddOutbox`'s reason
  inverted.** The purge runs from first boot and deletes from both tables, so a
  service carrying it without the table logs a failed delete every pass —
  where a dispatcher without its table logs a failed claim twice a second.
  Consuming nothing does not exempt a service; Catalog itself is the proof.
- **The inbox row is staged *after* the consumer returns, and staging it
  earlier is a silent disabling of the whole mechanism.** A row added before
  `next.Send` is a tracked entity on the context the consumer also uses, and
  every message-borne command reaches §6.3's `TransactionBehavior` →
  `EfUnitOfWork.ExecuteAsync` → `db.ChangeTracker.Clear()`, PR-09's line. The
  clear takes the pending row, the following `SaveChangesAsync` writes nothing,
  and no command is ever recorded. Two mechanisms already here, each right on
  its own, in tension where they meet — and invisible until a consumer does
  work, which is why the covering test drives one that clears the tracker.
- **A rolled-back unit of work now clears the tracker too, and the comment that
  said it need not is the lesson.** `EfUnitOfWork` returned on a failed
  `Result` leaving the rejected mutations tracked, because "§6.3's behaviour
  declines to SaveChanges … which is enough for tracked changes" — true while
  that behaviour was the *only* caller of `SaveChanges` on the scope. The inbox
  filter is the second, and it saves unconditionally, so a domain refusal
  would have committed its own mutations outside the rolled-back transaction.
  **A premise about who calls a method is falsified by the next PR that calls
  it**, and this one was.
- **`ProcessedAt IS NOT NULL` on the outbox purge is load-bearing, and is
  tested as such.** Purging on age alone deletes the abandoned rows §13.6's
  alert exists to surface — permanent data loss presenting as a clean, empty
  table. The inbox purges on age alone and the asymmetry is deliberate: an
  inbox row records completed work, so there is no unfinished state for a
  predicate to protect, and what protects it is a window that must outlast the
  broker's longest redelivery. Both windows are a registered `RetentionPolicy`
  rather than constants, because §9.5 tells the reader to check one of them.

**Two findings PR-15 made against the blueprint rather than against the code**,
both fixed in the chapters:

- **§12.6's round-trip assertion could not pass as written.**
  `ShouldBeEquivalentTo` compares the object graph, and a collection expression
  assigned to an `IReadOnlyList<T>` compiles to a synthesised read-only list
  where `System.Text.Json` returns a `List<T>` — a difference that is nowhere
  in the wire format. The suite compares the two **serialised** forms instead,
  because the wire form is what a contract actually is.
- **That comparison has a blind spot, and it takes a second test.** A member
  that fails to serialise at all is absent from both forms, so the contract
  loses a field and the round-trip stays green. A companion assertion requires
  every declared public property to appear in the JSON.

PR-14 landed the outbox — §7.5's flow end to end, §9.4's dispatcher, §9.3's
allow-list mapper — and six of its decisions bind what comes after:

- **`Common.Contracts` exists, with two files.** Appendix C put the project at
  PR-15 and it could not wait: `OutboxMessage.Stage` reads
  `message is IIntegrationEvent`, `MessageTypeMap` selects on that interface,
  and an allow-list mapper with an empty registry could not carry §12.4's
  "the domain type never reaches the broker" — which is only checkable
  because the contract and the domain event have different names. PR-15 adds
  the remaining records to a project that exists rather than creating one.
- **A value object on the `Local` lane needs a `JsonConverter`, and its
  absence is silent.** §5.3's `Money` is a `readonly record struct` with a
  private constructor and two get-only properties; `System.Text.Json` does not
  refuse that shape, because a struct always has a parameterless constructor —
  it builds the default, finds no setter, and returns `Amount = 0` with a null
  `Currency`. Two fixes were tried and rejected: `[JsonConstructor]` puts
  `System.Text.Json` in a domain assembly, which §4.2's allow-list gate names
  as forbidden, and a public constructor does not even work, because for a
  struct the implicit parameterless one still wins. The fix is
  `MoneyJsonConverter` in `Catalog.Infrastructure`, beside the
  `ComplexProperty` mapping that already persists the same type as two
  columns. `OutboxJson` is therefore a **registered instance taking its
  converters**, not a static field: the converters are half of what "both
  sides must agree" means. Verified red by deleting the registration.
- **`ProjectionRegistry`'s memo is a container-scoped singleton, not a static
  field.** §7.5's argument — DI registrations do not change at runtime — holds
  for one container and fails for a process holding several: two
  `WebApplicationFactory` hosts in one test assembly would share whichever
  answer was computed first, so the suite proving an event with no handler
  stages no `Local` row would poison the suite proving that one with a handler
  does.
- **`OutboxDispatcher` is registered with `AddHostedService<T>`, and the
  generic overload is load-bearing.** It records an `ImplementationType`,
  which is what `CatalogApiFactory` matches on to remove *only* this hosted
  service — MassTransit's bus is one too, so `RemoveAll<IHostedService>()`
  would stop the broker. A factory registration leaves `ImplementationType`
  null and that removal would match nothing, leaving the dispatcher draining
  rows underneath the assertions about them.
- **Catalog registers no projection handler and stages no `Local` row**, and
  that is asserted rather than assumed — it is the `IProjectionRegistry`
  contract observed from outside. §8.4's cache invalidator needs a cached
  query to invalidate and there is not one yet, so the lane's behaviours are
  proven by domain events and handlers in `Catalog.TestSupport`, admitted to
  the map through `MessageTypeSource.Add` — the mechanism §9.4 designed that
  type for.
- **The outbox schema is a registered `OutboxTable`, not a SQL literal.** §9.4
  writes `ordering.OutboxMessages` into code every service shares, which
  cannot be right; a dispatcher per service would be §9.3's prohibition on a
  second outbox table set arriving by the back door. The schema is
  shape-checked, because it is the one identifier interpolated into a
  statement rather than parameterised.

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
  `Common.Contracts` existed, no retry policy before the receive endpoints
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
Python, one command per service — and six of its decisions bind what comes
after:

- **Catalog is the template, read at run time.** There is no template
  directory, so there is one copy of the wiring rather than two that drift, and
  the scaffold's tests render *this* repository. The consequence is stated
  above and worth repeating here: a Catalog change can turn
  `tools/new-service`'s suite red, and reconciling the script belongs in the
  same change.
- **The scaffold copies no domain.** The slice is excluded by name, so a new
  service is PR-07's state with the wiring accumulated through PR-16 on it —
  five service projects, three test projects and a `TestSupport` library
  (§4.1 calls that last one *not* a test project, and counting it as one is a
  drift a review has already caught here), both images, the Compose pair, the
  `InitialCreate` migration with `AddOutbox`, `AddInbox` and
  `AddOutboxRetentionIndex` beside it, the bus
  registration with its harness smoke, §9.4's outbox and §9.5's inbox wired and
  empty, the retention purge over both tables, PR-16's token validation and
  `TestAuthHandler`, and no aggregate.
  Five things arrive with the first real slice, each noted at the line
  concerned in the generated code: `Dapper`, the application-test container
  wiring, the two silent-scan registration tests, the permission constant with
  the policy that names it and `AuthorizationPolicyTests` beside them, and —
  with the first domain event — §12.4's round-trip assertion and a
  `JsonConverter` for any value object that event carries.

  **The middleware stays and the policies go, and the split is the point.**
  `UseAuthentication`/`UseAuthorization` are copied because §11.2 says every
  host validates its own tokens whether or not it has an endpoint; a
  `{Service}Permissions` constant and the policy registered from it leave with
  the slice, because a permission nothing requires is a name in the realm
  nobody can act on.
- **The outbox and the inbox ship with their tables, which is why `AddOutbox`
  and `AddInbox` are copied
  rather than dropped with Catalog's other migrations.** A service carrying
  the dispatcher without its table would log a failed claim twice a second
  from its first boot; one carrying the retention purge without the inbox
  table would log a failed delete every pass, and consuming nothing does not
  exempt it. The snapshot is EF's own description of the model that
  leaves — the **last** migration's designer with the aggregate's `Entity(...)`
  block
  removed, which is the one edit made to a machine-owned file here. **The last
  one, and taking an earlier one is a defect with no symptom until the
  service's first `migrations add`**: the outbox designer knows nothing of the
  inbox, so the snapshot would omit a table the `DbContext` maps and EF would
  emit a second `CreateTable` for one the scaffolded migrations had already
  created. Verified
  rather than argued, the same way PR-11's empty snapshot was: a scaffolded
  service was built, `migrations add` was run against it, the generated `Up`
  came out empty and EF's rewritten snapshot was byte-identical to the emitted
  one. Two details were found only by that diff — EF sorts `System` usings
  **before** everything else, which a plain alphabetical sort got wrong the
  moment a `System` using first appeared, and `System.Collections.Generic`
  leaves with the aggregate, because EF emits it for the
  `Dictionary<string, object>` a `ComplexProperty` is mapped as.
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
  Docker; PR-10 raised its timeout to 25 minutes, **PR-17 raised it again to
  30** for the gateway's image, and **PR-18 raised it to 40** for Ordering's
  pair — five images, five minutes each on top of the 15 that pulls alone
  cost, the workflow header carrying the reason every time. The number lives
  in `.github/workflows/compose.yml` and is restated here, which is what makes
  it a claim to reconcile rather than a fact to read: it went stale the moment
  a third image joined, stayed stale for four review rounds, and went stale
  again in the very branch that raised it — this sentence was still saying 30
  while the workflow said 35, found by Grok round 4.

  **Then the raise itself was wrong, which is the more useful failure.** 35
  came from adding PR-17's +5 again, where PR-18 adds *two* images and owed
  +10; both stated rules — `30 + 2 × 5` and `15 + 5 × 5` — give 40, and the
  header said "two more take the same five minutes each" directly above the
  35. Copilot round 9 found it. **A count in a comment guards nothing until
  somebody multiplies by it**, and a sentence explaining the guard is the
  easiest thing in the file to read as already-checked. A change under `src/`
  alone does not re-run the workflow — per-service CI builds are PR-25's.
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
so neither signature names a domain type. PR-14 drew it, with §7.5's
`IDomainEventCollector`, exactly as predicted — and the argument survives the
edge: `TransactionBehavior` still reads an `int`, because counting behind the
port is what keeps EF's change tracker on Infrastructure's side of §4.2. A
reference existing is not permission to start using it. PR-09 brought
`IDomainEventDispatcher` forward as an interface only, over Catalog's
`NullDomainEventDispatcher`, which PR-14 deleted.

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

- **Raised events are no longer dropped, and PR-14 picked them up without
  touching `Product`** — which is what the aggregate raising anyway between
  PR-10 and PR-14 bought. Every `Product.Publish` now reaches §9.3's
  allow-list and commits a `Broker` row in the same transaction as the
  product. What is still dropped is the *`Local`* lane: Catalog registers no
  `IProjectionHandler`, so §7.5 stages no row for one, and that is asserted
  rather than assumed.
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
claim is not atomic with the SQL commit. **PR-14 did not close it, and changed
what it costs rather than leaving it unexamined**: with the outbox in place a
lost acknowledgement republishes the same fact, which is the at-least-once
delivery §9.4 promises and §9.5's inbox is built to absorb — a duplicate
rather than an invisible double-apply. The SQL-side marker is still the fix
for the *command*, and it belongs with §8.5's `IdempotencyBehavior`, whose
seat between Validation and Transaction is already reserved.

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
- **`dotnet test` needs Docker** — since PR-12, and for four projects since
  PR-18: `Catalog.Api.Tests`, `Catalog.Application.Tests`,
  `Common.Infrastructure.Tests` and `Ordering.Api.Tests`, each with its own
  `IntegrationCollection` and therefore its own container set (§12.4's stated
  price). `Ordering.Application.Tests` is deliberately not among them — its
  handler tests moved to `Ordering.Api.Tests`, because `ICurrentUser` is
  `HttpContextCurrentUser` and a handler resolved in a bare scope has no
  principal to bind a subject from. See the commands below.

The building blocks are all five since PR-14, and `Common.Contracts` is
complete since PR-15: the §9.1 envelope over five versioned namespaces holding
every name in §3.2's two published columns. **Complete is not the same as
closed**, and the rule that governs the next addition is the one PR-15
suspended: a record belongs in the PR whose code publishes or consumes it, and
Appendix C suspended it exactly once, for the PR whose other half is the suite
that constrains the whole assembly. A sixth service's contracts arrive with
that service. The same rule applies inside the others:
`Common.Infrastructure` holds §8's Redis helpers, §9.4's outbox, §9.5's inbox
and the two consumers, and `Common.Web`
holds §10.4, §10.5, §11.3, §11.4, §13.2, §13.4 and §13.5, and nothing else.
**`AddCommonWebDefaults` is complete at all five of §13.2's pieces since
PR-16** — the gap that file used to name was JWT validation, and closing it
brought §11.4's port with it for want of anywhere else a
`FrameworkReference` lives.

`Common.Application` is the same story one layer down, with one list finished
and one still short. The pipeline is three
behaviours of four: **`IdempotencyBehavior` (§8.5) does not exist**, and its
seat is between Validation and Transaction. `PluggableInterfaces.All` was built
to be appended to the same way and **is now complete at five** —
`IProjectionHandler<>` joined with PR-14's outbox, and
`IIntegrationEventHandler<>` and `ICommandMessageMapper<,>` with PR-15's
consumers, which is the PR that defined them. Adding an
interface there and nowhere else is the design; adding one before its PR is
inventing a project early by another route.

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
cd tools/new-service && py -3.12 -m unittest    # 81 tests, no Docker, no SDK
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
is 104 tests running in about a second. A shared xUnit collection was rejected
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
  consecutive usings. This one binds source only — a sample is an excerpt
  rather than a compilable unit (Appendix D), so **do not "complete" one by
  adding the block it would need to build.**

  It used to say the samples carry *no* `using` directives at all, and that was
  already untrue when it was written. The blueprint carries exactly two, both
  `using static`: §9.6's saga imports `Endpoints`, §12.4's subject suite
  imports `Principals`. Neither is completeness, which is why both stand — an
  unqualified `Authenticated(caller)` or a bare destination address reads as a
  member of the type being shown unless something says otherwise, and the
  directive is the only thing that can say so. A third is fine on those terms;
  a plain `using` is not, and neither is a second line beside one of these.
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

  **A trailing lambda is an argument like any other, and does not get to hang
  off the opening line.** This is the ragged middle in its commonest disguise,
  because the lambda reads as a body rather than as a list element:

  ```csharp
  // Wrong — one argument up on the call's line, one wrapped under it.
  cache.HasHandler.GetOrAdd(domainEvent.GetType(), type =>
      services.GetServices(typeof(IProjectionHandler<>).MakeGenericType(type)).Any());

  // Right.
  cache.HasHandler.GetOrAdd(
      domainEvent.GetType(),
      type => services.GetServices(typeof(IProjectionHandler<>).MakeGenericType(type)).Any());
  ```

  Note what happens to the lambda in the corrected form: its body comes back
  **up** onto the `=>` line. The lambda-body rule below breaks a body onto its
  own line to make nesting legible, and there is no nesting left to clarify
  once the argument owns a line — so the two rules resolve in one move rather
  than trading off, and the result is one line per argument with nothing
  wrapped inside either.

  **There is no carve-out, and a braced body does not earn one.** An argument
  list holding a lambda has exactly two legal shapes, tried in this order:

  ```csharp
  // 1. One line, if it fits inside 120. Always preferred.
  Publish(payload, type, c => { … }, ct);

  // 2. Otherwise one argument per line — the lambda included, braces and all.
  Publish(
      payload,
      type,
      c => { … },
      ct);

  // And if the lambda itself will not fit on its line, its braces open under
  // the rule that governs braces, at the argument's own column.
  Publish(
      payload,
      type,
      c =>
      {
          c.MessageId = message.MessageId;
          c.CorrelationId = message.CorrelationId;
      },
      ct);
  ```

  This replaced a carve-out that kept the leading arguments up on the call's
  line whenever the trailing lambda had a braced body — `ReceiveEndpoint("q",
  e => { … })` and the eleven other builder-DSL sites across §7.2, §9.4, §9.6
  and §9.8, plus `ProductConfiguration`. The argument for it was that braces
  already show the block's extent and `});` already marks the call's end, so
  one-argument-per-line bought nothing; the argument against is that it made
  the rule undecidable from the call site. Whether a leading argument may stay
  up depended on the *last* argument's body kind and on whether anything
  followed it — two lookaheads, and a reviewer who performed only the first got
  `Publish(payload, type, c => { … }, ct)` wrong. That case cost this branch a
  review round, and the count of ragged sites went "two, then fourteen, then
  seventeen, then eighteen" while the carve-out stood.

  **The cost is real and is accepted**: every builder DSL in the blueprint now
  breaks across four or five lines where it used to open on one, and that is a
  shape most C# codebases do not use. It buys a rule with no lookahead — does
  it fit on one line, or does every argument get its own — which is the same
  rule the rest of this section already applies to every other list.

  A **single**-argument call is untouched by any of this, because there is no
  leading argument to strand: `AddRateLimiter(options => { … })` and
  `app.Use(async (context, next) => { … })` keep their shape.

  Nor is a lambda the only thing that can hang:
  `WriteAsJsonAsync(new ProblemDetails { … }, ct)` is the same shape with an
  object initialiser in the lambda's place, and §10.3 had one.

  **Two greps narrow this down and neither closes it.** The arrow —
  `\(.+,\s*\w+\s*=>\s*$` — catches a lambda left hanging off a call that has a
  leading argument, which is now wrong whatever its body looks like. The closer
  — `^\s*[]})],\s*\S` — catches a bracket closing at the head of a line with an
  element still after it, which is what `Publish(payload, type, c => { … }, ct)`
  and `WriteAsJsonAsync(new ProblemDetails { … }, ct)` look like from below and
  the arrow cannot see.

  **What neither sees is the plain one**: a broken list whose continuation line
  simply carries two ordinary arguments, `SendAsync(` / `new
  CancelOrderCommand(…), ct);`. No arrow, no bracket at the head of the line —
  nothing to anchor a pattern to, and it was found by a reviewer reading rather
  than by either grep. Treat the two as a sieve that catches the disguised
  cases, not as a proof the corpus is clean.

  **Write the closer for the tool you are running it in**, because
  `[}\])]` is not one pattern. Ripgrep reads `\]` as an escaped bracket and
  builds the class `}` `]` `)`; POSIX `grep` treats a backslash inside a
  bracket expression as literal, so the class closes at the first `]` and the
  pattern becomes "`}` or `\`, then `)`, then `]`" — which matches nothing, ever.
  It does not error; it reports zero and exits 1, which reads exactly like a
  clean sweep. That is how the `src/` half of this rule's own sweep was
  certified clean while `TransactionBehavior.cs` still held a violation. Put
  the `]` first — `[]})]` — and it means the same thing in both.

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
  and the two compose rather than compete: a call whose last argument is a
  lambda breaks at its **own** parenthesis, which puts the lambda on a line of
  its own, and never after the `=>`.

  This sentence used to say the opposite, naming
  `.Send(queue, ctx => new CancelOrder(…))` as a break after `ctx =>`. It was
  written before the lambda rule above and survived it, so for one branch the
  file prescribed a form it also forbade — and §9.6, which the sentence
  described, had ten instances of it. A rule added beside an older one has to
  be read against it, not only against the code it was written for.
- **A multi-line condition takes trailing operators, four-space continuations,
  and braces on its body.** The braces are what make four safe: without them the
  last `&&` line and the body sit in the same column and the reader cannot see
  where the condition stops.

  ```csharp
  if (!command.IsSystemInitiated &&
      (!currentUser.IsAuthenticated ||
          (order.CustomerId.Value != currentUser.Id &&
              !currentUser.HasPermission("orders:admin"))))
  {
      return Result.Failure(OrderErrors.NotFound);
  }
  ```

  This is the **one exception to the braces rule above** — a single statement may
  omit braces, unless the condition it hangs off is wrapped. Prefer not to wrap
  at all; joining is the better fix, and it has already been applied everywhere
  it fits. The block above is the corpus: one wrapped header, in §11.4, kept
  because an ownership check that fails closed does not join inside 120. A
  second one appearing is a signal to join, not a precedent.

  **A parenthesised group that breaks indents a further four**, so nesting depth
  is visible — the same rule the SQL section states for a broken `OR` group, and
  for the same reason. Never align under the opening bracket: continuations
  indent four here as everywhere else. The check above gained its second level
  when the guard was rewritten to fail closed; the earlier form led with
  `currentUser.IsAuthenticated &&`, which read as a guard and behaved as an
  exemption, admitting every caller that arrived with no principal at all.
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
  `[.. assemblies]` already had it. There are no LINQ `.ToArray()` or
  `.ToList()` calls left in the corpus, and a new one is a site this rule
  missed:

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

  **A `ToArray` that is not a sequence materialisation is outside this rule**,
  and stating that is cheaper than defending it again. `MemoryStream.ToArray()`
  is a stream accessor — the type implements no `IEnumerable`, so `[.. buffer]`
  does not compile at all (CS9212, checked by compiling it). The rule is about
  the *terminal LINQ operator*, where the spread and the call are two spellings
  of one thing and only one of them leads with the target. Where they are not
  two spellings of one thing there is nothing to prefer.

  This was found by a Copilot review reading "there are no `.ToArray()` calls
  left in the corpus" as the test it literally is — a grep — rather than as the
  rule it means. That reading is fair, which is why the sentence was narrowed
  rather than the finding merely rejected: **a rule whose stated test is a
  string match will be enforced as one**, by a reviewer or by whoever greps
  next.
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
  (currently ADR-021) and keep the
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
- **Follow the delivery plan's order.** Appendix C sequences 27 PRs with
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
| `/ship` | Run the three below in sequence — the first of them forking the PR's own worktree where it can, and saying so when it cannot — resuming where a previous run stopped; once the PR is open, loop `/review-branch` (run by Grok) and `/review-grok` until two consecutive passes leave no `suggestions.md` — or until a Grok usage-limit skip hands over early, owing Grok a later re-entry — then loop a requested Copilot review and `/review-copilot` until one review lands with no new findings and no unresolved threads |
| `/branch` | Start a correctly named branch — **in a sibling worktree** the session moves into, from a clean `main`; in place when the tree is dirty (carrying the work off `main`) or the parent is not writable |
| `/commit` | Split the working tree into semantic commits with arguing bodies |
| `/pr` | Open a PR in the house body form |
| `/review-copilot` | Triage Copilot's PR comments — verify each before acting, then close every thread with a `done` or `rejected` marker and resolve it |
| `/review-grok` | Triage an external review into a resolution record |
| `/review-branch` | Review the branch (or working tree) against `main` for contradictions; writes `suggestions.md` and rechecks it on the next run |
| `/security-sweep` | Loop a defensive security audit up to seven rounds in a throwaway worktree, filing a GitHub issue per confirmed medium-or-above finding, until a round surfaces nothing new |
| `/bug-sweep` | The same loop aimed at defects rather than vulnerabilities — logic and execution bugs, filed at **critical or high** only, confirmed by reading because the grant runs no build |

**The two sweeps are one shape asking two questions, and the split is by what
makes a finding rather than by where it looks.** `/security-sweep` files what an
attacker can reach; `/bug-sweep` files what is wrong on its own terms — an
inverted condition, a guard that admits, a retry that double-applies, a test a
do-nothing implementation would satisfy. Both fork a detached worktree, verify
every subagent claim before filing, de-duplicate against the whole issue set,
never fail open, and file without fixing. Three things genuinely differ: the
threshold (critical-or-high against medium-or-above, because a latent
vulnerability is a liability the moment it exists where a latent defect on an
unreachable path is a note), the fan-out cut (six areas by tree, partitioning
the repository rather than sampling it, against
security's three), and what confirmation can mean.

**That last one is the interesting one: `/bug-sweep` executes none of the
snapshot it audits, and its grant withholds a build deliberately.** Its shell
reaches `mktemp`, the two worktree helpers and `gh` — the worktree and the issue
tracker, never the tree's own build. **The snapshot, not "the code it audits":**
the tooling row covers `.claude/**`, so the two helpers are audited *and* run,
and the honest claim is that they execute from the caller's checkout and never
from the pinned worktree. That they are trustworthy is a separate assumption,
resting on the `Edit(.claude/scripts/**)` deny and on review — the one `/ship`
and `/branch` already make of the same files. The reason for withholding the
build is the one that shapes the agent profile: building a tree executes it —
MSBuild targets, source generators, analysers, and under `dotnet test` the
tree's own test code — and
the audited repository is prompt-injection input, so a build grant hands that
input arbitrary code execution on the host. The suite also needs Docker. So a
defect claim there is confirmed by reading, the issue body says so, and the
class of bug only execution catches is named as the residual rather than
papered over.

**The teardown argument that used to sit here was false, and the disproof is
worth keeping.** It said a build would leave `bin/` and `obj/` behind and trip
the shared teardown's own guard. `.gitignore` carries `[Bb]in/` and `[Oo]bj/`,
so `git status --porcelain` is empty with them present and `git worktree
remove` takes the worktree without complaint — verified by running it, against
a control file that was genuinely untracked and did produce *contains modified
or untracked files*. Copilot found it after four sites had repeated it. The
guard is still real and still catches scratch written inside a sweep's
worktree, which is the thing it was written for; it was never a reason to
withhold a build.

**Both sweeps' worktrees carry the `secsweep-` prefix, and the second one is
borrowing.** `git-worktree-detach.sh` and `git-worktree-drop.sh` refuse any path
that is not `secsweep-` plus six characters under the canonical temp
root — the shape check that stops a poisoned finding from naming a sibling PR
worktree and having it deleted. Those helpers are `Edit`-denied to a command
session, so `/bug-sweep` satisfies the shape that exists rather than widening
it. The accepted path set is unchanged and `mktemp -d` names are unique, so
nothing is less safe; what is lost is attribution, since a stray temp directory
no longer says which sweep left it.

**Not *directly* under the root, which both helpers' comments claimed for
longer than it was true.** A bash `case` pattern does no pathname expansion, so
`?` matches `/` as happily as any other character, and
`"$tmproot"/secsweep-??????` accepts `$tmproot/secsweep-a/bbbb` as well as
`$tmproot/secsweep-abc123` — run through a `case` rather than reasoned about,
with the wrong length, the wrong prefix and the wrong root all correctly
refused. Prefix and length hold; direct-childness does not. It predates
`/bug-sweep`, so nothing about a second caller made it worse, and the fix is
one line in each helper: compare `dirname "$resolved"` against `$tmproot` and
match the basename alone. It is owed together with the `mktemp` narrowing
below, both needing the same deny lifted.

**Both helpers' header comments were retitled for "a sweep" in the same PR**,
because they had named `/security-sweep` as their only caller — "and nothing
else", "the throwaway worktree `/security-sweep` created", and a path check
argued from "only **this command's** own `mktemp -d`" — which is exactly the
incomplete reconciliation the four prose sites above were fixed for. That edit
needed `Edit(.claude/scripts/**)` lifted and put back afterwards, since a
session that removes its own restriction makes the control a fiction; the
lifting is the repo owner's call and was taken as one. Comments only: the
`secsweep-??????` shape check, `--detach`, and the absent `-f` are byte
identical, verified with `bash -n` and a non-comment diff that came back empty.

**The prefix itself is still `secsweep-`, and that residual stands.** Renaming
it is a wider change than a comment — it is the one literal both helpers match
on, so it has to move in both files and in both callers' `mktemp -d` at once,
and getting it half-done leaves a sweep unable to fork or unable to tear down.
The detach helper now says in place that the prefix is historical and shared,
which is the part a reader needs; the attribution cost is unchanged and small.

**A PR gets its own worktree by default, and `/branch` is where it comes
from.** From a clean `main` with a writable parent — the ordinary case — the
new branch is cut into a sibling directory (`../ashamray-<slug>`, the shape
`ashamray-groklimit` is already on disk in) and the session moves into it, so
the whole of `/ship` runs there and this checkout stays on `main`. The two
paragraphs below name the cases where it cannot, and in both of them the branch
is made in place and the command says so. Outside the
repository tree is the load-bearing half rather than the naming: a worktree
under `.claude/worktrees/` would be untracked content inside the checkout,
which puts it in front of every `git status` the chain reads and inside
`grok-review.sh`'s clean-tree refusal. A sibling needs no `.gitignore` entry
because there is nothing to ignore.

**A sweep's worktree is not this one, and none is another's precedent.** The
sweeps' — `/security-sweep`'s and `/bug-sweep`'s alike — are detached, live
under `mktemp -d`, carry no branch and are removed at the end, and they refuse a
sibling *by name*, because a root-level or container layout has no writable
parent, "both of which this repo runs under". This one holds a branch that a PR,
two review loops and a person come back to, so it wants a stable named
directory. The commands are answering different questions; making one match the
other would break whichever was changed. What the sweeps' argument does carry
across is the precondition: where the parent is not writable there is no sibling
to create, so `/branch` names that case and branches in place rather than
failing mid-`/ship`.

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

**A branch that took either in-place path does not get a worktree later from
`/branch`** — the two differ in their reason and not in this consequence — and
the file says so rather than offering a re-entry that lands elsewhere: the
command stops on an existing branch, refuses a name already taken, and cuts
from `origin/main`, which would not carry the commits. Attaching one afterwards
is manual — `git switch main` in this checkout, then `git worktree add
../ashamray-<slug> <branch>`, then `EnterWorktree` — and the first of those is
why it is manual, since a branch cannot be checked out in two worktrees at
once.

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
it read** and with no `review_requested` event newer than it. A resumed `/ship`
checks both and does not re-request when they hold — where the Grok half must
re-enter, because `suggestions.md` is absent both before the first review and
after a clean one and the two states are indistinguishable. The review says
which commit it read; a missing file never could.

**The newer-request half is what keeps the oid from being a trap.** A run
interrupted between requesting a round and its landing leaves the previous
clean review satisfying every other condition — same head, no threads, nothing
suppressed — so on the oid alone a resume would declare all-resolved with a
review it has never read still in flight. Copilot's own first review of PR #27
found that, against the paragraph introducing the rule.

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

**A helper is also the answer when a git grant is wider than the operation it
buys**, and `/branch` now has three of them: `git-switch-existing.sh`,
`git-worktree-fork.sh` and `git-branch-create.sh`. Each replaced a raw grant
that reached past the deny list, and each was confirmed by running the
offending form rather than reasoning about it:

| Raw grant | What it also bought |
|---|---|
| `Bash(git switch:*)` | `--discard-changes` and `-C` — and the flags **combine**, so `git switch -fC <name> <start>` defeats any `Bash(git switch -C:*)` deny |
| `Bash(git worktree add:*)` | `-B`, which resets an existing branch rather than creating one |
| `Bash(git checkout -b:*)` | the trailing flag — `git checkout -b <name> -f origin/main` is accepted, discarding tracked modifications |

All three are the operations `git reset --hard`, `git clean` and
`git branch -D/-M` are denied for, reachable by another spelling. **A prefix
rule cannot exclude a flag** — the refspec argument the push rules already
make — so each helper fixes the whole command and shape-checks every argument.
**No caller-controlled flag can reach git**, which is not the same as passing
none: `git-switch-existing.sh` and `git-worktree-drop.sh` really do pass none,
while `git-worktree-fork.sh`, `git-branch-create.sh` and
`git-worktree-detach.sh` embed the flags the operation requires —
`--no-track`, `-b`, `--detach` — decided in the file rather than by whoever
calls it. That distinction is the point of the pattern and worth keeping in the
summary, because the two are easy to collapse into a claim that is false of
most of them.

**What each one refuses differs, and reading "create only" across all three
breaks the recovery path.** `git-worktree-fork.sh` and `git-branch-create.sh`
create only: a name that already exists is refused, so a reset is not reachable
by passing one. `git-switch-existing.sh` is the mirror image — it **requires**
the branch to exist and only switches — which is exactly what the failed-fork
recovery needs, since `git worktree add -b` leaves the branch behind when the
directory fails. An agent that took the summary for all three would refuse that
recovery, or retry the create helper and hit *branch already exists*.

Copilot raised the first two against PR #27; the third was found by grepping
for the same shape, which is the rule that says one site is never the only
site. The sweeps' two — `git-worktree-detach.sh` and `git-worktree-drop.sh`,
shared by `/security-sweep` and `/bug-sweep` — followed for the same reason:
`-B` resets a branch, and `-f` on a removal defeats the refusal those commands'
teardown uses as its guard. Their `secsweep-??????` path check is now
load-bearing for two callers rather than one, which is why widening it is a
decision about both and not a rename.

**The same review then found the holes in the grants that were left raw**, and
they were in the command frontmatter rather than in `.claude/settings.json` —
worth knowing, because the global file had it right all along. Six commands
carried `Bash(git branch:*)`, which admits `git branch -fd <name>`: force and
delete behind a spelling the `-d`/`-D`/`--delete` denies do not match, verified
by deleting a branch with it. Two carried `Bash(git reset HEAD:*)`, which
admits `git reset HEAD --hard` — the `--hard` deny matches the *other* word
order, and the reset was verified to discard a tracked modification.
**A command's frontmatter is a grant like any other, and it is the one nobody
reads twice.**

The branch grant narrows cleanly to the read-only forms — `git branch
--list:*`, `--show-current` and `-a`, with `--list` checked against a trailing
`-D` and refusing it. **The reset grant does not narrow, and the attempt is
the sharpest lesson here.** It was first "fixed" to `Bash(git reset HEAD --:*)`
on the reasoning that `--` turns a later flag into a pathspec. That is true of
*git* and irrelevant to the *rule*: a permission rule is a prefix match, and
`git reset HEAD --hard` starts with `git reset HEAD --`, so the narrowed grant
admitted the exact command it was written to exclude — while the commit message
and this file both said the hole was closed. The git behaviour was verified and
the matching was not.

**A prefix rule cannot say "and then a space", so anything whose safety depends
on what follows a token needs a helper**, not a cleverer pattern.
`git-unstage.sh` writes the separator itself and refuses any argument beginning
with `-`.

**`sandbox/` is on that list for a reason worth keeping.** It arrived guarded
by nothing, and a review caught it: the Dockerfile is a *build input to the
security boundary*, so a session able to edit it can add an entrypoint that
reads the credentials the following `docker run` mounts in. Guarding the
runner and leaving its image editable protects the door and not the wall —
and the reasoning that put the image in `.claude/` at all was the reasoning
that should have guarded it, since a helper the loop invokes by name is
exactly what the Dockerfile had become.
