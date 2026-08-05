# Backend Architecture Blueprint

**A reference architecture for ASP.NET Core microservices using DDD, CQRS and TDD.**

| | |
|---|---|
| **Status** | Reference blueprint — adapt, don't copy wholesale |
| **Target runtime** | .NET 10 (LTS), C# 14 |
| **Last reviewed** | 2026-08-02 |
| **Availability figures** | Illustrative. The arithmetic of compounding is the point; the inputs are round numbers, not measurements |
| **Sample domain** | E-commerce (illustrative only) |
| **Revision** | Revised across successive design reviews. Load-bearing corrections: transaction failure and rollback semantics, outbox type and payload stability, single message identity, business counters as claims rather than calls, public URL shape. Originally merged with a parallel design, which contributed the delivery plan, composition-root gate, Redis eviction isolation, dual DB identities, cursor pagination and hop budget |

---

## Table of contents

1. [Purpose and how to read this](#1-purpose-and-how-to-read-this)
2. [Architecture at a glance](#2-architecture-at-a-glance)
3. [Bounded contexts and service decomposition](#3-bounded-contexts-and-service-decomposition)
4. [Solution and folder structure](#4-solution-and-folder-structure)
5. [Tactical DDD](#5-tactical-ddd)
6. [CQRS](#6-cqrs)
7. [Persistence](#7-persistence)
8. [Caching with Redis](#8-caching-with-redis)
9. [Messaging](#9-messaging)
10. [API Gateway](#10-api-gateway)
11. [Identity and authorization](#11-identity-and-authorization)
12. [Test strategy and TDD](#12-test-strategy-and-tdd)
13. [Observability](#13-observability)
14. [Local development](#14-local-development)
15. [CI/CD and deployment](#15-cicd-and-deployment)
16. [Appendix A — Architecture decision records](#appendix-a--architecture-decision-records)
17. [Appendix B — Dependency licence register](#appendix-b--dependency-licence-register)
18. [Appendix C — Delivery plan](#appendix-c--delivery-plan)
19. [Appendix D — Type inventory](#appendix-d--type-inventory)

---

## 1. Purpose and how to read this

This document describes how to build a distributed backend on ASP.NET Core using
Domain-Driven Design for the modelling, CQRS for the application layer, and
test-driven development as the working method. It uses an e-commerce domain for
its worked examples because the concepts — orders, stock, payment — are widely
understood, not because the architecture is e-commerce-specific.

### 1.1 Read this first: should you build microservices at all?

Microservices are an organisational solution with a technical implementation.
They exist to let independent teams deploy independently. If you do not have
that problem, you are buying the costs without the benefit.

The costs are real and permanent:

- Every in-process method call that becomes a network call gains partial
  failure, latency, retries, and serialisation.
- Transactions across services do not exist. You get sagas and compensation.
- Debugging requires distributed tracing before it becomes possible at all.
- Local development requires orchestration before anyone can run anything.
- Refactoring a boundary means a coordinated multi-repo, multi-deploy change.

**Default to a modular monolith.** Build the same bounded contexts, the same
aggregates, the same command handlers, the same integration events — but in one
process, with events dispatched in-memory and enforced module boundaries. Every
pattern in sections 5, 6, 7, 8 and 12 of this document applies unchanged. When a
module genuinely needs independent deployment or independent scaling, extract it:
the seams are already cut.

The rest of this document assumes you have made that decision deliberately.

### 1.2 What this document is not

It is not a tutorial, and it does not aim to be complete enough to compile.
Code samples are representative — they show the shape of a pattern, and they
omit null checks, logging and error paths that would obscure the point.

It is also not a menu. The patterns here compose; picking three of them at
random produces something worse than picking none.

### 1.3 Conventions used here

> **Decision** — a choice this blueprint makes, with the reasoning. Recorded in
> full in [Appendix A](#appendix-a--architecture-decision-records).

> **Trap** — a mistake common enough that it is worth naming explicitly.

---

## 2. Architecture at a glance

### 2.1 System context

```mermaid
graph TB
    Customer([Customer])
    Admin([Back-office operator])
    Browser[Web / mobile client]
    Gateway[API Gateway<br/>YARP]
    Platform[Commerce Platform<br/>6 domain services + BFF]
    PSP[(Payment provider)]
    Carrier[(Shipping carrier)]
    Email[(Email / SMS provider)]

    Customer --> Browser
    Admin --> Browser
    Browser -->|HTTPS + JWT| Gateway
    Gateway --> Platform
    Platform -->|HTTPS| PSP
    Platform -->|HTTPS| Carrier
    Platform -->|HTTPS| Email
```

### 2.2 Container view

```mermaid
graph TB
    subgraph Edge
        GW[API Gateway<br/>YARP]
        BFF[Web BFF<br/>aggregation only]
        IDP[Keycloak<br/>OIDC provider]
    end

    subgraph Services
        CAT[Catalog API]
        ORD[Ordering API]
        INV[Inventory API]
        PAY[Payments API]
        SHP[Shipping worker]
        NOT[Notifications worker]
    end

    subgraph Data
        SQL[(SQL Server<br/>database per service)]
        RC[(Redis cache<br/>allkeys-lru)]
        RK[(Redis coordination<br/>noeviction)]
        MQ[[RabbitMQ]]
    end

    subgraph Telemetry
        OTEL[OTel Collector]
        GRAF[Grafana / Tempo / Prometheus]
    end

    GW --> CAT
    GW --> ORD
    GW --> INV
    GW --> PAY
    GW --> BFF

    BFF -->|gRPC, the one sync hop| CAT

    GW -.->|validate| IDP
    BFF -.->|client credentials| IDP
    CAT -.-> IDP
    ORD -.-> IDP
    INV -.-> IDP
    PAY -.-> IDP

    CAT --> SQL
    ORD --> SQL
    INV --> SQL
    PAY --> SQL
    SHP --> SQL
    NOT --> SQL

    CAT --> RC
    ORD --> RC
    INV --> RC
    CAT --> RK
    ORD --> RK
    INV --> RK
    PAY --> RK

    CAT <--> MQ
    ORD <--> MQ
    INV <--> MQ
    PAY <--> MQ
    SHP <--> MQ
    NOT <--> MQ

    CAT --> OTEL
    ORD --> OTEL
    INV --> OTEL
    PAY --> OTEL
    SHP --> OTEL
    NOT --> OTEL
    GW  --> OTEL
    OTEL --> GRAF
```

Three details in this picture are decisions, not layout:

- **Two Redis instances, not one.** Their eviction policies are incompatible
  (§8.1) — a shared instance under `allkeys-lru` will drop a held lock or a
  revoked token with no error. Payments reaches only the coordination instance:
  it takes idempotency keys (§8.5) and caches nothing.
- **Every service validates its own token**, not just the gateway. §11.2 treats
  the network as hostile, so a request arriving by any other path is still
  authenticated. A diagram showing only `GW -.-> IDP` would depict exactly the
  arrangement that section forbids relying on.
- **Migrators are absent deliberately.** They are Jobs, not long-running
  containers — but each holds a *second* SQL identity with DDL rights (§7.1),
  which is the part worth knowing that this view cannot show.

### 2.3 Principles

These are the load-bearing rules. Everything else in the document follows from
them.

| # | Principle | Consequence if violated |
|---|---|---|
| 1 | A service owns its data exclusively. No other service touches its database. | You have a distributed monolith with worse latency than a monolith. |
| 2 | One transaction never spans two services. | You need distributed transactions, which do not work at scale. |
| 3 | One transaction never spans two aggregates. Asserted at the transaction boundary (§6.3). | Your aggregate boundaries are wrong; find the real ones. |
| 4 | Services communicate through events by default, synchronously only when a user is waiting on the answer. | Availability multiplies downwards: five services at 99.9% chained gives 99.5%. |
| 5 | The domain layer has no dependency on anything infrastructural. | You cannot unit test the domain, so you stop testing it. |
| 6 | Every integration event is idempotent on the consumer side. | At-least-once delivery corrupts data on the first redelivery. |
| 7 | Contracts are versioned and additive. | Any deploy becomes a lockstep deploy of everything. |

### 2.4 The consistency model

This is the single biggest adjustment for teams arriving from a monolith:

- **Inside an aggregate:** strongly consistent, enforced by a database
  transaction, always valid.
- **Between aggregates in one service:** eventually consistent, via domain
  events processed after commit.
- **Between services:** eventually consistent, via integration events and the
  outbox. Typical lag is milliseconds; the design must tolerate seconds.

Every screen, API and business rule must be designed with the knowledge that a
read may be stale. Where a rule genuinely cannot tolerate staleness, that is
strong evidence the data belongs inside a single aggregate.

> **Trap — the distributed monolith.** If deploying service A requires
> simultaneously deploying service B, you have not built microservices. You have
> built a monolith with network calls in the middle. The most common causes are a
> shared database, a shared "Common.Entities" assembly, and synchronous call
> chains three services deep.

---

## 3. Bounded contexts and service decomposition

### 3.1 The context map

A bounded context is a boundary within which a term has one unambiguous meaning.
"Product" in Catalog is a rich marketing object with descriptions, images and
categories. "Product" in Inventory is a SKU and a number. These are not the same
concept and must not share a class.

```mermaid
graph LR
    CAT[Catalog<br/><i>Supplier</i>]
    ORD[Ordering<br/><i>Core domain</i>]
    INV[Inventory<br/><i>Supplier</i>]
    PAY[Payments<br/><i>ACL over PSP</i>]
    SHP[Shipping<br/><i>Conformist</i>]
    NOT[Notifications<br/><i>Generic</i>]

    CAT -->|product + price events| ORD
    ORD -->|ReserveStock<br/>ReleaseStock| INV
    INV -->|StockReserved / Failed<br/>StockReleased| ORD
    ORD -->|AuthorisePayment| PAY
    PAY -->|PaymentAuthorised / Declined| ORD
    ORD -->|OrderConfirmed| SHP
    SHP -->|ShipmentDispatched| ORD
    ORD -->|order events| NOT
    INV -->|StockLevelChanged| CAT
    SHP -->|shipment events| NOT
```

**Every collaboration is a round trip, and the return leg is an event.**
Ordering sends a command and then waits — it does not call and block. The three
return edges are what the fulfilment saga (§9.6) transitions on, and drawing
only the outbound half would depict the request/response topology ADR-002 and
ADR-017 exist to reject.

| Context | Type | Why it is separate |
|---|---|---|
| **Ordering** | Core domain | This is where the business logic actually lives. Invest here. |
| **Catalog** | Supporting | Different read/write ratio (1000:1), different scaling, different team cadence. |
| **Inventory** | Supporting | Different consistency requirements — stock is the one place contention is real. |
| **Payments** | Supporting | Isolates a volatile third-party API behind an anti-corruption layer. Compliance boundary. |
| **Shipping** | Supporting | Conformist to carrier APIs; changes on the carrier's schedule, not yours. |
| **Notifications** | Generic | Not a differentiator. Would be replaced by an off-the-shelf product without regret. |

### 3.2 Service responsibilities

Each service is described by what it owns, what it publishes, and what it
listens to. This table is the contract summary for the whole platform.

Events are published to anyone interested; commands are sent to exactly one
owner (§9.6). The columns are separated because that distinction determines
whether a message uses `Publish` or `Send`, and getting it wrong means a second
subscriber silently executing your business commands.

| Service | Owns | Publishes (events) | Consumes (events) | Accepts (commands) |
|---|---|---|---|---|
| **Catalog** | Product, Category, Price | `ProductPublished`, `PriceChanged`, `ProductDiscontinued` | `StockLevelChanged` | — |
| **Ordering** | Order, OrderLine, the fulfilment saga | `OrderPlaced`, `OrderConfirmed`, `OrderCancelled` | `OrderPlaced` (its own — the saga starts on it), `ProductPublished`, `PriceChanged`, `ProductDiscontinued`, `StockReserved`, `StockReservationFailed`, `StockReleased`, `PaymentAuthorised`, `PaymentDeclined`, `ShipmentDispatched` | `CancelOrder`, `ConfirmOrder`, `MarkOrderShipped`, `FlagOrderForReview` |
| **Inventory** | StockItem, Reservation | `StockReserved`, `StockReservationFailed`, `StockReleased`, `StockLevelChanged` | `OrderCancelled`, `ShipmentDispatched` | `ReserveStock`, `ReleaseStock` |
| **Payments** | PaymentIntent, Refund | `PaymentAuthorised`, `PaymentDeclined`, `PaymentRefunded` | `OrderCancelled` | `AuthorisePayment` |
| **Shipping** | Shipment, TrackingEvent | `ShipmentDispatched`, `ShipmentDelivered` | `OrderConfirmed` | — |
| **Notifications** | NotificationLog | — | `OrderPlaced`, `OrderConfirmed`, `OrderCancelled`, `PaymentDeclined`, `PaymentRefunded`, `ShipmentDispatched`, `ShipmentDelivered` | — |

Every cell enumerates. "All customer-relevant events" would be shorter and is
not a contract: it cannot be versioned, reviewed, or checked against what
publishers actually emit, and Notifications is the service the delivery plan
says to build first (Appendix C.1). A subscription list that grows silently is
how a consumer ends up bound to a type nobody meant to give it.

**The table closes in both directions, and the second one is easier to lose.**
Every name in a Consumes cell appears in exactly one Publishes cell — an event
nobody emits is a consumer waiting for ever. Less obviously, every name in a
Publishes cell appears in at least one Consumes cell: a published event with no
reader is a contract the platform is committed to versioning and nobody is
asking for, and it looks identical to the case where its consumer was
forgotten. `PaymentRefunded` was that row until Notifications claimed it. Both
directions are two set comparisons over this table, which is worth automating
precisely because neither failure produces a symptom.

The command column is entirely the saga's doing: Ordering's `OrderFulfilmentSaga`
is the only thing that sends commands, and each one lands on the queue of the
service that owns the decision. `CancelOrder` and `ConfirmOrder` route back to
Ordering itself — the saga coordinates, the aggregate decides (§9.6).

Note the shapes this produces. **Shipping** and **Notifications** expose no
public write API at all — they are pure event consumers. **Notifications** is
the simplest possible service and is a good first one to build, because it
exercises the entire messaging and observability stack while containing almost
no domain logic.

### 3.3 Rules for creating a new service

Require **all four** before splitting:

1. It owns data no other service needs transactional access to.
2. It has a genuinely different scaling, availability or compliance profile.
3. A team can own it end-to-end.
4. Its interface can be expressed as events plus a small query API.

If only some hold, make it a module inside an existing service. Merging two
services later is straightforward; splitting one that shares a database is not.

### 3.4 What does not become a service

- **Shared business rules.** They belong in whichever context owns the decision.
- **A "common" or "core" service.** Everything depending on one service is a
  single point of failure with a coordination bottleneck attached.
- **An entity.** "UserService, OrderService, ProductService" is a database
  schema drawn as boxes, not a decomposition. Split by capability.
- **A database access layer.** If it has no behaviour, it is not a service.

---

## 4. Solution and folder structure

### 4.1 Repository layout

A single repository. Independent deployability comes from the CI pipeline
building and releasing services separately, not from separate git repositories.
A monorepo makes cross-cutting changes and contract updates atomic and reviewable.

```
/
├── src/
│   ├── BuildingBlocks/
│   │   ├── Common.Domain/              Entity, AggregateRoot, ValueObject, IDomainEvent
│   │   ├── Common.Application/         Dispatcher, pipeline behaviours, Result<T>
│   │   ├── Common.Infrastructure/      Outbox, inbox, EF conventions, Redis
│   │   ├── Common.Web/                 Host defaults: OTel, health, auth, ProblemDetails,
│   │   │                               resilience, versioning. Referenced by every host.
│   │   │                               (Aspire's template calls this ServiceDefaults.)
│   │   └── Common.Contracts/           Integration event DTOs — the ONLY shared types
│   │
│   ├── Gateway/
│   │   └── Gateway.Api/                YARP host
│   │
│   ├── BFF/
│   │   └── Web.Bff/                    Aggregation for the web client (§10.1).
│   │                                   The ONLY host that calls a service
│   │                                   synchronously (§9.7), and therefore the
│   │                                   only one with client credentials (§11.5)
│   │
│   └── Services/
│       ├── Catalog/
│       │   ├── Catalog.Domain/
│       │   ├── Catalog.Application/
│       │   ├── Catalog.Infrastructure/
│       │   ├── Catalog.Migrator/
│       │   └── Catalog.Api/
│       ├── Ordering/
│       │   ├── Ordering.Domain/
│       │   ├── Ordering.Application/
│       │   │   └── DependencyInjection.cs   AddOrderingApplication()
│       │   ├── Ordering.Infrastructure/
│       │   │   └── DependencyInjection.cs   AddOrderingInfrastructure(config)
│       │   ├── Ordering.Migrator/           Migration job host (§7.4)
│       │   └── Ordering.Api/
│       │       └── Program.cs               The ONLY composition root (§4.2)
│       ├── Inventory/                  (same five projects)
│       ├── Payments/                   (same five projects)
│       ├── Shipping/                   (Domain, Application, Infrastructure, Migrator, Worker)
│       └── Notifications/              (Application, Infrastructure, Migrator, Worker)
│
├── tests/
│   ├── Catalog.Domain.Tests/
│   ├── Catalog.Application.Tests/
│   ├── Catalog.Api.Tests/
│   ├── Catalog.TestSupport/            ServiceFixture, the test auth scheme and the
│   │                                   data builders (§12.4). Not a test project —
│   │                                   referenced by the two above, which each need
│   │                                   containers and cannot reference each other.
│   ├── ...
│   └── Platform.IntegrationTests/      Contract tests (§12.6) — the only suite
│                                       that references every service
│
├── deploy/
│   ├── compose/                        docker-compose.yml + overrides
│   ├── helm/                           Chart per service + umbrella chart
│   └── k8s/                            Raw manifests where Helm is overkill
│
├── docs/
│   ├── backend-architecture.md         This document
│   └── adr/                            One file per decision
│
├── Directory.Build.props               Shared MSBuild settings
├── Directory.Packages.props            Central package version management
└── Platform.slnx
```

`.slnx` is the XML solution format, supported by the SDK from .NET 9 and by
Visual Studio 2022 17.13 onward. The `global.json` pin below already puts every
machine above that floor, which is the only reason a one-line note suffices
rather than shipping a `.sln` alongside it.

**Every service has a `*.Migrator`, because every service owns a database**
(§7.1) and ADR-007 forbids migrating at application startup. That includes
Shipping and Notifications, which expose no public API but still own schemas —
`Shipment`/`TrackingEvent` and `NotificationLog` respectively (§3.2).

It is also why §15.2 builds **two images per service** rather than one, and why
the migrator gets its own connection string, its own SQL login and its own
Kubernetes Job. A service without a migrator has no way to create its schema
that this architecture permits.

### 4.2 The dependency rule

Inside a service, dependencies point inward only:

```
Api ──────────► Application ──────────► Domain
 │                                        ▲
 └──► Infrastructure ─────────────────────┘
```

| Project | May reference | Must never reference |
|---|---|---|
| `*.Domain` | `Common.Domain` and nothing else | EF Core, ASP.NET, Redis, MassTransit, `System.Text.Json` |
| `*.Application` | its own Domain, `Common.Application`, `Common.Contracts` | EF Core, ASP.NET, any concrete infrastructure |
| `*.Infrastructure` | Domain, Application, any package | another service's projects |
| `*.Api` | Application, Infrastructure (**composition root only**) | another service's projects |

`*.Domain` having no third-party dependencies is what makes domain tests
instant and mock-free. It is worth defending. Enforce it with an architecture
test rather than a code review convention:

```csharp
[Fact]
public void Domain_has_no_infrastructure_dependencies()
{
    var forbidden = new[] { "Microsoft.EntityFrameworkCore", "MassTransit",
                            "StackExchange.Redis", "Microsoft.AspNetCore" };

    var referenced = typeof(Order).Assembly
        .GetReferencedAssemblies()
        .Select(a => a.Name!);

    referenced.ShouldNotContain(name => forbidden.Any(name.StartsWith));
}
```

#### The composition-root rule

`*.Api` may reference Infrastructure, but only in one place. Stated normatively:

- **Only** `Program.cs` and host-level `*ServiceCollectionExtensions` may
  reference `*.Infrastructure` types.
- **Endpoints and controllers may not.** No `DbContext`, no concrete
  repository, no `IPublishEndpoint`, no `IConnectionMultiplexer` — Application
  and Domain contracts only.

Without this rule the dependency table is satisfied at project level while being
violated everywhere that matters, because "Api may reference Infrastructure"
silently licenses an endpoint to inject a `DbContext`.

```csharp
[Fact]
public void Endpoints_do_not_depend_on_infrastructure()
{
    var result = Types.InAssembly(typeof(OrdersEndpoints).Assembly)
        .That().ResideInNamespaceContaining(".Endpoints")
        .ShouldNot().HaveDependencyOn("Ordering.Infrastructure")
        .GetResult();

    result.IsSuccessful.ShouldBeTrue(
        $"leaked: {string.Join(", ", result.FailingTypeNames ?? [])}");
}
```

One more, for the rule §9.3 states in prose: application code publishes through
`IIntegrationEventPublisher` and the outbox, never through the bus directly.
The saga is the documented exception (§9.6) and it lives in Infrastructure, so
the boundary is checkable:

```csharp
[Fact]
public void Application_and_domain_do_not_reference_masstransit()
{
    // §9.3's must-not list. The saga may Send and Publish because MassTransit's
    // in-memory outbox holds those until the consume transaction commits — a
    // guarantee that exists on the consume pipeline and nowhere else. A handler
    // that copies the saga's style gets a dual write with no outbox behind it,
    // and it works in every test where the broker is up.
    foreach (var assembly in new[] { typeof(PlaceOrderHandler).Assembly,
                                     typeof(Order).Assembly })
        Types.InAssembly(assembly)
            .ShouldNot().HaveDependencyOn("MassTransit")
            .GetResult().IsSuccessful.ShouldBeTrue(assembly.GetName().Name);
}
```

If the namespace rule proves awkward to enforce, split the host into
`Ordering.Host` (composition, references Infrastructure) and `Ordering.Api`
(endpoints, does not) and let the project reference enforce it. That is the more
robust option; the single-project namespace rule is the lighter one.

**These tests are a CI gate from the first template commit**, not a later
addition. An architecture rule introduced after the violations exist is a
backlog item; one introduced before them is a constraint.

#### What the composition root composes

Each layer exposes exactly one registration method. `Program.cs` calls them and
does nothing else with Infrastructure — which is what makes the rule above
enforceable rather than aspirational, and what lets tests exercise the real
registration path (§6.2) instead of a hand-built container.

```csharp
// Ordering.Application/DependencyInjection.cs
public static IServiceCollection AddOrderingApplication(this IServiceCollection services)
{
    services.AddPluggableFrom(typeof(PlaceOrderCommand).Assembly);       // §6.2

    // The clock. TimeProvider is an abstract BCL class, not an ambient
    // service — ASP.NET Core does not register it, so without this line every
    // handler that takes one fails to resolve. §5.4's "pass time in as a
    // parameter" discipline runs through here, and FakeTimeProvider replaces
    // exactly this registration in tests (§12.7).
    services.AddSingleton(TimeProvider.System);

    // Application-layer metrics: OrderMetrics records domain quantities, so it
    // lives and registers here rather than in Infrastructure (§13.3).
    // RequestMetrics likewise — LoggingBehavior injects it, and the pipeline
    // is Application.
    //
    // Registration is not construction, and neither of these is reachable
    // without traffic: OrderMetrics waits for the projection to run (§6.6),
    // RequestMetrics for a dispatched request — which a health probe is not.
    // MetricsInitialiser (§13.6) forces both, and a test there asserts that
    // every registered *Metrics type is on its parameter list.
    services.AddSingleton<OrderMetrics>();
    services.AddSingleton<RequestMetrics>();                             // §13.3

    services.AddScoped<IDispatcher, Dispatcher>();
    services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
    services.AddScoped<IProjectionRegistry, ProjectionRegistry>();      // §7.5

    // Not an open generic, so the §6.2 scan cannot find it — one service, one
    // mapper, registered by hand. DomainEventDispatcher injects it, so a
    // missing line here fails ValidateOnBuild rather than failing silently.
    services.AddScoped<IIntegrationEventMapper, OrderingIntegrationEventMapper>();

    // Ordered, explicit, not scanned — registration order is pipeline order
    // (§6.3). Unregistered, nothing opens a transaction and no write persists.
    services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
    services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
    services.AddScoped(typeof(IPipelineBehavior<,>), typeof(IdempotencyBehavior<,>));
    services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));
    services.AddValidatorsFromAssemblyContaining<PlaceOrderValidator>();
    return services;
}
```

```csharp
// Ordering.Infrastructure/DependencyInjection.cs
public static IServiceCollection AddOrderingInfrastructure(
    this IServiceCollection services, IConfiguration configuration)
{
    services.AddDbContext<OrderingDbContext>(o =>
        o.UseSqlServer(configuration.GetConnectionString("Ordering"),   // runtime identity, §7.1
                       sql => sql.EnableRetryOnFailure()));

    // Projections, cache invalidators and command mappers live here, not in
    // Application — scanning only Application would skip them all (§6.2).
    services.AddPluggableFrom(typeof(OrderRepository).Assembly);

    services.AddScoped<IUnitOfWork, EfUnitOfWork>();                    // §6.3
    services.AddScoped<IDomainEventCollector, EfDomainEventCollector>(); // §7.5
    services.AddScoped<IIntegrationEventPublisher, OutboxPublisher>();   // §9.3
    services.AddScoped<IOrderRepository, OrderRepository>();
    services.AddSingleton<IDbConnectionFactory, SqlConnectionFactory>();

    // The outbox's persisted type names (§9.4). Singleton and built here, so a
    // duplicate name fails this host at startup rather than one message at
    // delivery. Two assemblies: contracts for the Broker lane, this service's
    // domain events for the Local one.
    //
    // Registered as a source rather than a finished map, because a test host
    // has to add its own event types (§12.4) and a map built by `new` in this
    // line leaves no way to. Adding to the source is not the same as replacing
    // the map: the production assemblies stay in the list, so a test still
    // cannot stage something the real host would reject.
    services.AddSingleton<MessageTypeSource>(_ => new MessageTypeSource(
        typeof(V1.OrderPlaced).Assembly, typeof(Order).Assembly));

    services.AddSingleton(sp =>
        new MessageTypeMap(sp.GetRequiredService<MessageTypeSource>().Assemblies));

    // Plain ports — not open generics, so the §6.2 scan does not see them and
    // each needs a line here. Omitting one fails at DI resolution on the first
    // request that needs it, not at startup — unless ValidateOnBuild is on.
    services.AddScoped<IProductPriceReader, ProjectedPriceReader>();      // §6.4

    // Scoped, and paired with the accessor it depends on — ASP.NET Core does
    // not register IHttpContextAccessor by default, so omitting the second
    // line fails ValidateOnBuild rather than at the first ownership check.
    services.AddHttpContextAccessor();
    services.AddScoped<ICurrentUser, HttpContextCurrentUser>();           // §11.4

    services.AddScoped<IIdempotencyStore, RedisIdempotencyStore>();       // §8.5

    // No ITokenCache, no ClientCredentialsHandler and no ServiceIdentityOptions
    // here. Ordering makes no synchronous outbound call — the price it needs
    // comes from a local projection (§6.4) and everything else it says goes
    // over the broker. The one host in this blueprint that calls a peer is the
    // BFF (§9.7), and outbound identity belongs to it (§11.5).

    // Registered by type, not by factory: the integration-test fixture locates
    // and removes this exact descriptor (§12.4).
    services.AddHostedService<OutboxDispatcher>();

    // Outbox metrics (§13.6) read the database, so they belong here.
    // OrderMetrics does not — it is an Application type (§13.3) and is
    // registered by AddOrderingApplication above.
    services.AddSingleton<IOutboxStats, OutboxStats>();
    services.AddSingleton<OutboxMetrics>();

    // The two delivery lags and the rejection counter (§13.3). Injected by
    // IntegrationEventConsumer<T> and CommandConsumer<,>, resolved from the
    // provider by ProjectionInvoker — all three live here. Forced at startup
    // for the same reason as OrderMetrics: a consumer is constructed when a
    // message arrives, so on a quiet service these instruments do not exist.
    services.AddSingleton<MessagingMetrics>();

    // Forces construction of the metrics singletons in both layers. Resolving
    // them is the whole job: nothing else injects OutboxMetrics, and an
    // observable gauge that is never constructed never reports.
    services.AddHostedService<MetricsInitialiser>();

    // Keyed connections (§8.1) and the HybridCache over the cache one (§8.2).
    // No service name passed: the key prefix comes from ApplicationName, the
    // single source §8.5 already uses for idempotency keys.
    services.AddRedisConnections(configuration);
    services.AddMassTransitMessaging(configuration);                     // §9.4

    // Readiness checks live here, not in Common.Web — they need connection
    // strings, which the shared host package does not have (§13.5).
    services.AddHealthChecks()
        .AddSqlServer(configuration.GetConnectionString("Ordering")!,
                      name: "sql", tags: ["ready"])
        .AddRedis(configuration.GetConnectionString("RedisCache")!,
                  name: "redis-cache", tags: ["ready"])
        .AddRedis(configuration.GetConnectionString("RedisCoordination")!,
                  name: "redis-coordination", tags: ["ready"])
        .AddRabbitMQ(name: "rabbitmq", tags: ["ready"])
        .AddCheck<OutboxBacklogHealthCheck>("outbox", tags: ["observe"]);

    return services;
}
```

```csharp
// Ordering.Api/Program.cs — the only file that may reference Infrastructure.
var builder = WebApplication.CreateBuilder(args);

// Refuse to start if any registered service has a dependency the container
// cannot satisfy, or if a singleton captures a scoped one. Both are otherwise
// discovered on the first request that happens to need them.
builder.Host.UseDefaultServiceProvider(o =>
{
    o.ValidateOnBuild = true;
    o.ValidateScopes  = true;
});

builder.AddCommonWebDefaults();                                     // §13.2
builder.Services.AddOrderingApplication();                          // §6.2
builder.Services.AddOrderingInfrastructure(builder.Configuration);  // above

// Ordering's permission policies (§11.4). Deliberately not inside either
// helper: Application knows nothing about HTTP, and Common.Web must not know
// Ordering's names. A policy named by an endpoint and registered nowhere
// throws on the first request that reaches it — never at startup.
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("orders:read",   p => p.RequireClaim("permission", "orders:read"))
    .AddPolicy("orders:write",  p => p.RequireClaim("permission", "orders:write"))
    .AddPolicy("orders:cancel", p => p.RequireClaim("permission", "orders:cancel"));

var app = builder.Build();

// Middleware order is behaviour, not formatting. Each line below depends on
// the ones above it, and getting it wrong fails silently rather than loudly.
app.UseExceptionHandler();        // §10.5 — outermost, so it catches faults in
                                  //          the middleware below it too
app.UseCorrelationId();           // §10.4 — above everything else that logs
app.UseAuthentication();          // §11.3 — populates HttpContext.User
app.UseAuthorization();           // §11.4 — evaluates the permission policies

app.MapCommonHealthEndpoints();   // §13.5 — anonymous; kubelet carries no token
app.MapOrderEndpoints();          // §11.4

app.Run();

// Top-level statements compile to an INTERNAL Program, which
// WebApplicationFactory<Program> cannot see from another assembly (§12.4).
// One line here rather than InternalsVisibleTo: it does not have to name the
// assembly that consumes it, and the consumer is Ordering.TestSupport rather
// than either of the test projects — which is the version people get wrong.
public partial class Program;
```

Five ordering constraints worth stating, because each one produces a defect
that no test catches by accident:

| Rule | What breaks otherwise |
|---|---|
| `UseCorrelationId` before everything that logs, `UseExceptionHandler` alone above it | Early log lines and traces have no correlation ID, so the one request you need to follow is the one you cannot. The handler is the deliberate exception — it has to be outermost to catch faults in the middleware below it, and it reaches the ID through `Request.Headers` rather than the log scope (§10.4) |
| `UseAuthentication` before `UseAuthorization` | `User` is unpopulated when policies evaluate; every authenticated request 403s |
| `UseAuthentication` before `UseRateLimiter` (gateway only) | Same empty `User`, but this one does not 403 — §10.3's per-user partition key silently degrades to per-IP, and everyone behind one NAT shares a single bucket |
| Both before endpoint mapping | `RequireAuthorization` has nothing to evaluate against |
| Health endpoints mapped **anonymous** | Probes 401, Kubernetes reads that as unhealthy, and the pod is killed in a loop |

Registration without middleware is the quiet failure mode here. `AddRateLimiter`
and `AddAuthentication` both succeed and do nothing if `UseRateLimiter` and
`UseAuthentication` are absent — no error, no warning, no failing test unless
one specifically asserts a 401.

The **gateway** has its own pipeline and is the only place rate limiting is
applied (§10.1); a service behind it does not call `UseRateLimiter`:

```csharp
// Gateway.Api/Program.cs
var builder = WebApplication.CreateBuilder(args);

builder.AddCommonWebDefaults();                                  // §13.2

// YARP is registered here and configured from the "ReverseProxy" section
// shown in §10.2. Without this, MapReverseProxy() throws at startup and the
// entire routing configuration is inert.
builder.Services.AddReverseProxy()
       .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddRateLimiter(/* §10.3 */);

// Every policy §10.2's routes name that Common.Web does not already register.
// "authenticated" comes from AddCommonWebDefaults; this one is the gateway's
// own, and it is a permission check rather than a role check for the reason
// §11.4 gives. A route naming a policy nobody registered does not fail closed:
// YARP rejects the route at config load and drops it, so that path 404s while
// every other route keeps serving.
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("inventory:admin", p => p.RequireClaim("permission", "inventory:admin"));

// Both of the following are conditional on the deployment shape, and each is
// REQUIRED once switched on. "Off" and "on but unconfigured" are different
// states: the first is a valid topology, the second is a silent defect.
var behindProxy = builder.Configuration.GetValue<bool>("Ingress:Enabled");
var corsEnabled = builder.Configuration.GetValue<bool>("Cors:Enabled");

if (behindProxy)
{
    // A load balancer or Ingress sits in front (§15.3), so
    // Connection.RemoteIpAddress is the proxy on every request. Without this
    // the rate limiter partitions all anonymous traffic into ONE bucket and
    // its per-client limit becomes a global cap — configured, running, and a
    // denial of service against legitimate users rather than a defence.
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // Trust only the ingress. Left empty, ASP.NET Core trusts nothing
        // beyond loopback and silently keeps the proxy's address; opened to
        // all, any client can spoof its partition key and bypass the limit.
        o.KnownNetworks.Clear();
        o.KnownProxies.Clear();
        foreach (var cidr in builder.Configuration
                     .GetRequiredSection("Ingress:TrustedNetworks").Get<string[]>()!)
            o.KnownNetworks.Add(IPNetwork.Parse(cidr));
    });
}

// Only when browsers call the gateway directly rather than through a CDN or
// same-origin edge (§10.2). Enabled but unset would yield WithOrigins([]),
// which rejects every browser request while starting cleanly — surfacing as a
// CORS error in a console rather than as the missing setting it is (§15.4).
if (corsEnabled)
{
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
        .WithOrigins(builder.Configuration.GetRequiredSection("Cors:Origins").Get<string[]>()!)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));
}

var app = builder.Build();

// First when present: everything below reads the client address, and until
// this runs it is the proxy's. Skipped when the gateway IS the edge (Compose),
// where RemoteIpAddress is already the client and trusting a forwarded header
// would let any caller choose its own rate-limit bucket.
if (behindProxy) app.UseForwardedHeaders();

app.UseExceptionHandler();
app.UseCorrelationId();           // assigns the ID if the client sent none
if (corsEnabled) app.UseCors();

// Authentication FIRST, then the limiter, then authorization. §10.3's
// "authenticated" policy partitions on the subject claim, and until this line
// runs HttpContext.User is an empty principal.
app.UseAuthentication();
app.UseRateLimiter();             // §10.3 — needs the user, precedes policy work
app.UseAuthorization();

app.MapReverseProxy();
app.MapCommonHealthEndpoints();   // §13.5 — same anonymous probes as every service

app.Run();
```

Rate limiting sits **between** authentication and authorization, and both halves
of that are load-bearing.

It must come *after* `UseAuthentication` because §10.3's `authenticated` policy
partitions on the subject claim. Before that line the claim lookup returns null
and the key falls back to `RemoteIpAddress` — which does not fail, it just
quietly meters every signed-in user behind one corporate NAT as a single client
on a bucket sized for one person. The per-user quota would be advertised,
configured, and absent.

Placing it after authentication costs less than the older "reject floods before
doing crypto" instinct suggests. A request with no `Authorization` header does
no cryptographic work at all — `JwtBearer` finds nothing to validate and returns
immediately — so an anonymous flood is still turned away for the price of a
header lookup. Only a flood that presents tokens pays signature validation, and
that is the unavoidable cost of knowing whose quota to charge.

It must come *before* `UseAuthorization` because policy evaluation is the
expensive half: `inventory:admin` (§10.2) walks the principal's permission
claims, and a request that is over its limit should never reach that.

The gateway uses the same `MapCommonHealthEndpoints` as every service rather
than mapping a probe inline. Its readiness set is empty — the gateway owns no
database — so `/health/ready` returns healthy as soon as the process is up,
which is correct. What matters is that the probes stay **anonymous**: mapped
inline after `UseAuthorization`, the gateway would be the one component whose
own health check could be rejected by its own auth pipeline.

### 4.3 What may be shared between services

Exactly one thing: `Common.Contracts`, containing integration event records and
nothing else. No behaviour, no validation, no domain types.

> **Trap — the shared kernel that ate the platform.** A `Common.Entities`
> assembly containing `Product`, `Customer` and `Order` looks like sensible reuse
> and is the single most reliable way to destroy service independence. Two
> contexts sharing an entity class cannot evolve their models separately, so they
> must deploy together, so they are one service with extra steps. Duplicate the
> class. The duplication is the point — each context keeps only the fields it
> actually needs, and they diverge correctly over time.

`Common.Domain`, `Common.Application` and `Common.Infrastructure` are shared
*mechanism*, not shared *model*: base classes, the dispatcher, the outbox. That
is legitimate, but keep them small and treat every addition sceptically — a
shared library used by six services and the BFF is a coordination point.

### 4.4 Pinning the toolchain and packages

`global.json` pins the SDK, so every developer and every CI agent compiles with
the same compiler and analysers. Without it, a machine with a newer SDK can
produce different diagnostics — or different behaviour — from the build that was
reviewed.

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestPatch"
  }
}
```

`Directory.Packages.props` pins every package version once for the whole
repository. This prevents the situation where two services depend on different
EF Core minor versions and behave differently under identical code.

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup Label="Runtime">
    <PackageVersion Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Caching.Hybrid" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Http.Resilience" Version="10.0.0" />
    <PackageVersion Include="Dapper" Version="2.1.66" />
    <!-- Exact major. v9 is commercially licensed — see ADR-003. -->
    <PackageVersion Include="MassTransit.RabbitMQ" Version="8.5.3" />
    <PackageVersion Include="StackExchange.Redis" Version="2.9.11" />
    <PackageVersion Include="FluentValidation" Version="12.0.0" />
    <PackageVersion Include="Scrutor" Version="6.1.0" />
    <PackageVersion Include="Yarp.ReverseProxy" Version="2.3.0" />
    <!-- The BFF's one synchronous hop (§9.7). Grpc.* majors move on their own
         schedule, independent of the .NET release — exactly the case for
         pinning rather than trusting the SDK to carry a compatible version. -->
    <PackageVersion Include="Grpc.Net.ClientFactory" Version="2.71.0" />
    <PackageVersion Include="Grpc.AspNetCore" Version="2.71.0" />
    <PackageVersion Include="Grpc.Tools" Version="2.71.0" />
    <PackageVersion Include="Google.Protobuf" Version="3.29.3" />
  </ItemGroup>
  <ItemGroup Label="Telemetry">
    <PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="1.13.1" />
    <PackageVersion Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.13.1" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.13.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.Http" Version="1.13.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.Runtime" Version="1.13.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.EntityFrameworkCore" Version="1.13.0-beta.1" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.StackExchangeRedis" Version="1.13.0-beta.1" />
    <PackageVersion Include="AspNetCore.HealthChecks.SqlServer" Version="9.0.0" />
    <PackageVersion Include="AspNetCore.HealthChecks.Redis" Version="9.0.0" />
    <PackageVersion Include="AspNetCore.HealthChecks.Rabbitmq" Version="9.0.0" />
  </ItemGroup>
  <ItemGroup Label="Test">
    <!-- Test packages are pinned for exactly the same reason as runtime ones.
         xUnit v2 → v3 changed IAsyncLifetime from Task to ValueTask (§12.4):
         a major that drifts in silently breaks every fixture in the repo. -->
    <PackageVersion Include="xunit.v3" Version="3.1.0" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />
    <PackageVersion Include="Shouldly" Version="4.3.0" />
    <PackageVersion Include="NSubstitute" Version="5.3.0" />
    <PackageVersion Include="Testcontainers.MsSql" Version="4.6.0" />
    <PackageVersion Include="Testcontainers.Redis" Version="4.6.0" />
    <PackageVersion Include="Testcontainers.RabbitMq" Version="4.6.0" />
    <PackageVersion Include="Respawn" Version="6.2.1" />
    <PackageVersion Include="WireMock.Net" Version="1.8.11" />
    <PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="9.9.0" />
  </ItemGroup>
</Project>
```

**Every package means every package**, including the test ones — those are where
the version numbers here came from, and the list is the same set Appendix B
registers. The two files answer different questions about the same dependencies:
Appendix B says whether a licence is acceptable, this file says which version CI
will actually resolve. A package in one and not the other is how a licence
boundary gets crossed by a restore, and it is worth a CI check that the two
lists match.

The versions are those current at the review date in the header. A blueprint
cannot keep them accurate; the SCA step below is what keeps them honest.

> **Trap — pinning floors instead of versions.** Writing `Version="8.*"`, or
> treating the file as a set of minimums to be "reviewed quarterly", means a
> routine `dotnet restore` can resolve forward across a major boundary. Where
> that boundary is also a **licence** boundary — MassTransit v8 → v9 is the live
> example (Appendix B) — the obligation is acquired by a restore rather than by a
> decision. Pin exact versions and upgrade deliberately.

Back this with a CI step that fails the build on any package licence outside an
allow-list. Licence drift is only caught reliably by tooling; a convention will
not survive the twentieth dependency.

---

## 5. Tactical DDD

### 5.1 The building blocks

| Block | Definition | Test |
|---|---|---|
| **Entity** | Has identity that persists through change. | Two instances with the same ID are the same thing regardless of other fields. |
| **Value object** | Defined entirely by its values; immutable. | Two instances with equal values are interchangeable. |
| **Aggregate** | A cluster of entities and value objects with one root, forming a consistency boundary. | Can you state an invariant that must hold across all of it, atomically? |
| **Aggregate root** | The only entity outside code may hold a reference to. | Every path into the aggregate goes through it. |
| **Domain event** | A record that something meaningful happened, in past tense. | A domain expert would recognise the name. |
| **Repository** | Collection-like access to aggregate roots. One per aggregate. | It loads and saves whole aggregates, never fragments. |
| **Domain service** | Logic that belongs to no single aggregate. | It genuinely spans aggregates or needs external data. Rare — be suspicious. |

### 5.2 Strongly typed identifiers

Primitive `Guid` identifiers allow `GetOrder(customerId)` to compile. Typed IDs
make that a compile error, which over the lifetime of a system prevents a
recurring class of bug at essentially zero cost.

```csharp
namespace Ordering.Domain.Orders;

public readonly record struct OrderId(Guid Value)
{
    // Version 7 GUIDs are time-ordered, which keeps SQL Server's clustered
    // index append-only instead of fragmenting on every insert.
    public static OrderId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
```

### 5.3 Value objects

```csharp
namespace Ordering.Domain.Common;

public readonly record struct Money
{
    public decimal Amount { get; }
    public string Currency { get; }

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public static Money Of(decimal amount, string currency)
    {
        if (amount < 0)
            throw new DomainException("Money cannot be negative.");
        if (currency is not { Length: 3 })
            throw new DomainException("Currency must be a 3-letter ISO code.");

        return new Money(decimal.Round(amount, 2, MidpointRounding.ToEven),
                         currency.ToUpperInvariant());
    }

    public static Money Zero(string currency) => Of(0m, currency);

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator *(Money money, int quantity) =>
        new(money.Amount * quantity, money.Currency);

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
            throw new DomainException(
                $"Cannot combine {left.Currency} with {right.Currency}.");
    }
}
```

The constructor is private and `Of` is the only way in. An invalid `Money`
cannot be constructed, so no code downstream needs to check for one. This is the
**always-valid** principle, and applying it consistently removes a surprising
amount of defensive code from the rest of the system.

### 5.4 An aggregate

`Order` is the core aggregate of the blueprint. Note what it does *not* have:
public setters, a parameterless public constructor, references to other
aggregates by object, or any knowledge of persistence.

```csharp
namespace Ordering.Domain.Orders;

public sealed class Order : AggregateRoot<OrderId>
{
    private readonly List<OrderLine> _lines = [];

    public CustomerId CustomerId { get; private set; }
    public OrderStatus Status { get; private set; }
    public Address ShippingAddress { get; private set; }
    public DateTimeOffset PlacedAt { get; private set; }
    public IReadOnlyList<OrderLine> Lines => _lines.AsReadOnly();

    public Money Total => _lines.Aggregate(
        Money.Zero(_currency), (sum, line) => sum + line.LineTotal);

    /// <summary>
    /// An immutable copy of the lines, for events. `Lines` returns a read-only
    /// *view* over the live list, so an event holding it would keep changing
    /// after the fact — a record of what happened must not track what happens
    /// next.
    /// </summary>
    private IReadOnlyList<OrderLineSnapshot> SnapshotLines() =>
        _lines.Select(l => new OrderLineSnapshot(l.ProductId, l.Quantity, l.UnitPrice))
              .ToArray();

    private readonly string _currency;

    // EF Core materialisation only.
    private Order() { }

    private Order(OrderId id, CustomerId customerId, Address shippingAddress,
                  string currency, DateTimeOffset placedAt)
    {
        Id = id;
        CustomerId = customerId;
        ShippingAddress = shippingAddress;
        _currency = currency;
        PlacedAt = placedAt;
        Status = OrderStatus.Draft;
    }

    public static Order Place(
        CustomerId customerId,
        Address shippingAddress,
        IEnumerable<(ProductId Product, int Quantity, Money UnitPrice)> items,
        string currency,
        DateTimeOffset now)
    {
        var order = new Order(OrderId.New(), customerId, shippingAddress,
                              currency, now);

        foreach (var (product, quantity, unitPrice) in items)
            order.AddLine(product, quantity, unitPrice);

        if (order._lines.Count == 0)
            throw new DomainException("An order must contain at least one line.");

        order.Status = OrderStatus.AwaitingStock;
        // Lines travel with the event: the integration contract needs them so
        // Inventory can reserve stock (§9.6), and a handler must not have to
        // reload the aggregate to find out what was ordered.
        order.Raise(new OrderPlacedDomainEvent(
            order.Id, customerId, order.Total, order.SnapshotLines(), now));

        return order;
    }

    private void AddLine(ProductId product, int quantity, Money unitPrice)
    {
        if (quantity <= 0)
            throw new DomainException("Quantity must be positive.");
        if (unitPrice.Currency != _currency)
            throw new DomainException("All lines must share the order currency.");

        var existing = _lines.SingleOrDefault(l => l.ProductId == product);
        if (existing is not null)
            existing.IncreaseQuantity(quantity);
        else
            _lines.Add(OrderLine.For(product, quantity, unitPrice));
    }

    public void ConfirmStock(DateTimeOffset now)
    {
        EnsureStatus(OrderStatus.AwaitingStock);
        Status = OrderStatus.AwaitingPayment;
        Raise(new OrderStockConfirmedDomainEvent(Id, Total, now));
    }

    public void ConfirmPayment(PaymentReference reference, DateTimeOffset now)
    {
        EnsureStatus(OrderStatus.AwaitingPayment);
        Status = OrderStatus.Confirmed;
        // Total and Lines are required by the V1.OrderConfirmed contract (§9.1);
        // the mapper has only the event to work from.
        Raise(new OrderConfirmedDomainEvent(
            Id, CustomerId, reference, ShippingAddress, Total, SnapshotLines(), now));
    }

    public void MarkShipped(TrackingNumber tracking, DateTimeOffset now)
    {
        EnsureStatus(OrderStatus.Confirmed);
        Status = OrderStatus.Shipped;
        Raise(new OrderShippedDomainEvent(Id, CustomerId, tracking, now));
    }

    public void Cancel(CancellationReason reason, DateTimeOffset now)
    {
        if (Status is OrderStatus.Shipped or OrderStatus.Delivered)
            throw new DomainException(
                $"A {Status} order cannot be cancelled; raise a return instead.");
        if (Status is OrderStatus.Cancelled)
            return;   // Idempotent — cancelling twice is not an error.

        Status = OrderStatus.Cancelled;
        Raise(new OrderCancelledDomainEvent(Id, CustomerId, reason, now));
    }

    private void EnsureStatus(OrderStatus expected)
    {
        if (Status != expected)
            throw new DomainException(
                $"Expected order to be {expected} but it is {Status}.");
    }
}
```

Points worth noticing:

- **`Place` is a factory, not a constructor.** It names the business operation
  and can enforce rules a constructor cannot express cleanly.
- **`_lines` is private; `Lines` is read-only.** Callers cannot bypass `AddLine`
  and its invariants.
- **`Cancel` is idempotent.** Because events arrive at-least-once, aggregate
  methods driven by events should tolerate repetition.
- **`now` is a parameter.** The domain never reads the clock. This makes every
  time-dependent rule trivially testable without freezing time globally.
- **Other aggregates are referenced by ID**, never by object. `CustomerId`, not
  `Customer`. This is what keeps the aggregate loadable in one query and the
  transaction boundary honest.

### 5.5 Domain events

```csharp
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}

/// <summary>
/// Non-generic markers. Infrastructure filters EF's change tracker by them —
/// `Entries<IHasDomainEvents>()` in §7.5, `is IAggregateRoot` in §6.3 — and the
/// tracker holds objects, not `AggregateRoot<TId>` for a known TId. Without a
/// non-generic interface to test against, those queries would need to know
/// every key type in the model.
/// </summary>
public interface IHasDomainEvents
{
    IReadOnlyList<IDomainEvent> DomainEvents { get; }
    void ClearDomainEvents();
}

public interface IAggregateRoot;

public abstract class AggregateRoot<TId>
    : Entity<TId>, IAggregateRoot, IHasDomainEvents
    where TId : struct
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();

    // Optimistic concurrency token, mapped to SQL Server rowversion.
    public byte[] Version { get; private set; } = [];
}
```

> **Both markers must be on the base class, and neither failure is loud.** A
> marker used only in a predicate is a filter that silently matches nothing:
> `Entries<IHasDomainEvents>()` returns empty, `CollectAndClear()` returns
> empty, the dispatcher exits early, and the command commits having staged no
> outbox rows at all — no projection, no integration event, no saga start. The
> write succeeds and every downstream mechanism in this document runs on an
> empty list. `IAggregateRoot` fails the same way, more quietly: the
> one-aggregate assertion (§6.3) counts zero and never fires.

The events themselves are records in the Domain project, free to carry domain
types — that freedom is the point of keeping them separate from contracts:

```csharp
namespace Ordering.Domain.Orders.Events;

/// <summary>Immutable copy of a line as it stood when the event was raised.</summary>
public sealed record OrderLineSnapshot(ProductId ProductId, int Quantity, Money UnitPrice);

public sealed record OrderPlacedDomainEvent(
    OrderId OrderId,
    CustomerId CustomerId,
    Money Total,
    IReadOnlyList<OrderLineSnapshot> Lines,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OrderConfirmedDomainEvent(
    OrderId OrderId,
    CustomerId CustomerId,
    PaymentReference Reference,
    Address ShippingAddress,
    Money Total,
    IReadOnlyList<OrderLineSnapshot> Lines,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OrderStockConfirmedDomainEvent(
    OrderId OrderId,
    Money Total,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OrderShippedDomainEvent(
    OrderId OrderId,
    CustomerId CustomerId,
    TrackingNumber Tracking,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OrderCancelledDomainEvent(
    OrderId OrderId,
    CustomerId CustomerId,
    CancellationReason Reason,
    DateTimeOffset OccurredAt) : IDomainEvent;
```

All five name the identifier `OrderId`, not `Id`. The projection in §6.6 reads
`e.OrderId` across every handler, so one record calling it something else would
break only that handler — the kind of divergence a positional `Raise(Id, …)`
call site hides completely.

Two rules these illustrate:

**Carry everything the contract needs.** `OrderConfirmedDomainEvent` holds
`Total` and `Lines` it makes no domain use of, because `V1.OrderConfirmed`
requires them and the mapper (§9.3) sees only the event. An event missing a
field its contract declares is a mapper that cannot be written.

**Snapshot, never alias.** `OrderLineSnapshot` exists because `Order.Lines` is a
read-only *view* over a live list of mutable entities. An event holding that
view would report whatever the aggregate looks like later, not what happened
when it was raised.

`Money`, `OrderId` and `ProductId` here would be forbidden in an integration
event (§9.1) — the mapper is where they are flattened to primitives.

Events accumulate on the aggregate. Raising one does not publish it, and the
two halves of what happens next are worth keeping apart, because §7.5 is
normative about both and one of them is easy to state backwards:
`DomainEventDispatcher` runs **inside** the transaction, before `SaveChanges`,
and only **stages** outbox rows — it invokes no handler. Everything that
*reacts* runs after commit, driven by the outbox (ADR-018). "Dispatched" is the
staging; delivery is the dispatcher's job later.

**Domain events and integration events are different things** and conflating
them is one of the most consequential mistakes in this architecture:

| | Domain event | Integration event |
|---|---|---|
| Scope | Inside one service | Across services |
| Coupling | Free to change with the code | A published contract, versioned |
| Content | Rich domain types | Primitives and simple DTOs only |
| Delivery | In-process after commit, via the outbox's `Local` lane (§7.5) | Message broker, via the outbox's `Broker` lane |
| Naming | `*DomainEvent` suffix | Bare name, in a versioned namespace (§9.2) |
| Example | `OrderConfirmedDomainEvent` | `Common.Contracts.Ordering.V1.OrderConfirmed` |

A domain event may be translated into an integration event. Never publish a
domain event directly onto the bus — it exposes your internal model as a public
contract and you will not be able to refactor afterwards.

**They must not share a type name.** `OrderPlacedDomainEvent` and
`Common.Contracts.Ordering.V1.OrderPlaced` describe the same business fact in
two shapes — one carries `Money`, the other a `decimal` and a currency code —
and a single name for both makes the mapper in §9.3 read as an identity
function. Namespace versioning (ADR-012) distinguishes contract *versions*, not
contracts from domain events; the suffix does that.

### 5.6 Repositories

Defined in Domain, implemented in Infrastructure. One per aggregate root.

```csharp
namespace Ordering.Domain.Orders;

public interface IOrderRepository
{
    Task<Order?> GetAsync(OrderId id, CancellationToken ct);
    Task<Order?> GetByPaymentReferenceAsync(PaymentReference reference, CancellationToken ct);
    void Add(Order order);
}
```

There is no `Update` — the unit of work tracks changes — and no `GetAll`,
`Find(Expression<...>)` or `IQueryable`. A repository that returns `IQueryable`
lets callers build arbitrary queries against the domain model, which leaks
persistence concerns everywhere and makes the aggregate boundary unenforceable.

**Reads do not go through repositories.** Query handlers use Dapper directly
against read models (section 6.5). Repositories exist only to load aggregates
for the purpose of changing them.

### 5.7 Anti-patterns

| Anti-pattern | Why it fails | Instead |
|---|---|---|
| Anaemic domain model — entities of public setters, logic in services | The model enforces nothing; invariants scatter and drift | Behaviour on the aggregate; setters private |
| Aggregate referencing another aggregate by object | Loads half the database; transaction spans two consistency boundaries | Reference by typed ID |
| Huge aggregate (`Customer` owning all orders) | Every write locks everything; concurrency collapses | Split by invariant, not by "belongs to" |
| Repository per entity | Lets callers modify children outside the root | Repository per aggregate root only |
| Injecting `IEmailSender` into an aggregate | Domain now depends on infrastructure and cannot be unit tested | Raise a domain event; handle it outside |
| `DateTime.UtcNow` inside the domain | Non-deterministic tests, hidden dependency | Pass time in as a parameter |
| Domain exceptions used for validation of user input | Exceptions for control flow; poor error messages | Validate in the application layer; domain exceptions signal bugs |

---

## 6. CQRS

### 6.1 What CQRS means here

Command Query Responsibility Segregation means the write path and the read path
use different models. It does not require different databases, event sourcing,
or eventual consistency. Those are options that CQRS *enables*, not requirements
it imposes.

The blueprint uses two levels and shows how to move between them:

| | Level 1 — Logical CQRS | Level 2 — Physical split |
|---|---|---|
| Write model | EF Core, aggregates | EF Core, aggregates |
| Read model | Dapper over the same tables/views | Dedicated denormalised tables or Redis |
| Store | One database | Write DB + read store |
| Sync | None needed | Projections from events |
| Consistency | Strong | Eventual |
| Used by | Catalog, Inventory, Payments, Shipping | Ordering (section 6.6) |

**Start at level 1.** It gives most of the benefit — the write model stays
clean, queries stay fast — at none of the operational cost. Escalate only where
measurement justifies it.

### 6.2 The dispatcher

MediatR is the conventional choice and moved to a commercial licence in 2025.
The functionality it provides here is roughly eighty lines. Writing them removes
a licence obligation, a dependency, and a layer of reflection-driven indirection
that makes stack traces harder to read.

```csharp
namespace Common.Application;

public interface ICommand<out TResult>;
public interface IQuery<out TResult>;

public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken ct);
}

public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken ct);
}

public delegate Task<TResult> NextDelegate<TResult>();

public interface IPipelineBehavior<in TRequest, TResult>
{
    Task<TResult> HandleAsync(TRequest request, NextDelegate<TResult> next,
                              CancellationToken ct);
}

public interface IDispatcher
{
    Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken ct = default);
    Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken ct = default);
}
```

The implementation caches one invoker instance per concrete request type, so the
reflection cost is paid once per type rather than per call.

```csharp
internal sealed class Dispatcher(IServiceProvider services) : IDispatcher
{
    private static readonly ConcurrentDictionary<Type, object> Invokers = new();

    public Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken ct = default)
        => GetInvoker<TResult>(command.GetType(), typeof(CommandInvoker<,>))
              .InvokeAsync(services, command, ct);

    public Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken ct = default)
        => GetInvoker<TResult>(query.GetType(), typeof(QueryInvoker<,>))
              .InvokeAsync(services, query, ct);

    private static Invoker<TResult> GetInvoker<TResult>(Type requestType, Type openInvoker)
        => (Invoker<TResult>)Invokers.GetOrAdd(requestType, _ =>
               Activator.CreateInstance(
                   openInvoker.MakeGenericType(requestType, typeof(TResult)))!);

    private abstract class Invoker<TResult>
    {
        public abstract Task<TResult> InvokeAsync(
            IServiceProvider services, object request, CancellationToken ct);
    }

    private sealed class CommandInvoker<TCommand, TResult> : Invoker<TResult>
        where TCommand : ICommand<TResult>
    {
        public override Task<TResult> InvokeAsync(
            IServiceProvider services, object request, CancellationToken ct)
        {
            var typed   = (TCommand)request;
            var handler = services.GetRequiredService<ICommandHandler<TCommand, TResult>>();

            NextDelegate<TResult> pipeline = () => handler.HandleAsync(typed, ct);

            // Reversed so the first-registered behaviour is the outermost.
            foreach (var behavior in services
                         .GetServices<IPipelineBehavior<TCommand, TResult>>()
                         .Reverse())
            {
                var next = pipeline;
                pipeline = () => behavior.HandleAsync(typed, next, ct);
            }

            return pipeline();
        }
    }

    // QueryInvoker<TQuery, TResult> is identical but resolves IQueryHandler<,>.
}
```

Registration scans the assembly once at startup. Every pluggable interface must
be scanned — one that exists but is never registered resolves to an empty
collection or throws at first use, and neither failure points at the omission.

The list is declared **once**, in `Common.Application`, and both the scan and
the test below read it. That is the point: the previous version kept two copies,
and adding a fifth interface meant remembering both:

```csharp
namespace Common.Application;

/// <summary>
/// Every open generic the container is expected to discover by convention.
/// Adding a pluggable interface means adding it here — and nowhere else.
/// </summary>
public static class PluggableInterfaces
{
    public static readonly IReadOnlyList<Type> All =
    [
        typeof(ICommandHandler<,>),          // §6.2 — HTTP and message-borne
        typeof(IQueryHandler<,>),            // §6.5
        typeof(IProjectionHandler<>),        // §7.5 — local outbox lane
        typeof(IIntegrationEventHandler<>),  // §9.4 — broker lane
        typeof(ICommandMessageMapper<,>)     // §9.4 — wire contract → command

        // IPipelineBehavior<,> is deliberately absent. Registration order is
        // pipeline order (§6.3), and a scan gives no ordering guarantee —
        // behaviours are registered explicitly and asserted by a test.
    ];
}

/// <summary>Maps an inbound command contract to its application command.</summary>
public interface ICommandMessageMapper<in TMessage, out TCommand>
    where TMessage : class
{
    TCommand Map(TMessage message);
}
```

```csharp
public static IServiceCollection AddPluggableFrom(
    this IServiceCollection services, Assembly assembly)
    => services.Scan(scan =>
    {
        var from = scan.FromAssemblies(assembly);

        foreach (var contract in PluggableInterfaces.All)
            from.AddClasses(c => c.AssignableTo(contract))
                .AsImplementedInterfaces()
                .WithScopedLifetime();
    });
```

**Each layer scans itself.** Handlers do not all live in Application: the
projections in §6.6 write SQL, `PriceChangedCacheInvalidator` (§8.4) sits in
`Ordering.Infrastructure.Caching`, and the command mappers convert wire
contracts. Scanning one assembly registers some handlers and silently skips the
rest, which is the §6.2 trap with a wider blast radius — so both registration
methods call it:

```csharp
// Ordering.Application/DependencyInjection.cs
services.AddPluggableFrom(typeof(PlaceOrderCommand).Assembly);

// Ordering.Infrastructure/DependencyInjection.cs
services.AddPluggableFrom(typeof(OrderRepository).Assembly);
```

> **Trap — the handler that was never registered.** Nothing in C# requires an
> implemented interface to be resolvable. `GetServices<IProjectionHandler<T>>()`
> returning empty is indistinguishable from "this event has no projection", so
> the message is marked processed having done nothing and the monitoring stays
> green. §9.4 closes this by throwing when a `Local` row finds no handler, and
> the registration test below catches it at build time instead.
>
> The trap has a second form worth naming, because this document fell into it:
> a *list* of interfaces duplicated between the registration and the test that
> guards it. Both copies drift together or not at all, and the guard silently
> stops covering whatever the newest interface is. One list, two readers.

Three mechanisms guard wiring, and none subsumes the others:

| | Catches | Misses |
|---|---|---|
| **`ValidateOnBuild`** (§4.2) | Anything *depended upon* but unregistered — ports, stores, clients — at startup, for the whole graph | A type nothing depends on. An unregistered `IProjectionHandler` breaks no constructor, so the container starts happily |
| **The registration test** below | An implementation of a scanned interface that never got registered, whether or not anything depends on it | Plain ports — not open generics, so not in `PluggableInterfaces` |
| **`ValidateOnStart`** (§15.4) | An options type that is never bound, or bound but missing a `[Required]` value | Anything that is not configuration |

They cover three different failure shapes, and the third is the least obvious:
`IOptions<T>` resolves whether or not it was bound, handing back an empty
instance. So a forgotten `AddOptions` satisfies `ValidateOnBuild`, passes the
registration test, starts the service, and fails as *behaviour* — an empty
Redis key prefix, a token request with no scope.

Worked examples of each: `IProductPriceReader` unregistered fails
`ValidateOnBuild`, because `PlaceOrderHandler` needs it. `ProductPriceProjection`
unregistered fails only the test — nothing constructs it, it is simply never
called. `ServiceIdentityOptions` unbound fails only `ValidateOnStart` — the
container resolves `IOptions<T>` happily and hands back an empty instance.

```csharp
[Fact]
public void Every_handler_implementation_is_registered()
{
    // BuildProvider() — the real registration path, not a test-only container,
    // and the same helper §6.3 and §13.6 use rather than a second copy of the
    // three calls. It runs BOTH AddOrderingApplication and
    // AddOrderingInfrastructure, which is the property this test depends on:
    // a hand-rolled version that ran only the Application half would find the
    // Infrastructure handlers absent and report the layer it forgot to build
    // as an unregistered handler.
    //
    // Handlers are scoped; resolving them from the root provider throws.
    using var scope = BuildProvider().CreateScope();

    // Every service assembly, not just Application. Building the provider above
    // has forced both to load, and deriving the set here means a new layer is
    // covered without editing this test — the same reason the interface list
    // is not duplicated either.
    var assemblies = AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => a.GetName().Name?.StartsWith("Ordering.") == true);

    // Same list the scan uses — a new interface is covered the moment it is
    // added to PluggableInterfaces, with no second place to remember.
    var implementations = assemblies.SelectMany(a => a.GetTypes())
        .Where(t => t is { IsAbstract: false, IsInterface: false })
        .SelectMany(t => t.GetInterfaces()
            .Where(i => i.IsGenericType
                     && PluggableInterfaces.All.Contains(i.GetGenericTypeDefinition()))
            .Select(i => (Implementation: t, Service: i)));

    foreach (var (implementation, service) in implementations)
        scope.ServiceProvider.GetServices(service).ShouldContain(
            s => s!.GetType() == implementation,
            $"{implementation.Name} implements {service.Name} but is not registered.");
}
```

> **Decision — no mediator library.** See [ADR-004](#adr-004--no-mediator-library).

### 6.3 Pipeline behaviours

Cross-cutting concerns are behaviours, registered once and applied to every
command. Order matters — they nest outermost-first.

```
Request
  → Logging          (correlation id, timing, outcome)
  → Validation       (FluentValidation; fails fast before any I/O)
  → Idempotency      (has this command id been processed?)
  → Transaction      (open, handle, dispatch domain events, commit)
      → Handler
```

**Behaviours are registered explicitly, in order, and are deliberately not part
of the §6.2 convention scan:**

```csharp
// In AddOrderingApplication, after AddPluggableFrom.
//
// Registration order IS pipeline order — the dispatcher reverses this list so
// the first registered ends up outermost (§6.2). A scan would register them in
// whatever order reflection returns types, which is unspecified, so this one
// interface is excluded from PluggableInterfaces on purpose.
services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
services.AddScoped(typeof(IPipelineBehavior<,>), typeof(IdempotencyBehavior<,>));
services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));
```

> **Unregistered, this fails silently and completely.** `GetServices<IPipelineBehavior<…>>()`
> returning empty is indistinguishable from "no behaviours configured", so the
> dispatcher invokes the handler alone. `SaveChangesAsync` has exactly one call
> site — inside `TransactionBehavior` — so a missing registration means
> `PlaceOrderHandler` calls `orders.Add(order)`, returns `Result.Success`, and
> **nothing is ever written**: no order, no outbox row, no saga. The request
> returns 200.
>
> That is the argument for the ordered-registration test below rather than
> trusting four lines to survive a refactor.

```csharp
[Fact]
public void Command_behaviours_are_registered_in_the_documented_order()
{
    using var scope = BuildProvider().CreateScope();

    var actual = scope.ServiceProvider
        .GetServices<IPipelineBehavior<PlaceOrderCommand, Result<Guid>>>()
        .Select(b => b.GetType().GetGenericTypeDefinition())
        .ToArray();

    actual.ShouldBe([
        typeof(LoggingBehavior<,>),
        typeof(ValidationBehavior<,>),
        typeof(IdempotencyBehavior<,>),
        typeof(TransactionBehavior<,>)
    ], "outermost first — see the pipeline diagram above");
}
```

The generic constraints do the rest of the work: `IdempotencyBehavior` requires
`IIdempotentCommand` (§8.5) and `TransactionBehavior` requires
`ICommand<TResult>`, so both are skipped for queries and for commands that have
not opted in, without either behaviour needing to check.

That skipping is a container feature, not a language one — `Microsoft.Extensions
.DependencyInjection` has honoured constraints on open generic registrations
since .NET 7, and on an older container the same registration throws when the
first query resolves rather than quietly omitting the behaviour. A blueprint
that leaves it at "the constraints do the work" is trusting a version note, so
the assertion above has a mirror:

```csharp
[Fact]
public void Queries_run_without_the_transaction_and_idempotency_behaviours()
{
    using var scope = BuildProvider().CreateScope();

    var actual = scope.ServiceProvider
        // The query's own result type (§6.5) — CursorPage, not Result. A
        // closed IPipelineBehavior<,> asked for with the wrong TResult resolves
        // to an empty sequence, and an empty sequence passes any assertion
        // about what is absent.
        .GetServices<IPipelineBehavior<GetOrderSummariesQuery,
                                       CursorPage<OrderSummaryDto>>>()
        .Select(b => b.GetType().GetGenericTypeDefinition())
        .ToArray();

    // A query opening a transaction is the defect this catches: harmless in
    // a test, and a held connection per read under load.
    actual.ShouldBe([
        typeof(LoggingBehavior<,>),
        typeof(ValidationBehavior<,>)
    ], "queries get logging and validation only — §6.3");
}
```

Validation:

```csharp
public sealed class ValidationBehavior<TRequest, TResult>(
    IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResult>
{
    public async Task<TResult> HandleAsync(
        TRequest request, NextDelegate<TResult> next, CancellationToken ct)
    {
        if (!validators.Any())
            return await next();

        var context = new ValidationContext<TRequest>(request);
        var failures = (await Task.WhenAll(
                validators.Select(v => v.ValidateAsync(context, ct))))
            .SelectMany(r => r.Errors)
            .Where(f => f is not null)
            .ToArray();

        if (failures.Length > 0)
            throw new ValidationException(failures);

        return await next();
    }
}
```

Transaction — this is the behaviour that makes the domain-event and outbox
mechanism work, and it is the one worth reading closely.

It must not reference EF Core, because it lives in `Common.Application` and
§4.2 forbids it. The transaction boundary is therefore expressed as a port:

```csharp
namespace Common.Application;

/// <summary>
/// The command transaction boundary. Implemented in Infrastructure over the
/// service DbContext; Application never sees EF Core.
/// </summary>
public interface IUnitOfWork
{
    bool HasActiveTransaction { get; }

    /// <summary>
    /// Runs <paramref name="operation"/> inside one atomic unit, retrying the
    /// whole unit on transient faults. Persists aggregate changes, domain-event
    /// side effects and outbox rows together, or none of them.
    /// </summary>
    Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation, CancellationToken ct);

    Task<int> SaveChangesAsync(CancellationToken ct);
}
```

The behaviour depends only on that:

```csharp
public sealed class TransactionBehavior<TCommand, TResult>(
    IUnitOfWork unitOfWork,
    IDomainEventDispatcher domainEvents)
    : IPipelineBehavior<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<TResult> HandleAsync(
        TCommand command, NextDelegate<TResult> next, CancellationToken ct)
    {
        // Already inside a transaction (nested dispatch) — do not open another.
        if (unitOfWork.HasActiveTransaction)
            return await next();

        return await unitOfWork.ExecuteAsync(async token =>
        {
            var result = await next();

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

**This is the whole behaviour.** Nothing below adds to it. Every sample in this
document that shows part of a pipeline is an excerpt of something, and the one
place that matters is this one — because a behaviour assembled from fragments
loses whichever fragment the reader did not scroll to, and the missing piece is
silent in all three cases: no failure guard commits rejected commands, no
dispatch publishes nothing, no count check makes principle 3 advisory.

> **A rejected command must not have written anything, and one guard is not
> enough to promise that.** This one skips the staging and the save, which
> covers everything EF is tracking. It does nothing about a write that already
> reached the connection — `ExecuteRawAsync` (below) executes immediately, and
> no amount of not-calling-`SaveChanges` takes it back. That is why
> `EfUnitOfWork.ExecuteAsync` carries the *same* check before `CommitAsync`
> (§6.3): the two together mean a failed command commits nothing by either
> route.
>
> **Validate first, mutate second** remains the rule, because the guards make
> breaking it cost a discarded write rather than a committed lie — but a rule
> whose enforcement is two checks in two types is a rule worth testing. PR-09
> covers both: `SaveChanges` once on success and never on failure, and a
> handler that calls `ExecuteRawAsync` and then returns `Result.Failure` leaves
> no row behind.

> **Nothing inside this transaction may make a network call to another
> service.** The behaviour wraps the whole handler, so any remote call a handler
> makes is held open across the wire. With §9.7's 5-second client budget, a
> single slow peer can pin a SQL Server transaction — and its pooled connection
> — for five seconds per request. Under load that is connection-pool exhaustion
> and lock contention, and it converts "Catalog is slow" into "Ordering is
> down", which is precisely what ADR-002 exists to prevent.
>
> A command handler may therefore read only its **own** database. Data owned by
> another service must already be present locally, projected from that service's
> events (§6.6). §9.7 states the general rule; this is where violating it hurts
> most, because the transaction makes the coupling invisible at the call site.

#### One aggregate per transaction

Principle 3 (§2.3) says a transaction never spans two aggregates, and nothing
about `SaveChangesAsync` objects if a handler loads two — one save, one commit,
no complaint. The count in the behaviour above is what makes the rule
observable, and it needs two members on the port:

```csharp
public interface IUnitOfWork
{
    // ... as above

    /// <summary>Distinct aggregate roots with pending changes.</summary>
    int ModifiedAggregateCount { get; }

    /// <summary>
    /// Raw SQL on the transaction's own connection, for the rare table with no
    /// aggregate behind it (§9.6's OrderReviews). A command handler must not
    /// open its own connection — that write would commit outside this
    /// transaction.
    /// </summary>
    Task ExecuteRawAsync(string sql, object parameters, CancellationToken ct);
}
```

The EF implementation of both new members is in `EfUnitOfWork` below. Owned
children (`OrderLine`) do not count — they are part of their root, which is the
whole reason an aggregate is a consistency boundary rather than a table.

**Why a runtime check rather than an architecture test.** The violation is not
structural: nothing in a handler's *type* says how many aggregates it will
touch, and the second one is usually loaded conditionally, three calls deep.
An assertion at the transaction boundary catches it on the first execution that
does it — in a unit test, in CI, or on the developer's machine — with the
command name and the count in the message.

**When it fires, the fix is almost never to relax it.** A command that must
change two aggregates is describing a process, not a transaction: the second
aggregate reacts to the first one's domain event after commit (ADR-018), or the
two belong in one aggregate and the boundary is drawn in the wrong place. Both
are §5.4 problems, and the exception says which sections to read.

Query handlers must never resolve `IUnitOfWork`. The behaviour is constrained to
`ICommand<TResult>` precisely so the read path cannot open a write transaction
or touch the outbox.

The EF Core implementation lives in Infrastructure:

```csharp
namespace Ordering.Infrastructure.Persistence;

internal sealed class EfUnitOfWork(OrderingDbContext db) : IUnitOfWork
{
    public bool HasActiveTransaction => db.Database.CurrentTransaction is not null;

    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var result = await operation(ct);

            // The commit decision belongs with the commit. §6.3's behaviour
            // declines to SaveChanges on a failed Result, which is enough for
            // tracked changes — but ExecuteRawAsync writes on this
            // transaction's connection immediately, and only a rollback undoes
            // that. Returning without committing disposes the transaction,
            // which rolls it back.
            if (result is Result { IsFailure: true })
                return result;

            await tx.CommitAsync(ct);
            return result;
        });
    }

    public Task<int> SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    // Owned children (OrderLine) are not roots and do not count — that is the
    // difference between an aggregate and a table (§6.3, principle 3).
    public int ModifiedAggregateCount => db.ChangeTracker
        .Entries()
        .Count(e => e.Entity is IAggregateRoot &&
                    e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);

    // The transaction's own connection and transaction, explicitly passed —
    // this is what makes a raw write part of the command rather than beside it.
    public Task ExecuteRawAsync(string sql, object parameters, CancellationToken ct) =>
        db.Database.GetDbConnection().ExecuteAsync(new CommandDefinition(
            sql, parameters,
            transaction: db.Database.CurrentTransaction?.GetDbTransaction(),
            cancellationToken: ct));
}
```

Two details worth keeping:

**`CreateExecutionStrategy` is not optional.** With SQL Server retry-on-failure
enabled, EF Core refuses to retry a user-initiated transaction unless the whole
unit is wrapped in the strategy. Omitting it produces an exception the first
time a transient network fault occurs in production. Note that the operation may
therefore run **more than once** — it must not have side effects outside the
transaction, which is another reason the outbox exists.

**`DbContext` never leaves Infrastructure.** An `IApplicationDbContext`
interface exposing `DbSet<T>` is a common shortcut and is explicitly rejected
here: it puts EF Core types in the Application signature, which defeats the
boundary while appearing to respect it. Aggregates are reached through
repositories; the transaction through `IUnitOfWork`; nothing else.

### 6.4 A command

Commands are imperative, named for the business intent, and immutable.

```csharp
namespace Ordering.Application.Orders.PlaceOrder;

// IIdempotentCommand is what puts this command through IdempotencyBehavior
// (§8.5). Carrying a CommandId is not enough — the behaviour is constrained on
// the interface, so a command with the field and not the interface is
// unprotected, and a retried POST creates a second order.
public sealed record PlaceOrderCommand(
    Guid CommandId,
    Guid CustomerId,
    IReadOnlyList<PlaceOrderItem> Items,
    AddressDto ShippingAddress,
    string Currency) : ICommand<Result<Guid>>, IIdempotentCommand;

public sealed record PlaceOrderItem(Guid ProductId, int Quantity);

public sealed class PlaceOrderValidator : AbstractValidator<PlaceOrderCommand>
{
    public PlaceOrderValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.Currency).Length(3);
        RuleFor(x => x.Items).NotEmpty();
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.ProductId).NotEmpty();
            item.RuleFor(i => i.Quantity).GreaterThan(0).LessThanOrEqualTo(999);
        });
    }
}

public sealed class PlaceOrderHandler(
    IOrderRepository orders,
    IProductPriceReader prices,
    TimeProvider clock)
    : ICommandHandler<PlaceOrderCommand, Result<Guid>>
{
    public async Task<Result<Guid>> HandleAsync(
        PlaceOrderCommand command, CancellationToken ct)
    {
        var productIds = command.Items.Select(i => new ProductId(i.ProductId)).ToArray();
        var priceList  = await prices.GetAsync(productIds, command.Currency, ct);

        var missing = productIds.Where(id => !priceList.ContainsKey(id)).ToArray();
        if (missing.Length > 0)
            return Result.Failure<Guid>(OrderErrors.ProductsUnavailable(missing));

        var items = command.Items.Select(i =>
        {
            var id = new ProductId(i.ProductId);
            return (id, i.Quantity, priceList[id]);
        });

        var order = Order.Place(
            new CustomerId(command.CustomerId),
            command.ShippingAddress.ToDomain(),
            items,
            command.Currency,
            clock.GetUtcNow());

        orders.Add(order);

        // No metric here. "Orders placed" is a count of orders that committed,
        // and this line runs inside a transaction that may still roll back —
        // or be replayed whole by EF's retrying execution strategy (§6.3),
        // which would count the same order once per attempt. It is recorded by
        // the projection instead (§13.3).
        return Result.Success(order.Id.Value);
    }
}
```

The handler is thin by design. It loads what the domain needs, calls one domain
operation, and returns. All the business rules — line merging, currency
consistency, minimum one line — live in `Order`. If a handler grows past about
forty lines, logic has usually leaked out of the aggregate.

Note the handler does not call `SaveChanges`. The transaction behaviour owns
that. And note `TimeProvider` — the .NET abstraction for the clock, which makes
`FakeTimeProvider` available in tests.

#### Where the prices come from

`IProductPriceReader` is the one part of this handler worth dwelling on, because
the obvious implementation is wrong.

Prices are owned by **Catalog**. The tempting implementation calls Catalog over
gRPC — and it would run inside the write transaction (§6.3), holding a database
transaction open across a network call to another service.

Instead, `IProductPriceReader` reads a **local projection** in Ordering's own
database, kept current by Catalog's `PriceChanged` and `ProductPublished`
events (§6.6):

```csharp
internal sealed class ProjectedPriceReader(IDbConnectionFactory connections)
    : IProductPriceReader
{
    private const string Sql =
        """
        SELECT ProductId, Amount, Currency
        FROM   ordering.ProductPrices
        WHERE  ProductId IN @ProductIds
          AND  Currency = @Currency
          AND  IsAvailable = 1;
        """;

    public async Task<IReadOnlyDictionary<ProductId, Money>> GetAsync(
        IReadOnlyCollection<ProductId> productIds, string currency, CancellationToken ct)
    {
        using var connection = connections.Create();
        var rows = await connection.QueryAsync<PriceRow>(new CommandDefinition(
            Sql, new { ProductIds = productIds.Select(p => p.Value), Currency = currency },
            cancellationToken: ct));

        return rows.ToDictionary(r => new ProductId(r.ProductId),
                                 r => Money.Of(r.Amount, r.Currency));
    }
}
```

Three consequences, and the middle one is the point:

- **No network call inside the transaction.** The read is local, and a missing
  product is a plain validation failure rather than a timeout.
- **Catalog can be down and orders still get placed.** Availability stops
  multiplying, which is the whole argument of §2.3 principle 4 and ADR-002.
- **Prices can be stale by the projection's lag** — typically milliseconds.
  Where that is unacceptable, the order captures the price it used and payment
  reconciles against it; that is a business rule, not a reason to make the
  write path depend on another service being up.

§9.7's gRPC pricing client is a different caller: the **BFF**, reading prices to
render the order form before anything is submitted. A display read may be
synchronous and may fail with a spinner. The write path may not.

### 6.5 A query

Queries bypass the domain model entirely. There is no benefit to loading an
aggregate, enforcing its invariants, and mapping it to a DTO in order to display
a list.

```csharp
namespace Ordering.Application.Orders.GetOrderSummaries;

public sealed record GetOrderSummariesQuery(Guid CustomerId, string? Cursor, int Limit)
    : IQuery<CursorPage<OrderSummaryDto>>;

// Level 1. §6.6 rewrites this pair in place when the projection arrives —
// they are one slice at two points in its life, not two slices.
public sealed record OrderSummaryDto(
    Guid OrderId, string Status, decimal Total, string Currency,
    int LineCount, DateTimeOffset PlacedAt);

public sealed class GetOrderSummariesHandler(IDbConnectionFactory connections)
    : IQueryHandler<GetOrderSummariesQuery, CursorPage<OrderSummaryDto>>
{
    private const string Sql =
        """
        SELECT TOP (@Take)
                o.Id            AS OrderId,
                o.Status        AS Status,
                o.TotalAmount   AS Total,
                o.Currency      AS Currency,
                COUNT(l.Id)     AS LineCount,
                o.PlacedAt      AS PlacedAt
        FROM    ordering.Orders      o
        JOIN    ordering.OrderLines  l ON l.OrderId = o.Id
        WHERE   o.CustomerId = @CustomerId
          AND   (@AfterPlacedAt IS NULL
                 OR o.PlacedAt < @AfterPlacedAt
                 OR (o.PlacedAt = @AfterPlacedAt AND o.Id < @AfterId))
        GROUP BY o.Id, o.Status, o.TotalAmount, o.Currency, o.PlacedAt
        ORDER BY o.PlacedAt DESC, o.Id DESC;
        """;

    public async Task<CursorPage<OrderSummaryDto>> HandleAsync(
        GetOrderSummariesQuery query, CancellationToken ct)
    {
        var limit  = Math.Clamp(query.Limit, 1, 100);
        var after  = Cursor.Decode(query.Cursor);
        using var connection = connections.Create();

        // Fetch one extra row to determine whether a next page exists,
        // without a second COUNT(*) over the whole table.
        var rows = (await connection.QueryAsync<OrderSummaryDto>(
            new CommandDefinition(Sql, new
            {
                query.CustomerId,
                Take          = limit + 1,
                AfterPlacedAt = after?.PlacedAt,
                AfterId       = after?.Id
            }, cancellationToken: ct))).AsList();

        var hasMore = rows.Count > limit;
        var items   = hasMore ? rows.GetRange(0, limit) : rows;
        var next    = hasMore && items.Count > 0
            ? Cursor.Encode(items[^1].PlacedAt, items[^1].OrderId)
            : null;

        return new CursorPage<OrderSummaryDto>(items, next);
    }
}
```

> **Decision — cursor pagination is the default; `page`/`pageSize` is not.** See [ADR-016](#adr-016--cursor-pagination-by-default).
> `OFFSET @n ROWS` requires SQL Server to produce and discard every skipped row,
> so page 500 costs roughly 500 times page 1. Worse, rows inserted while a user
> pages cause items to be skipped or repeated. A keyset cursor over
> `(PlacedAt DESC, Id DESC)` reads the same number of rows for every page and is
> stable under concurrent inserts.
>
> The cursor is **opaque** — base64 of the sort key plus the tiebreaker ID — so
> the sort strategy stays an implementation detail rather than a public contract.
> The tiebreaker is required: without it, rows sharing a `PlacedAt` value
> straddle the page boundary unpredictably.
>
> Offset pagination remains acceptable for a genuinely bounded admin list where
> jumping to an arbitrary page number is a real requirement. It is not the
> default.

Rules for the read side:

- Dapper, not EF Core. No change tracking, no lazy loading, no accidental N+1.
- The query returns exactly the shape the caller needs. No generic DTO reused
  across six endpoints.
- Never `SELECT *`. Column lists are a contract.
- Pagination is mandatory on any collection endpoint, and cursor-based by
  default. There is no such thing as a small table in production.
- `limit` is clamped server-side. A client asking for 100,000 rows gets 100.
- Avoid `COUNT(*)` alongside a page. Fetching `limit + 1` rows answers "is there
  more?" without scanning the table. Return a total only where the UI genuinely
  displays one.
- Query handlers never mutate anything and never run inside the transaction
  behaviour.

### 6.6 The progression — escalating Ordering to a physical split

Level 1 stops working when one of these becomes true, and not before:

- The read query needs data the write model does not store in a queryable shape.
- Read load contends with write load on the same tables.
- The query joins across so many tables that it cannot be made fast.
- Reads and writes need to scale independently.

For **Ordering**, the trigger is the customer order history screen: it needs
product names and images, which live in Catalog and are not in the Ordering
database at all. Joining across services is impossible; calling Catalog per row
is an N+1 over the network.

The upgrade adds denormalised tables inside Ordering's own database, kept
current by projections. Two of them, serving different paths:

| Table | Fed by | Read by |
|---|---|---|
| `ordering.OrderSummaries` | Ordering's own `OrderPlacedDomainEvent` + Catalog's `ProductPublished` | The escalated history query, below — **not** §6.5's, which stays at level 1 |
| `ordering.ProductPrices` | Catalog's `PriceChanged`, `ProductPublished`, `ProductDiscontinued` | `IProductPriceReader`, on the **write** path (§6.4) |

The second is the more consequential. A read model that only backs a screen can
be stale with mild consequences; one that backs a command handler is what keeps
that handler from making a network call inside a transaction.

```mermaid
graph LR
    subgraph Ordering
        CMD[Command handlers] --> WDB[(Write tables<br/>Orders, OrderLines)]
        CMD --> OB[(Outbox)]
        OB -.->|local lane, after commit| PROJ[OrderSummaryProjection]
        CAT_EV[[ProductPublished<br/>PriceChanged<br/>ProductDiscontinued]] --> PROJ
        CAT_EV --> PP[ProductPriceProjection]
        PROJ --> RDB[(OrderSummaries)]
        PP --> PDB[(ProductPrices)]
        RDB --> QRY[Query handlers]
        PDB --> CMD
    end
```

Note the direction of that last edge: `ProductPrices` feeds the **command**
side. It is the only read model in this design that a write path depends on,
which is why the next section treats its staleness as a business question
rather than a display one.

The read table carries denormalised copies of the fields it needs:

```sql
-- Only the three columns every event carries are NOT NULL. The rest arrive
-- with OrderPlaced, and §9.4 does not guarantee that OrderPlaced is claimed
-- first: a status event that beats it inserts a row identified only by id,
-- status and time. PlacedAt IS NULL is what marks such a row incomplete.
CREATE TABLE ordering.OrderSummaries
(
    OrderId         UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    Status          VARCHAR(32)      NOT NULL,
    UpdatedAt       DATETIMEOFFSET   NOT NULL,

    CustomerId      UNIQUEIDENTIFIER NULL,
    TotalAmount     DECIMAL(19,4)    NULL,
    Currency        CHAR(3)          NULL,
    LineCount       INT              NULL,
    -- One JSON array of {id, name, thumb}, not three parallel arrays: the
    -- ProductPublished handler has to find the element for a given product id
    -- and update it in place, which needs the id alongside the copied fields.
    Products        NVARCHAR(MAX)    NULL,
    PlacedAt        DATETIMEOFFSET   NULL,

    -- Set when the order reaches those states. ConfirmedAt is what makes
    -- fulfilment duration measurable from the row rather than from whichever
    -- handler happened to see both ends; CancelReason is the metric's tag, and
    -- is worth a column anyway — "why was my order cancelled" is a question the
    -- history screen should answer.
    ConfirmedAt     DATETIMEOFFSET   NULL,
    CancelReason    VARCHAR(32)      NULL,

    -- Counted-once flags (§13.3). A business counter is not idempotent, so the
    -- fact that it fired is state like any other.
    PlacedCounted     BIT NOT NULL CONSTRAINT DF_Summaries_Placed     DEFAULT 0,
    CancelledCounted  BIT NOT NULL CONSTRAINT DF_Summaries_Cancelled  DEFAULT 0,
    FulfilmentCounted BIT NOT NULL CONSTRAINT DF_Summaries_Fulfilment DEFAULT 0
);

CREATE INDEX IX_OrderSummaries_Customer_PlacedAt
    ON ordering.OrderSummaries (CustomerId, PlacedAt DESC)
    INCLUDE (Status, TotalAmount, Currency, LineCount);
```

The price table is smaller and hotter — it is read on every `PlaceOrder`:

```sql
CREATE TABLE ordering.ProductPrices
(
    ProductId    UNIQUEIDENTIFIER NOT NULL,
    Currency     CHAR(3)          NOT NULL,
    Amount       DECIMAL(19,4)    NOT NULL,
    IsAvailable  BIT              NOT NULL DEFAULT 1,
    UpdatedAt    DATETIMEOFFSET   NOT NULL,
    CONSTRAINT PK_ProductPrices PRIMARY KEY (ProductId, Currency)
);
```

`IsAvailable` rather than deleting on `ProductDiscontinued`: an order already
placed must still be explicable months later, and a row that vanishes takes its
price history with it.

```csharp
// Infrastructure, not Application: raw SQL and a connection factory. Registered
// by AddOrderingInfrastructure's scan (§6.2) — Application's scan would not
// see it.
namespace Ordering.Infrastructure.Projections;

public sealed class ProductPriceProjection(IDbConnectionFactory connections)
    : IIntegrationEventHandler<ProductPublished>,
      IIntegrationEventHandler<PriceChanged>,
      IIntegrationEventHandler<ProductDiscontinued>
{
    private const string UpsertSql =
        """
        MERGE ordering.ProductPrices AS target
        USING (SELECT @ProductId AS ProductId, @Currency AS Currency) AS source
           ON target.ProductId = source.ProductId AND target.Currency = source.Currency
        WHEN NOT MATCHED THEN
            INSERT (ProductId, Currency, Amount, IsAvailable, UpdatedAt)
            VALUES (@ProductId, @Currency, @Amount, 1, @OccurredAt)
        -- Same out-of-order guard as OrderSummaries: a retried stale event
        -- must not overwrite a newer price.
        WHEN MATCHED AND target.UpdatedAt < @OccurredAt THEN
            UPDATE SET Amount = @Amount, IsAvailable = 1, UpdatedAt = @OccurredAt;
        """;

    private const string DiscontinueSql =
        """
        UPDATE ordering.ProductPrices
        SET    IsAvailable = 0, UpdatedAt = @OccurredAt
        WHERE  ProductId = @ProductId AND UpdatedAt < @OccurredAt;
        """;

    public Task HandleAsync(ProductPublished e, CancellationToken ct) =>
        ExecuteAsync(UpsertSql, new { e.ProductId, e.Currency, e.Amount, e.OccurredAt }, ct);

    public Task HandleAsync(PriceChanged e, CancellationToken ct) =>
        ExecuteAsync(UpsertSql, new { e.ProductId, e.Currency, e.Amount, e.OccurredAt }, ct);

    public Task HandleAsync(ProductDiscontinued e, CancellationToken ct) =>
        ExecuteAsync(DiscontinueSql, new { e.ProductId, e.OccurredAt }, ct);

    private async Task ExecuteAsync(string sql, object parameters, CancellationToken ct)
    {
        using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
    }
}
```

`ProjectedPriceReader` (§6.4) filters on `IsAvailable = 1`, so a discontinued
product produces the same `ProductsUnavailable` failure as one that was never
published — which is what the customer experiences either way.

> **A projection with no publisher is worse than a remote call.** If Catalog has
> never emitted `ProductPublished` for a product, this table has no row for it
> and every order containing it fails — silently, with a plain validation
> message and no error in any log. Two mitigations, both worth having: Catalog
> republishes its full catalogue on demand (an operational task, not a code
> path), and the §13.6 alert on business volume catches the case where orders
> stop for a reason no technical metric shows.

The projection reacts to two different sources, so it implements two different
interfaces (§9.4): `IProjectionHandler<T>` for this service's own events,
arriving after commit through the local outbox lane, and
`IIntegrationEventHandler<T>` for Catalog's events, arriving from the broker.

Both run **after** the originating transaction has committed, on their own
connection. That is deliberate — a projection must never run inside the write
transaction (§7.5), because it would deadlock against the locks that
transaction still holds and would turn a read-model bug into a write-path
failure. The cost is a few milliseconds of lag; the benefit is that a broken
projection can be fixed and replayed without touching the write path.

Both must therefore be idempotent:

```csharp
namespace Ordering.Infrastructure.Projections;

public sealed class OrderSummaryProjection(
    IDbConnectionFactory connections, OrderMetrics metrics)
    // Every lifecycle event, not just the first. A projection that handles
    // only creation shows a status frozen at whatever the aggregate was when
    // it was born — and the SQL still looks correct, because the UPDATE branch
    // exists and simply never fires.
    : IProjectionHandler<OrderPlacedDomainEvent>,
      IProjectionHandler<OrderStockConfirmedDomainEvent>,
      IProjectionHandler<OrderConfirmedDomainEvent>,
      IProjectionHandler<OrderShippedDomainEvent>,
      IProjectionHandler<OrderCancelledDomainEvent>,
      IIntegrationEventHandler<ProductPublished>  // Catalog's event, from the broker
{
    public async Task HandleAsync(OrderPlacedDomainEvent e, CancellationToken ct)
    {
        using var connection = connections.Create();
        await connection.ExecuteAsync(
            """
            MERGE ordering.OrderSummaries AS target
            USING (SELECT @OrderId AS OrderId) AS source
               ON target.OrderId = source.OrderId
            WHEN NOT MATCHED THEN
                INSERT (OrderId, CustomerId, Status, TotalAmount, Currency,
                        LineCount, Products, PlacedAt, UpdatedAt)
                VALUES (@OrderId, @CustomerId, @Status, @Total, @Currency,
                        @LineCount, @Products, @PlacedAt, @UpdatedAt)
            -- PlacedAt IS NULL, not an UpdatedAt guard: the row exists because
            -- a status event arrived first, and the descriptive columns have
            -- never been written. Matching on that condition fires exactly
            -- once — a redelivery finds PlacedAt set and does nothing, which
            -- is what keeps the counter below honest.
            WHEN MATCHED AND target.PlacedAt IS NULL THEN
                UPDATE SET CustomerId  = @CustomerId,
                           TotalAmount = @Total,
                           Currency    = @Currency,
                           LineCount   = @LineCount,
                           Products    = @Products,
                           PlacedAt    = @PlacedAt,
                           -- The facts above are immutable and always safe to
                           -- write. Status is not: something later already set
                           -- it, and this event is the older one.
                           Status      = CASE WHEN target.UpdatedAt < @UpdatedAt
                                              THEN @Status ELSE target.Status END,
                           UpdatedAt   = CASE WHEN target.UpdatedAt < @UpdatedAt
                                              THEN @UpdatedAt ELSE target.UpdatedAt END;
            """,
            new
            {
                OrderId    = e.OrderId.Value,
                CustomerId = e.CustomerId.Value,
                Status     = nameof(OrderStatus.AwaitingStock),
                Total      = e.Total.Amount,
                Currency   = e.Total.Currency,
                LineCount  = e.Lines.Count,
                // Ids are known now; name and thumbnail arrive with
                // ProductPublished and are patched in below.
                Products   = JsonSerializer.Serialize(
                                 e.Lines.Select(l => new { id = l.ProductId.Value,
                                                           name = "", thumb = "" })),
                PlacedAt   = e.OccurredAt,
                UpdatedAt  = e.OccurredAt
            });

        // Not "if (applied > 0) metrics.Placed(...)". This row may have been
        // created by a status event that outran its OrderPlaced, in which case
        // a cancellation is already sitting on it uncounted — and an
        // OrderConfirmed may be too. One call records whatever is now true.
        await RecordPendingFactsAsync(connection, e.OrderId);
    }

    // The status transitions. One statement, because they differ only in the
    // value written — and because a per-event copy is how one of them ends up
    // missing the out-of-order guard.
    //
    // OrderStockConfirmed is handled here but deliberately absent from §9.3's
    // publish allow-list: AwaitingPayment is a state the customer sees on their
    // own history screen and no other service has any business knowing.
    public Task HandleAsync(OrderStockConfirmedDomainEvent e, CancellationToken ct) =>
        SetStatusAsync(e.OrderId, OrderStatus.AwaitingPayment, e.OccurredAt);

    public Task HandleAsync(OrderConfirmedDomainEvent e, CancellationToken ct) =>
        SetStatusAsync(e.OrderId, OrderStatus.Confirmed, e.OccurredAt,
                       confirmedAt: e.OccurredAt);

    public Task HandleAsync(OrderShippedDomainEvent e, CancellationToken ct) =>
        SetStatusAsync(e.OrderId, OrderStatus.Shipped, e.OccurredAt);

    public Task HandleAsync(OrderCancelledDomainEvent e, CancellationToken ct) =>
        // The wire code, not the enum: a metric tag is a string either way, and
        // ToString() on an enum makes its member names the dimension values —
        // renaming a member would silently split the series in two (§13.3).
        SetStatusAsync(e.OrderId, OrderStatus.Cancelled, e.OccurredAt,
                       cancelReason: CancellationReasons.ToCode(e.Reason));

    // Returns nothing. It used to return rows affected, for callers that
    // decided whether to count a metric from it — and that is precisely the
    // reasoning RecordPendingFactsAsync replaced. Handing the next reader an
    // `applied` on a status write is an invitation to write `if (applied > 0)`
    // again, which is the bug, not the fix.
    private async Task SetStatusAsync(
        OrderId orderId, OrderStatus status, DateTimeOffset occurredAt,
        DateTimeOffset? confirmedAt = null, string? cancelReason = null)
    {
        using var connection = connections.Create();

        await connection.ExecuteAsync(
            """
            MERGE ordering.OrderSummaries AS target
            USING (SELECT @OrderId AS OrderId) AS source
               ON target.OrderId = source.OrderId
            -- An UPDATE here would be the whole defect: §9.4 claims ordering
            -- is not required, and a Cancelled claimed before its OrderPlaced
            -- would match no row, change nothing, and be marked processed. The
            -- order would read AwaitingStock for ever, with no error anywhere.
            WHEN NOT MATCHED THEN
                INSERT (OrderId, Status, UpdatedAt, ConfirmedAt, CancelReason)
                VALUES (@OrderId, @Status, @OccurredAt, @ConfirmedAt, @CancelReason)
            -- The guard that makes this safe under at-least-once delivery:
            -- a redelivered Confirmed must not undo a Shipped that followed.
            WHEN MATCHED AND target.UpdatedAt < @OccurredAt THEN
                UPDATE SET Status       = @Status,
                           UpdatedAt    = @OccurredAt,
                           -- COALESCE, not assignment: Shipped follows
                           -- Confirmed and passes NULL, and overwriting would
                           -- erase the timestamp the duration is measured from.
                           ConfirmedAt  = COALESCE(@ConfirmedAt,  target.ConfirmedAt),
                           CancelReason = COALESCE(@CancelReason, target.CancelReason);
            """,
            new { OrderId = orderId.Value, Status = status.ToString(),
                  occurredAt, confirmedAt, cancelReason });

        await RecordPendingFactsAsync(connection, orderId);
    }

    /// <summary>
    /// Records every business fact the row now supports and has not yet been
    /// counted for. Called after each write, because any write can be the one
    /// that completes a pair.
    /// </summary>
    private async Task RecordPendingFactsAsync(IDbConnection connection, OrderId orderId)
    {
        // Each statement is an atomic claim: the flag flips and the values come
        // back in one UPDATE, so two dispatcher replicas racing the same order
        // record it once. This is the outbox's lease idiom (§9.4) applied to a
        // counter — a metric is not idempotent, so "it already fired" is state.
        var args = new { OrderId = orderId.Value };

        // PlacedAt is the predicate, but TotalAmount and Currency are what come
        // back — non-null only because the MERGE above writes all three in one
        // statement. Keep them in one statement: a future split that sets
        // PlacedAt earlier would hand this a NULL decimal, and PlacedFact has
        // nowhere to put it (Appendix D.5).
        var placed = await connection.QuerySingleOrDefaultAsync<PlacedFact>(
            """
            UPDATE ordering.OrderSummaries SET PlacedCounted = 1
            OUTPUT inserted.TotalAmount, inserted.Currency
            WHERE  OrderId = @OrderId AND PlacedAt IS NOT NULL AND PlacedCounted = 0;
            """, args);

        // Money.Of, not new Money: the constructor is private (§5.3) and Of is
        // the normalising way in. CHAR(3) comes back space-padded, which is
        // exactly the input the factory exists to clean.
        if (placed is not null)
            metrics.Placed(Money.Of(placed.TotalAmount, placed.Currency.Trim()));

        // PlacedCounted = 1 in the predicate, not merely PlacedAt IS NOT NULL:
        // a cancellation must never be counted before the placement it belongs
        // to. Ordering is not guaranteed on the lane (§9.4), and `cancelled`
        // exceeding `placed` is a state the write model cannot reach — a
        // reconciliation that finds it should be finding a real defect.
        var cancelled = await connection.QuerySingleOrDefaultAsync<string>(
            """
            UPDATE ordering.OrderSummaries SET CancelledCounted = 1
            OUTPUT inserted.CancelReason
            WHERE  OrderId = @OrderId AND PlacedCounted = 1
                   AND CancelReason IS NOT NULL AND CancelledCounted = 0;
            """, args);

        if (cancelled is not null)
            metrics.Cancelled(cancelled);

        var fulfilment = await connection.QuerySingleOrDefaultAsync<FulfilmentFact>(
            """
            UPDATE ordering.OrderSummaries SET FulfilmentCounted = 1
            OUTPUT inserted.PlacedAt, inserted.ConfirmedAt
            WHERE  OrderId = @OrderId AND PlacedAt IS NOT NULL
                   AND ConfirmedAt IS NOT NULL AND FulfilmentCounted = 0;
            """, args);

        if (fulfilment is not null)
            metrics.Fulfilled(fulfilment.ConfirmedAt - fulfilment.PlacedAt);
    }

    public async Task HandleAsync(ProductPublished e, CancellationToken ct)
    {
        // Patch the element for this product in place, in every summary that
        // contains it. OPENJSON gives the array index; JSON_MODIFY needs it.
        // The UpdatedAt guard keeps a stale republish from overwriting a
        // newer name, as everywhere else in §6.6.
        using var connection = connections.Create();
        await connection.ExecuteAsync(
            """
            UPDATE s
            SET    s.Products = JSON_MODIFY(
                                    JSON_MODIFY(s.Products,
                                        '$[' + CAST(j.[key] AS varchar(10)) + '].name',  @Name),
                                        '$[' + CAST(j.[key] AS varchar(10)) + '].thumb', @Thumbnail),
                   s.UpdatedAt = @OccurredAt
            FROM   ordering.OrderSummaries s
            CROSS APPLY OPENJSON(s.Products) j
            WHERE  JSON_VALUE(j.value, '$.id') = @ProductId
              AND  s.UpdatedAt < @OccurredAt;
            """,
            new { ProductId = e.ProductId, Name = e.Name, Thumbnail = e.ThumbnailUrl, e.OccurredAt });
    }
}
```

> **This handler is the expensive one, and the reason to think twice before
> denormalising a name.** `OrderPlacedDomainEvent` writes one row; a single
> `ProductPublished` scans every summary that ever contained that product.
> Joining at read time is not an option — the products live in Catalog — so
> denormalisation moved the cost from every read to every rename. That is the
> right trade only while renames are rare, and this is the first thing that
> breaks if they stop being.

Three details that are easy to miss and expensive to discover later:

- **The `MERGE` is idempotent.** Redelivery of `OrderPlacedDomainEvent` inserts
  nothing new.
- **`UpdatedAt < @UpdatedAt` guards against out-of-order delivery.** Messages
  can and do arrive out of sequence, especially after a retry. Without this
  check a redelivered `AwaitingPayment` overwrites a `Confirmed` that already
  followed it — and because all five lifecycle events now feed this table
  (above), that is a sequence the projection genuinely sees rather than a
  hypothetical.
- **Every statement here inserts when the row is absent.** Redelivery and
  reordering are different problems, and the `UpdatedAt` guard only solves the
  first. An event that arrives *early* matches nothing, and an `UPDATE` would
  discard it in silence — no error, no retry, and a summary frozen at whatever
  state it reached. The `WHEN NOT MATCHED` branch is what lets §9.4 keep saying
  ordering is not required.

#### Counting is a claim, not a call

The counters in `RecordPendingFactsAsync` deserve their own note, because the
shape looks like ceremony until the alternative is written out.

An event handler that increments a counter is recording *"this message
arrived"*. The business wants *"this order was placed"*, and those two coincide
only when delivery is exactly-once and ordered — which §9.4 states it is not.
Every simpler version of this code fails one of the two:

| Approach | Fails on |
|---|---|
| Count in the handler | Redelivery. Two messages, two increments, one order |
| Count on rows-affected | Reordering. A cancellation counted before the placement it belongs to, and permanently orphaned if that placement is later abandoned |
| Count on rows-affected, plus an ordering assumption | Nothing, until the assumption stops holding — silently, and only under the load that makes the metric interesting |

The claim pattern survives all three, because it asks the row rather than the
message. Its cost is honest and worth stating: **three extra statements per
projection write**, on the same connection, none of them indexed lookups beyond
the primary key. That is real, and it buys a number a finance team can reconcile
against the write model. If the write volume ever makes it not worth paying,
the thing to change is the frequency — a periodic sweep claiming in batches —
not the correctness.

**It does not survive everything, and the case it loses is worth naming.** The
cancellation claim requires `PlacedCounted = 1`. If the `OrderPlaced` row is
abandoned after `MaxAttempts` (§9.4 permits this and alerts on it), that flag
never flips, and the cancellation is never counted at all. A phantom
cancellation was traded for a missing one.

That is the right direction to fail in — `cancelled > placed` is a state the
write model cannot reach, and a metric that reports it is worse than one that
under-reports — but "the right direction" is not "no consequence", and a
permanent silent drop is the same defect §13.3 describes the old fulfilment
guard having. The difference is that this one is bounded by an alert that
already exists: a row reaches `MaxAttempts` only by failing ten times (§9.4),
across ten leases with a growing gap between them, which the abandoned-row
alert (§13.6) pages on. Ten dispatcher attempts, not the five of
`UseMessageRetry` (§9.8) — that limit governs a consumer redelivering a message
it already received, and a row that never left the outbox has not reached one. **The metric's correctness therefore
depends on that alert being answered**, which is a dependency worth stating out
loud rather than a property of the pattern.

Replaying an abandoned row after the fix is what closes it, and the claims make
that safe: replay flips `PlacedCounted`, the next write claims the cancellation
behind it, and nothing double-counts because every flag is already set.

#### The read side, which is the point

A projection nothing queries is cost without benefit. §6.5's handler is the
level-1 version — it joins the write tables and returns no product data,
because it has none to return. Escalating replaces it:

```csharp
// EDITS the types in §6.5 — same namespace, same names. Escalating a query is
// a change to one slice, not a second slice alongside it. After this, the
// level-1 versions no longer exist.
namespace Ordering.Application.Orders.GetOrderSummaries;

// The fields §6.6 exists for. Level 1 could not return these at any price:
// the names and images live in Catalog.
public sealed record OrderSummaryDto(
    Guid OrderId, string Status, decimal Total, string Currency,
    int LineCount, DateTimeOffset PlacedAt,
    IReadOnlyList<SummaryProduct> Products);

public sealed record SummaryProduct(Guid Id, string Name, string Thumb);

public sealed class GetOrderSummariesHandler(IDbConnectionFactory connections)
    : IQueryHandler<GetOrderSummariesQuery, CursorPage<OrderSummaryDto>>
{
    // One table, no joins, no aggregation — the projection did that work once
    // at write time. Compare the level-1 query in §6.5, which groups over
    // OrderLines on every read.
    private const string Sql =
        """
        SELECT TOP (@Take)
               OrderId, Status, TotalAmount AS Total, Currency,
               LineCount, PlacedAt, Products
        FROM   ordering.OrderSummaries
        -- Also excludes incomplete rows: a summary created by a status event
        -- that outran its OrderPlaced has a NULL CustomerId and matches no
        -- customer, so a half-built order is never returned. It becomes
        -- visible the moment the MERGE above fills it in.
        WHERE  CustomerId = @CustomerId
          AND  (@AfterPlacedAt IS NULL
                OR PlacedAt < @AfterPlacedAt
                OR (PlacedAt = @AfterPlacedAt AND OrderId < @AfterId))
        ORDER BY PlacedAt DESC, OrderId DESC;
        """;

    public async Task<CursorPage<OrderSummaryDto>> HandleAsync(
        GetOrderSummariesQuery query, CancellationToken ct)
    {
        var limit = Math.Clamp(query.Limit, 1, 100);
        var after = Cursor.Decode(query.Cursor);
        using var connection = connections.Create();

        var rows = (await connection.QueryAsync<SummaryRow>(new CommandDefinition(
            Sql, new { query.CustomerId, Take = limit + 1,
                       AfterPlacedAt = after?.PlacedAt, AfterId = after?.Id },
            cancellationToken: ct))).AsList();

        var hasMore = rows.Count > limit;
        var items = (hasMore ? rows.GetRange(0, limit) : rows)
            .Select(r => new OrderSummaryDto(
                r.OrderId, r.Status, r.Total, r.Currency, r.LineCount, r.PlacedAt,
                JsonSerializer.Deserialize<SummaryProduct[]>(r.Products)!))
            .ToArray();

        var next = hasMore && items.Length > 0
            ? Cursor.Encode(items[^1].PlacedAt, items[^1].OrderId)
            : null;

        return new CursorPage<OrderSummaryDto>(items, next);
    }
}
```

The index from the DDL above — `(CustomerId, PlacedAt DESC)` including the
scalar columns — serves the seek, the ordering and the cursor predicate. It is
**not** fully covering: `Products` is `NVARCHAR(MAX)` and is left out
deliberately, so each row costs a lookup. That is the right trade at a page of
twenty and the wrong one at a page of a thousand, which is another reason the
`limit` is clamped (§6.5).

The benefit being bought is visible in the shape of the query: one table, no
join, no `GROUP BY`, and a page size that bounds the work. Level 1 aggregates
`OrderLines` on every read and still cannot return a product name at any price.

The API must now expose the staleness rather than hide it — for example, by
returning the write-model status on the order detail endpoint (strongly
consistent, single-row read) while the list endpoint serves from the projection.

> **Trap — projecting everything by default.** Each projection is a second copy
> of the truth, with its own bugs, its own rebuild procedure and its own
> monitoring. Add one when a measurement demands it. Keep the rebuild script in
> source control from day one, because you will need it.

---

## 7. Persistence

### 7.1 Database per service

Each service owns a SQL Server database. No shared tables, no cross-database
joins, no views into another service's data, no shared read-only user.

For smaller deployments, one SQL Server instance hosting six databases is
acceptable — the isolation that matters is logical. Physical separation is a
scaling and blast-radius decision that can be made later, because nothing in the
code depends on it. Using *schemas* within one shared database instead is the
option to avoid: it makes cross-schema joins possible, and something will
eventually write one.

Each service uses its own SQL login with permissions to its database only. This
turns principle 1 from a convention into something the database enforces.

#### Two identities per database

One login is not enough. Each service database has **two** principals with
different rights, used by different processes:

| Identity | Used by | Rights | Rationale |
|---|---|---|---|
| **Runtime** | The API and worker pods | `SELECT`/`INSERT`/`UPDATE`/`DELETE` on business, outbox and inbox tables. **No DDL.** | The application never alters schema, so it should be unable to. A SQL injection flaw or a compromised pod cannot drop a table |
| **Migrator** | The `*.Migrator` job only | DDL on its own database | Elevated rights exist for the seconds the job runs, in a process with no network listener and no user input |

The role grants are the same either way; **how the principal is created is not**,
and the difference is the one that stops a copy-pasted script at the first
semicolon. Managed environments:

```sql
-- Azure SQL / SQL Server with Entra auth: the principal exists in the
-- directory, the database only maps to it. No password anywhere.
CREATE USER [ordering-runtime]  FROM EXTERNAL PROVIDER;
CREATE USER [ordering-migrator] FROM EXTERNAL PROVIDER;
```

Compose, the CI service container, and any SQL Server without a directory behind
it (§14.1):

```sql
-- Server-level login, then a database user mapped to it.
CREATE LOGIN [ordering-runtime]  WITH PASSWORD = '$(OrderingRuntimePassword)';
CREATE LOGIN [ordering-migrator] WITH PASSWORD = '$(OrderingMigratorPassword)';
GO
USE [Ordering];
CREATE USER [ordering-runtime]  FOR LOGIN [ordering-runtime];
CREATE USER [ordering-migrator] FOR LOGIN [ordering-migrator];
```

```sql
-- Identical from here, and the only part worth reviewing.
-- Runtime: data plane only.
ALTER ROLE db_datareader ADD MEMBER [ordering-runtime];
ALTER ROLE db_datawriter ADD MEMBER [ordering-runtime];

-- Migrator: schema plane, used by the pre-deploy job only.
ALTER ROLE db_ddladmin   ADD MEMBER [ordering-migrator];
ALTER ROLE db_datawriter ADD MEMBER [ordering-migrator];   -- for data backfills
```

> **Do not let local convenience set the production shape.** The temptation is
> to run everything as `sa` locally because it is one line, and the cost lands
> months later when nobody can say whether the runtime identity has ever been
> tested without DDL. Compose seeds both logins from the same script the cloud
> path uses below the divide, so the restriction is exercised on every developer
> machine, where a `CREATE TABLE` in application code fails immediately and for
> the same reason it would in production.

This means **two connection strings per service**, held in different secrets and
mounted into different workloads. The migrator's secret is never present in an
API pod. Configuration shape:

```
ConnectionStrings__Ordering           → runtime identity  (API, workers)
ConnectionStrings__OrderingMigrator   → migrator identity (Job only)
```

The split costs one extra secret and pays for itself the first time someone
reviews what an application-tier compromise could actually reach.

### 7.2 EF Core for the write side

`DbContext` is an implementation detail of Infrastructure. Configuration lives
in `IEntityTypeConfiguration<T>` classes, never in attributes on domain types —
attributes would put an EF Core dependency in the Domain project.

```csharp
internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders", "ordering");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.Id)
               .HasConversion(id => id.Value, value => new OrderId(value))
               .ValueGeneratedNever();

        builder.Property(o => o.CustomerId)
               .HasConversion(id => id.Value, value => new CustomerId(value));

        builder.Property(o => o.Status)
               .HasConversion<string>()
               .HasMaxLength(32);

        // Value object mapped as a complex type — columns on the same table,
        // no identity, exactly matching the domain semantics.
        builder.ComplexProperty(o => o.ShippingAddress, address =>
        {
            address.Property(a => a.Line1).HasColumnName("ShipLine1").HasMaxLength(200);
            address.Property(a => a.City).HasColumnName("ShipCity").HasMaxLength(100);
            address.Property(a => a.PostCode).HasColumnName("ShipPostCode").HasMaxLength(20);
            address.Property(a => a.Country).HasColumnName("ShipCountry").HasMaxLength(2);
        });

        // Backing field, not the public read-only property.
        builder.Metadata
               .FindNavigation(nameof(Order.Lines))!
               .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.OwnsMany<OrderLine>("_lines", line =>
        {
            line.ToTable("OrderLines", "ordering");
            line.WithOwner().HasForeignKey("OrderId");
            line.Property<Guid>("Id");
            line.HasKey("Id");

            line.ComplexProperty(l => l.UnitPrice, money =>
            {
                money.Property(m => m.Amount).HasColumnName("UnitAmount").HasPrecision(19, 4);
                money.Property(m => m.Currency).HasColumnName("Currency").HasMaxLength(3);
            });
        });

        // Optimistic concurrency — SQL Server maintains this automatically.
        builder.Property(o => o.Version).IsRowVersion();

        builder.HasIndex(o => o.CustomerId);
        builder.HasIndex(o => new { o.Status, o.PlacedAt });

        builder.Ignore(o => o.DomainEvents);
        builder.Ignore(o => o.Total);       // Computed, not stored.
    }
}
```

Global conventions cover what would otherwise be repeated in every file:

```csharp
protected override void ConfigureConventions(ModelConfigurationBuilder builder)
{
    builder.Properties<decimal>().HavePrecision(19, 4);
    builder.Properties<string>().HaveMaxLength(400);
    builder.Properties<DateTimeOffset>().HaveColumnType("datetimeoffset(7)");
}
```

Unbounded `NVARCHAR(MAX)` columns are a common and avoidable source of both
storage bloat and index limitations; defaulting `string` to a bounded length
turns "someone forgot" into a compile-time-visible override.

### 7.3 Concurrency

Optimistic concurrency is the default and is enough for most aggregates. The
`rowversion` column means a stale write throws `DbUpdateConcurrencyException`,
which the API translates to `409 Conflict`.

Inventory is the exception. Stock reservation is genuinely contended — the same
SKU may be reserved by many concurrent orders — and optimistic retry loops
degrade badly under that load. There, use a targeted pessimistic update:

```sql
UPDATE inventory.StockItems
SET    Available = Available - @Quantity,
       Reserved  = Reserved  + @Quantity,
       UpdatedAt = SYSDATETIMEOFFSET()
OUTPUT inserted.Available
WHERE  ProductId = @ProductId
  AND  Available >= @Quantity;
```

The `WHERE Available >= @Quantity` makes the check and the decrement a single
atomic statement. If it affects zero rows, there was not enough stock — no read,
no race, no retry loop.

### 7.4 Migrations

#### What EF generates, and what you write by hand

Two kinds of table live in a service database, and they are authored
differently:

| Kind | Examples | Authored by |
|---|---|---|
| **Write model** | `Orders`, `OrderLines` | The EF model. `IEntityTypeConfiguration<T>` (§7.2) is the source of truth; `dotnet ef migrations add` produces the DDL |
| **Read models and technical tables** | `OrderSummaries`, `ProductPrices`, `OutboxMessages`, `InboxMessages`, `OrderReviews` | Hand-written DDL, because they are shaped for queries and index plans rather than for objects |

That is why §6.6 and §9.4 show `CREATE TABLE` and §7.2 does not — the write
model's schema is a projection of the aggregate, and duplicating it as SQL would
create two definitions that drift.

**Both kinds ship in the same EF migration.** There is no second mechanism: the
migrator job runs `Database.Migrate()` and nothing else, so hand-written DDL
that is not inside a migration never executes.

```csharp
public partial class AddOrderSummaries : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // EF-generated operations for write-model changes appear here.

        // Hand-written DDL rides along, in the same transaction, applied by
        // the same job, versioned by the same migration history.
        migrationBuilder.Sql(
            """
            CREATE TABLE ordering.OrderSummaries ( /* §6.6 */ );
            CREATE INDEX IX_OrderSummaries_Customer_PlacedAt ...;
            """);
    }
}
```

`OrderFulfilmentStates` (§9.6) is the one table in both categories: MassTransit's
EF saga repository maps it, so EF can generate it — but the DDL is shown
explicitly because the alert in §13.6 and the stuck-saga runbook both query it
directly, and an index nobody declared is an index nobody has.

> **Decision — migrations never run at application startup.** See [ADR-007](#adr-007--migrations-as-a-pre-deploy-job).

`Database.Migrate()` in `Program.cs` seems convenient and fails in exactly the
situations that matter: with three replicas starting simultaneously, three
processes race to apply the same migration; with a rolling deploy, old and new
code run against a half-migrated schema; and the application's runtime identity
needs DDL permissions it should not have.

Instead migrations run as a distinct step that must complete before new pods
receive traffic:

```yaml
apiVersion: batch/v1
kind: Job
metadata:
  name: ordering-migrate-{{ .Values.image.tag }}
  annotations:
    "helm.sh/hook": pre-install,pre-upgrade
    "helm.sh/hook-weight": "-5"
    "helm.sh/hook-delete-policy": before-hook-creation
spec:
  backoffLimit: 2
  template:
    spec:
      restartPolicy: Never
      containers:
        - name: migrate
          image: "{{ .Values.image.registry }}/{{ .Values.image.migrator }}:{{ .Values.image.tag }}"
          env:
            # The MIGRATOR identity (DDL), not the runtime one — §7.1.
            # This secret is mounted only here, never into an API pod.
            - name: ConnectionStrings__OrderingMigrator
              valueFrom:
                secretKeyRef:
                  name: ordering-migrator-secret
                  key: connection-string
```

Because migrations and application code deploy separately, **every migration
must be backward compatible with the currently running version**. Renaming a
column is therefore a multi-release operation: add the new column, write to
both, backfill, switch reads, stop writing the old one, drop it — one release
per step. This is tedious and it is the price of zero-downtime deploys.

### 7.5 The unit of work and domain event dispatch

**This section is the single normative description of how a domain event becomes
an integration event.** §6.3 shows where it is invoked and §9.3 shows the
translation rules; neither describes a separate mechanism.

Domain events are dispatched inside the transaction that persists the state
change, *after* the handler has finished mutating aggregates and *before*
`SaveChanges` — so that the outbox rows they produce commit atomically with the
state that raised them. **Dispatch stages rows; it runs no handlers** (ADR-018).
Nothing reacts to a domain event until the dispatcher picks that row up after
the commit.

The whole flow, and the only one this document describes: collect → map through
the §9.3 allow-list → stage `Broker` and `Local` outbox rows → one
`SaveChanges` → post-commit reaction driven by §9.4. An in-process handler
writing into the same save is exactly what ADR-018 rejects, because it is how a
transaction acquires a second aggregate, a second service's data, or a deadlock
that only appears under load.

#### The collector port

The dispatcher needs to know which aggregates changed, which is EF Core's
change tracker — an Infrastructure concern. Application sees only a port:

```csharp
namespace Common.Application;

public interface IDomainEventCollector
{
    /// <summary>
    /// Returns the domain events raised by every tracked aggregate and clears
    /// them, so a second call after re-entrant work returns only new events.
    /// </summary>
    IReadOnlyList<IDomainEvent> CollectAndClear();
}
```

```csharp
namespace Ordering.Infrastructure.Persistence;

internal sealed class EfDomainEventCollector(OrderingDbContext db) : IDomainEventCollector
{
    public IReadOnlyList<IDomainEvent> CollectAndClear()
    {
        var aggregates = db.ChangeTracker
            .Entries<IHasDomainEvents>()
            .Where(e => e.Entity.DomainEvents.Count > 0)
            .Select(e => e.Entity)
            .ToArray();

        var events = aggregates.SelectMany(a => a.DomainEvents).ToArray();

        // Cleared as they are collected, so a nested dispatch (§6.3's
        // HasActiveTransaction path) sees only events raised since the last
        // call rather than staging these a second time.
        foreach (var aggregate in aggregates)
            aggregate.ClearDomainEvents();

        return events;
    }
}
```

#### The dispatcher

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
    Task DispatchAsync(CancellationToken ct);
}

/// <summary>
/// Answers whether an event type has any registered projection handler, so the
/// dispatcher does not stage Local rows nobody will consume.
/// </summary>
public interface IProjectionRegistry
{
    bool HasHandler(IDomainEvent domainEvent);
}

internal sealed class ProjectionRegistry(IServiceProvider services) : IProjectionRegistry
{
    private static readonly ConcurrentDictionary<Type, bool> Cache = new();

    // Derived from the DI container rather than a hand-maintained list, so it
    // cannot drift from what is actually registered (§6.2).
    public bool HasHandler(IDomainEvent domainEvent) =>
        Cache.GetOrAdd(domainEvent.GetType(), type =>
            services.GetServices(typeof(IProjectionHandler<>).MakeGenericType(type)).Any());
}

internal sealed class DomainEventDispatcher(
    IDomainEventCollector collector,
    IIntegrationEventMapper mapper,
    IIntegrationEventPublisher publisher,
    IProjectionRegistry projections)
    : IDomainEventDispatcher
{
    public async Task DispatchAsync(CancellationToken ct)
    {
        var events = collector.CollectAndClear();
        if (events.Count == 0)
            return;

        // Broker lane: allow-listed events become integration events (§9.3).
        foreach (var integrationEvent in mapper.Map(events))
            await publisher.StageAsync(integrationEvent, OutboxLane.Broker, ct);

        // Local lane: events with a registered projection handler are staged
        // too, so the projection survives a crash immediately after commit.
        foreach (var domainEvent in events.Where(projections.HasHandler))
            await publisher.StageAsync(domainEvent, OutboxLane.Local, ct);
    }
}
```

Deriving the registry from the container matters: a `Local` row is staged
**only** when a handler is registered, and §9.4 throws if a staged `Local` row
then finds none. The two checks read the same source, so a handler that is
implemented but unregistered fails at the first assertion rather than becoming
an invisible no-op.

> **`ProjectionRegistry` must be registered scoped**, not singleton. Handlers are
> scoped (§6.2), and `GetServices` for a scoped service from the root provider
> throws *"Cannot resolve scoped service from root provider"*. The static cache
> is safe across scopes because DI registrations do not change at runtime — it
> memoises a question about the container's shape, not about any instance.

```csharp
services.AddScoped<IProjectionRegistry, ProjectionRegistry>();
services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
```

**The dispatcher performs no I/O beyond staging rows.** It does not invoke a
single handler. That is the change that makes the rest of the design safe.

#### Nothing reacts inside the transaction

> **Decision — all reactions to a domain event happen after commit, driven by
> the outbox. Nothing subscribes to a domain event inside the transaction.** See
> [ADR-018](#adr-018--reactions-happen-after-commit).

The tempting alternative is to run projection handlers in-process before
`SaveChanges`, so the projection commits atomically with the aggregate. It
fails in three ways, and the third is the one that hurts:

1. **A projection that writes on its own connection is a second transaction.**
   It can commit while the aggregate rolls back — leaving a summary row for an
   order that does not exist — or the reverse.
2. **A projection that writes on the *same* `DbContext` is atomic but not
   retryable.** If the projection has a bug, the command fails. A read model
   defect becomes a write-path outage.
3. **Either version deadlocks.** The handler queries and updates the same tables
   the outer transaction still holds locks on, at exactly the moment those locks
   are held. It works under test and fails under load.

Staging to the outbox and reacting after commit costs a few milliseconds of
staleness and buys durability, independent retry, and no lock contention. The
read model was already eventually consistent (§2.4); this makes the lag explicit
rather than pretending it is zero.

#### The ordered flow

```mermaid
sequenceDiagram
    participant B as TransactionBehavior
    participant H as Command handler
    participant A as Aggregate
    participant D as DomainEventDispatcher
    participant U as IUnitOfWork
    participant O as Outbox dispatcher
    participant P as Projection handler
    participant MQ as RabbitMQ

    rect rgb(240, 245, 250)
    Note over B,U: One transaction
    B->>U: begin
    B->>H: handle command
    H->>A: domain method
    A-->>H: events raised in memory
    H-->>B: Result
    B->>D: DispatchAsync
    D->>D: collect + clear, map allow-list
    D->>U: stage outbox rows (Broker + Local lanes)
    B->>U: SaveChangesAsync (aggregate + outbox, one save)
    B->>U: commit
    end

    Note over O,MQ: After commit — retried independently
    O->>P: Local lane → IProjectionHandler
    O->>MQ: Broker lane → publish
```

Stated as rules:

1. Aggregates raise domain events in memory and perform **no I/O**.
2. Command handlers never read `DomainEvents` and never publish anything.
3. `TransactionBehavior` calls the dispatcher **once**, after the handler
   returns successfully and before `SaveChanges`.
4. The dispatcher only **stages outbox rows** — allow-listed events to the
   broker lane (§9.3), events with projection handlers to the local lane.
5. `SaveChangesAsync` persists aggregate changes and outbox rows in **one**
   save; the commit makes both durable together.
6. **Everything else happens after commit**, driven by the outbox dispatcher
   (§9.4) and retried independently of the command that caused it.

Two designs are deliberately rejected:

**No `PendingDomainEvent` table populated by a `SaveChanges` interceptor.** The
interceptor necessarily runs *during* `SaveChanges`, which is too late to
influence that same save, and it duplicates what the outbox already does.

**No in-process domain event handlers.** Domain events are transient signals
within a transaction; the only thing that may consume one is the outbox, which
persists it. If something needs to react, it reacts to a durable row after
commit — not to an in-memory object mid-transaction.

---

## 8. Caching with Redis

### 8.1 What Redis is used for

Redis serves four distinct purposes here. They are worth separating because
they have different failure semantics.

| Use | Pattern | If Redis is unavailable |
|---|---|---|
| Read-through cache | `HybridCache` over query results | Degrade to database; slower but correct |
| Idempotency keys | `SET key NX EX` | **Fail the request** — correctness depends on it |
| Distributed lock | `SET key NX PX` + token-checked release | Fail the operation; do not proceed unlocked |
| Rate limiting | Sliding window counter — **not built in v1** (§10.3) | Fail open or closed — an explicit policy decision |

Only the first tolerates Redis being down. Conflating them behind a single
"cache is optional" assumption produces duplicate charges the first time Redis
restarts.

The fourth row is here as a reserved keyspace rather than a description of
running code: the gateway limits in process, per replica. It is listed because
the decision of *which instance* a shared counter would use is the one worth
recording in advance — coordination, not cache — and because a `{service}:
ratelimit:` key appearing on the cache instance later would look reasonable and
be silently evictable.

#### Eviction policy couples them — separate the keyspaces

This is the subtle one, and it is the reason the four uses need more than a
naming convention between them.

A cache instance is normally configured `maxmemory-policy allkeys-lru`, so that
memory pressure silently evicts cold entries. That policy applies to the
**entire keyspace** — including your distributed locks and your token denylist.
Under load, Redis will happily evict a held lock, and two workers will then both
believe they own it. A revoked token will quietly become valid again.

The failure has no error, no log line, and appears only under the memory
pressure that makes it hardest to reproduce.

| Keyspace | Eviction policy | Placement |
|---|---|---|
| `{service}:cache:` | `allkeys-lru` — eviction is the point | Shared cache instance |
| `{service}:lock:` | **`noeviction`** | Separate instance, or a separate Redis DB index with its own policy |
| `{service}:idem:` | **`noeviction`** | With the locks |
| `{service}:denylist:` | **`noeviction`** | With the locks |
| `{service}:ratelimit:` | `volatile-ttl` acceptable | Either |

Production topology: a **shared Redis cluster with a per-service ACL user** and
a mandatory `{service}:` key prefix is the cost-effective default. What must
*not* be shared is the eviction policy between cache and coordination keys.

```
user ordering-svc on >REDACTED ~ordering:* +@read +@write +@keyspace -@dangerous
```

Two rules the helper library enforces rather than documents:

- **Every cache and lock key has a TTL.** A key without one is a memory leak
  with a slow fuse, and on a `noeviction` instance it eventually stops writes
  entirely.
- **No cross-service keys.** The ACL makes this impossible rather than
  discouraged, which is the right level of enforcement for something that
  otherwise gets violated once and never noticed.

### 8.2 HybridCache

.NET 9 introduced `HybridCache`, which supersedes hand-rolled
`IMemoryCache`+`IDistributedCache` combinations. It gives a two-tier cache (L1
in-process, L2 Redis) with **stampede protection** — concurrent misses for the
same key execute the factory once rather than N times.

Registered inside `AddRedisConnections` — the same helper that supplies the two
connections of §8.1 — rather than as a separate call somebody has to remember.
`PriceChangedCacheInvalidator` (§8.4) injects `HybridCache` and is registered by
the §6.2 scan, so an unregistered cache is a service that will not start:

```csharp
// Ordering.Infrastructure — called by AddOrderingInfrastructure (§4.2).
public static IServiceCollection AddRedisConnections(
    this IServiceCollection services, IConfiguration configuration)
{
    // §8.1's two keyed IConnectionMultiplexer registrations — cache and
    // coordination, separate because the eviction policies cannot be shared.

    services.AddStackExchangeRedisCache(options =>
    {
        // The CACHE connection (allkeys-lru). Coordination keys use the other.
        options.Configuration = configuration.GetConnectionString("RedisCache");
    });

    // The §8.1 key prefix, from ApplicationName — the same single source §8.5
    // uses for idempotency keys. A literal here is a second place the service
    // name lives (§15.4), and the two drift silently: §8.1's per-service ACL
    // denies writes to a prefix the service does not own, so the symptom is a
    // cache that never populates rather than an error naming the prefix.
    services.AddOptions<RedisCacheOptions>()
            .Configure<IHostEnvironment>((o, env) =>
                o.InstanceName = $"{env.ApplicationName}:cache:");

    services.AddHybridCache(options =>
    {
        options.DefaultEntryOptions = new HybridCacheEntryOptions
        {
            Expiration           = TimeSpan.FromMinutes(10),  // L2, Redis
            LocalCacheExpiration = TimeSpan.FromMinutes(1)    // L1, in-process
        };
        options.MaximumPayloadBytes = 1024 * 1024;
    });

    return services;
}
```

The short L1 expiry bounds how long one instance can serve data another instance
has already invalidated. One minute of possible staleness across instances is
usually an acceptable trade for eliminating most Redis round trips; adjust with
the domain in mind.

```csharp
public sealed class GetProductDetailHandler(
    HybridCache cache, IDbConnectionFactory connections)
    : IQueryHandler<GetProductDetailQuery, ProductDetailDto?>
{
    public async Task<ProductDetailDto?> HandleAsync(
        GetProductDetailQuery query, CancellationToken ct)
        => await cache.GetOrCreateAsync(
            $"product:{query.ProductId}:v2",
            query,
            static async (q, token) =>
            {
                using var connection = connections.Create();
                return await connection.QuerySingleOrDefaultAsync<ProductDetailDto>(
                    ProductSql, new { q.ProductId });
            },
            tags: [$"product:{query.ProductId}", "catalog"],
            cancellationToken: ct);
}
```

The `static` lambda with the state parameter avoids allocating a closure per
call — a small thing that matters on a hot path.

### 8.3 Key naming

A convention, and the important thing about it is that **a call site writes only
half the key**. `RedisCacheOptions.InstanceName` (§8.2) contributes the
`{service}:cache:` prefix §8.1's keyspace table requires; the handler passes the
rest. Reading the two halves as one string is how a "cache key" ends up written
without the `cache:` segment and therefore outside the keyspace whose eviction
policy was the whole argument of §8.1:

```
{service}:cache:  {entity}:{id}:v{schema-version}
└── InstanceName  └── what the call site passes to HybridCache

catalog:cache:product:0195e4b2-...:v2
catalog:cache:product:0195e4b2-...:pricing:v1
ordering:cache:customer:0195e4c1-...:summaries:head:v1
```

So §8.2's handler passes `product:{id}:v2` and the key in Redis is
`catalog:cache:product:{id}:v2`. The helper exists to keep that true: a literal
prefix at a call site produces `catalog:catalog:cache:...` or, worse, a key that
skips the prefix entirely and is denied by the §8.1 ACL — which fails as a cache
that never populates rather than as an error naming the key.

The trailing schema version is the important part of the half you do write: when
a DTO's shape changes, bump the version and old entries become unreachable and
expire naturally. Without it, a deploy that changes a cached type causes
deserialisation failures across the fleet until the TTL drains.

### 8.4 Invalidation

Cache invalidation is driven by events, never by timers alone.

```mermaid
sequenceDiagram
    participant A as Admin
    participant C as Catalog API
    participant DB as SQL Server
    participant R as Redis
    participant MQ as RabbitMQ
    participant O as Ordering

    A->>C: PUT /products/{id}/price
    rect rgb(240, 245, 250)
    Note over C,DB: One transaction — no I/O but the database
    C->>DB: UPDATE Prices
    C->>DB: INSERT outbox: Local (own cache)
    C->>DB: INSERT outbox: Broker (PriceChanged)
    C->>DB: COMMIT
    end
    C-->>A: 202 Accepted

    Note over C,MQ: Outbox dispatcher, after commit
    C->>R: Local lane → RemoveByTag("product:{id}")
    C->>MQ: Broker lane → PriceChanged
    MQ->>O: PriceChanged
    O->>R: RemoveByTag("product:{id}")
```

**Catalog's own invalidation goes through the local outbox lane, not an inline
call after commit.** That is ADR-018: a `RemoveByTag` issued directly by the
handler is unretryable, so a process that dies between commit and the call
leaves a stale cache with nothing to fix it. Staged as a `Local` row, it gets
the same durability, retry accounting and alerting as everything else the
outbox carries.

Remote invalidation flows through the `Broker` lane as an ordinary integration
event. Both are needed — the local row keeps the writing service consistent
with itself, the event keeps every other service consistent shortly after — and
now both are the same mechanism.

This one lives in **Ordering** — a consumer of Catalog's event, invalidating
its own cached projections of Catalog data. It is the second of two handlers
Ordering registers for `PriceChanged`; the other is `ProductPriceProjection`
(§6.6), which updates the price table the write path reads. Both run through
the same `IntegrationEventConsumer<PriceChanged>` (§9.4), sequentially:

```csharp
namespace Ordering.Infrastructure.Caching;

public sealed class PriceChangedCacheInvalidator(HybridCache cache)
    : IIntegrationEventHandler<PriceChanged>
{
    public Task HandleAsync(PriceChanged e, CancellationToken ct)
        => cache.RemoveByTagAsync($"product:{e.ProductId}", ct).AsTask();
}
```

### 8.5 Idempotency keys

Every non-idempotent write endpoint requires an `Idempotency-Key` header. The
key is claimed atomically before any work happens.

The behaviour lives in `Common.Application`, so — exactly as with
`IUnitOfWork` in §6.3 — it must not reference `StackExchange.Redis`. §4.2 names
that package as forbidden and the architecture test enforces it. The store is a
port:

```csharp
namespace Common.Application;

public interface IIdempotencyStore
{
    /// <summary>Atomically claims the key. False if it is already held.</summary>
    Task<bool> TryClaimAsync(string key, TimeSpan retention, CancellationToken ct);

    Task<IdempotencyEntry?> GetAsync(string key, CancellationToken ct);
    Task CompleteAsync(string key, string payload, TimeSpan retention, CancellationToken ct);
    Task ReleaseAsync(string key, CancellationToken ct);
}

public sealed record IdempotencyEntry(bool InProgress, string? Payload);

/// <summary>
/// Opts a command into IdempotencyBehavior. Not an empty marker: the behaviour
/// reads CommandId to build its key, so the interface has to carry it.
///
/// The behaviour is constrained to this, which means a command that does not
/// declare it is simply never protected — no error, no warning, and a retry
/// creates a second order. Opting in is a decision; forgetting to is not
/// meant to look like one.
/// </summary>
public interface IIdempotentCommand
{
    Guid CommandId { get; }
}
```

```csharp
public sealed class IdempotencyBehavior<TCommand, TResult>(
    IIdempotencyStore store)
    : IPipelineBehavior<TCommand, TResult>
    where TCommand : ICommand<TResult>, IIdempotentCommand
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public async Task<TResult> HandleAsync(
        TCommand command, NextDelegate<TResult> next, CancellationToken ct)
    {
        // Key shape only — the store owns the service prefix and namespace.
        var key = $"{typeof(TCommand).Name}:{command.CommandId}";

        if (!await store.TryClaimAsync(key, Retention, ct))
        {
            var existing = await store.GetAsync(key, ct);

            if (existing is null || existing.InProgress)
                throw new ConcurrentRequestException(command.CommandId);

            return JsonSerializer.Deserialize<TResult>(existing.Payload!)!;
        }

        try
        {
            var result = await next();
            await store.CompleteAsync(key, JsonSerializer.Serialize(result), Retention, ct);
            return result;
        }
        catch
        {
            // Release the claim so the caller may legitimately retry.
            await store.ReleaseAsync(key, ct);
            throw;
        }
    }
}
```

The Redis implementation lives in Infrastructure and is where the two §8.1
constraints are satisfied — the `{service}:idem:` prefix required by the ACL,
and the **coordination** connection rather than the cache connection, because
idempotency keys must never be evicted:

```csharp
namespace Ordering.Infrastructure.Idempotency;

internal sealed class RedisIdempotencyStore(
    [FromKeyedServices(RedisConnections.Coordination)] IConnectionMultiplexer redis,
    IHostEnvironment environment)
    : IIdempotencyStore
{
    // {service}:idem:... — matches the ACL pattern ~ordering:* from §8.1.
    //
    // ApplicationName, not a configured value: the service name is also what
    // §13.2 stamps on every trace and metric. Two sources would let the Redis
    // prefix and the telemetry label disagree, which breaks correlation exactly
    // when it is needed — and a wrong prefix fails the ACL silently.
    private string Key(string suffix) => $"{environment.ApplicationName}:idem:{suffix}";

    public async Task<bool> TryClaimAsync(string key, TimeSpan retention, CancellationToken ct)
        => await redis.GetDatabase().StringSetAsync(
               Key(key), InProgressMarker, retention, When.NotExists);

    // GetAsync / CompleteAsync / ReleaseAsync follow the same key shaping.
}
```

A behaviour constrained on a marker fails open: the command still executes, just
unprotected. That is the same silent shape as an unregistered handler (§6.2), so
it gets the same kind of test — one that reads intent from the shape of the
command rather than trusting the author to have opted in:

```csharp
[Fact]
public void Commands_carrying_a_CommandId_declare_IIdempotentCommand()
{
    var offenders = typeof(PlaceOrderCommand).Assembly.GetTypes()
        .Where(t => t.GetInterfaces().Any(i =>
                        i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)))
        .Where(t => t.GetProperty("CommandId") is not null)
        .Where(t => !typeof(IIdempotentCommand).IsAssignableFrom(t))
        .Select(t => t.Name);

    offenders.ShouldBeEmpty(
        "a CommandId with no IIdempotentCommand is a command that looks protected " +
        "and is not — IdempotencyBehavior is constrained on the interface, not the field.");
}
```

> **Two connections, not one.** The cache multiplexer points at the instance
> running `allkeys-lru`; the coordination multiplexer points at the
> `noeviction` instance or DB index holding locks, idempotency keys and the
> denylist. Registering them as keyed services makes picking the wrong one a
> visible choice rather than an invisible default. This is the §8.1 rule
> expressed in wiring instead of prose.

### 8.6 Rules and traps

- **Cache read models, never aggregates.** A cached aggregate that someone
  mutates is a corruption bug that reproduces once a week.
- **Never make Redis the system of record.** It is a cache and a coordination
  primitive. Anything that must survive is in SQL Server.
- **Set an expiry on every key.** A key without a TTL is a memory leak with a
  slow fuse.
- **Do not cache per-user data under a shared key.** The classic incident: one
  customer sees another's basket.
- **Watch the payload size.** `MaximumPayloadBytes` exists because serialising
  large objects through Redis can be slower than the query it replaced.

---

## 9. Messaging

### 9.1 Integration events

Integration events are the public contract of a service. They are published
facts about the past, and they must be boring: primitives, no domain types, no
behaviour, no assumptions about the consumer.

The three envelope fields every event carries are an interface, not a
convention — the consumer adapter needs `OccurredAt` to measure delivery lag
(§13.3) and cannot read it off an unconstrained type parameter:

```csharp
namespace Common.Contracts;

/// <summary>
/// Implemented by every integration event. No behaviour and no domain types —
/// three primitives, which is what keeps this legal under §9.6's rule that a
/// contract may not name a domain type.
/// </summary>
public interface IIntegrationEvent
{
    Guid MessageId            { get; }
    Guid CorrelationId        { get; }
    DateTimeOffset OccurredAt { get; }
}
```

> **`MessageId` here is *the* message id, not a second one.** The envelope's
> value is what `Stage` writes to the outbox row (§9.4), what the dispatcher
> puts on the transport, what MassTransit's header carries, and therefore what
> the inbox dedupes on (§9.5). Body, row, header and inbox key are one GUID.
>
> That has to be stated because the alternative is so easy to write and so hard
> to see: a `Guid.CreateVersion7()` in `Stage` would compile, work, and give
> every event two identities — one a consumer reads out of the payload, one the
> broker and the inbox use. Nothing fails. The cost arrives during an incident,
> when the id in the application log cannot be found in the inbox table, and
> the answer to "was this message processed?" becomes "which id do you mean?".
>
> `CorrelationId` follows the same rule for the same reason. The mapper decides
> it (§9.3 sets it from the order) precisely because a business correlation is
> more useful across a saga than an ambient request id, and a second value
> assigned at staging time would quietly replace that choice.

```csharp
namespace Common.Contracts.Ordering.V1;

public sealed record OrderConfirmed : IIntegrationEvent
{
    public required Guid MessageId       { get; init; }
    public required Guid CorrelationId   { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId         { get; init; }
    public required Guid CustomerId      { get; init; }
    public required decimal TotalAmount  { get; init; }
    public required string Currency      { get; init; }
    public required IReadOnlyList<ConfirmedLine> Lines { get; init; }
    public required ShippingAddressV1 ShippingAddress  { get; init; }
}

public sealed record ConfirmedLine(Guid ProductId, int Quantity, decimal UnitPrice);
```

`OrderPlaced` is the other contract worth writing out, because it is what the
fulfilment saga starts on (§9.6) and what `ReserveStock` draws its lines from:

```csharp
namespace Common.Contracts.Ordering.V1;

public sealed record OrderPlaced : IIntegrationEvent
{
    public required Guid MessageId       { get; init; }
    public required Guid CorrelationId   { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId         { get; init; }
    public required Guid CustomerId      { get; init; }
    public required decimal TotalAmount  { get; init; }
    public required string Currency      { get; init; }
    public required IReadOnlyList<PlacedLine> Lines { get; init; }
}

public sealed record PlacedLine(Guid ProductId, int Quantity, decimal UnitPrice);
```

Catalog's three follow the same shape and the same interface — they are what
Ordering's projection endpoint consumes (§9.8), and `IntegrationEventConsumer<T>`
will not compile against a type that lacks it:

```csharp
namespace Common.Contracts.Catalog.V1;

public sealed record PriceChanged : IIntegrationEvent
{
    public required Guid MessageId            { get; init; }
    public required Guid CorrelationId        { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid ProductId            { get; init; }
    public required decimal Amount            { get; init; }
    public required string Currency           { get; init; }
}

// ProductPublished and ProductDiscontinued repeat the same three envelope
// members and add their own — listed in Appendix D.5, because §6.6's
// projections read them and a member no declaration and no inventory covers
// is how a sample stops being checkable. The envelope is written out on every
// contract rather than inherited from a base record: a shared base is a shared
// versioning fate (§9.2), and three properties is a cheaper price than that.
```

> **The constraint is the enforcement, and no test is needed for it.** A new
> event without `IIntegrationEvent` fails to compile the moment somebody binds
> a consumer to it — which is the only moment it starts mattering. Commands
> (`CancelOrder`, `ReserveStock`) deliberately do not implement it: they are
> routed by `CommandConsumer` (§9.4), they carry no envelope in the body, and
> their `MessageId` is the transport's.

> **Each contract owns its line type.** `PlacedLine` and `ConfirmedLine` have
> identical shapes today, and sharing one record would be the obvious economy.
> It is the wrong one: a field added to `OrderConfirmed`'s lines would silently
> change `OrderPlaced`'s payload, and the two contracts would have to version
> together — the coupling §9.2 exists to prevent. Duplication between published
> contracts is deliberate, for the same reason duplication between bounded
> contexts is (§4.3).

**Design guidance for event payloads.** There is a real trade-off between thin
events (ID only, consumer calls back for detail) and fat events (everything a
consumer might need). Thin events keep the contract small but reintroduce
synchronous coupling on the consume path. Fat events are self-contained but
duplicate data and grow over time.

This blueprint uses **fat-enough events**: carry the data consumers actually
need to act, established by asking them, not by guessing. `OrderConfirmed`
includes the shipping address because Shipping cannot function without it and
should not call back to Ordering to get it.

### 9.2 Versioning

Contracts live in a versioned namespace: `Common.Contracts.Ordering.V1`.

**Additive changes** — new optional fields — do not require a version bump.
Consumers deserialising an unknown field ignore it.

**Breaking changes** — removing a field, renaming, changing a type, changing
semantics — require a new version. The publisher then emits both V1 and V2 for a
deprecation window, consumers migrate independently, and V1 is retired once
telemetry confirms no consumer remains on it.

There is no shortcut here. A "just this once" breaking change to a live contract
means a coordinated deploy, and coordinated deploys are the thing this
architecture exists to avoid.

### 9.3 Domain event → integration event: the allow-list mapper

§5.5 states the principle — never publish a domain event to the bus. This is the
mechanism that makes it structural rather than aspirational.

Translation is **opt-in**. A mapper registry names each domain event type that
becomes an integration event, and how. Everything unregistered is local-only.

**Who calls this:** the mapper and publisher below are invoked by
`DomainEventDispatcher` (§7.5), not by command handlers. A handler that calls
either of them directly is a bug: the dispatcher runs at the single point where
every aggregate has finished changing, and a handler that stages earlier
serialises a snapshot the rest of the handler can still move on from. The
payload is written at `StageAsync` (the port below), not at commit, so a total adjusted
two lines later commits an outbox row that disagrees with the row beside it.
Both leave the transaction together and only one of them is right.

```csharp
namespace Ordering.Application.Integration;

public interface IIntegrationEventMapper
{
    IReadOnlyList<object> Map(IReadOnlyList<IDomainEvent> domainEvents);
}

internal sealed class OrderingIntegrationEventMapper : IIntegrationEventMapper
{
    // The allow-list. A domain event absent from this dictionary never
    // reaches the bus — by construction, not by review.
    private static readonly Dictionary<Type, Func<IDomainEvent, object>> Registry = new()
    {
        // Domain type in, contract type out. The suffix (§5.5) is what makes
        // that visible — with one name for both, this reads as identity.
        [typeof(OrderPlacedDomainEvent)]    = e => ToContract((OrderPlacedDomainEvent)e),
        [typeof(OrderConfirmedDomainEvent)] = e => ToContract((OrderConfirmedDomainEvent)e),
        [typeof(OrderCancelledDomainEvent)] = e => ToContract((OrderCancelledDomainEvent)e),
        // OrderStockConfirmedDomainEvent is deliberately absent — internal only.
    };

    // V1.OrderPlaced, not OrderPlacedDomainEvent: Money is decomposed into a
    // decimal and an ISO code, because a contract may not carry domain types.
    private static V1.OrderPlaced ToContract(OrderPlacedDomainEvent e) => new()
    {
        MessageId     = Guid.CreateVersion7(),
        CorrelationId = e.OrderId.Value,
        OccurredAt    = e.OccurredAt,
        OrderId       = e.OrderId.Value,
        CustomerId    = e.CustomerId.Value,
        TotalAmount   = e.Total.Amount,
        Currency      = e.Total.Currency,
        // PlacedLine, not ConfirmedLine — OrderPlaced owns its own line type
        // so the two contracts can version independently (§9.1).
        Lines         = e.Lines.Select(l => new V1.PlacedLine(
                            l.ProductId.Value, l.Quantity, l.UnitPrice.Amount)).ToArray()
    };

    public IReadOnlyList<object> Map(IReadOnlyList<IDomainEvent> domainEvents)
    {
        var mapped = new List<object>();

        foreach (var domainEvent in domainEvents)
        {
            if (!Registry.TryGetValue(domainEvent.GetType(), out var map))
                continue;                       // Unregistered → local-only. Not an error.

            mapped.Add(map(domainEvent));       // Registered and throwing → fails the command.
        }

        return mapped;
    }
}
```

The two failure semantics are deliberately different, and the distinction is the
whole point:

| Case | Behaviour | Why |
|---|---|---|
| Domain event **not** in the registry | Skipped silently. No bus message, no failure. | Most domain events are internal. Failing on them would force every new event to be published or explicitly suppressed |
| Registered mapper **throws** | The command fails and the transaction rolls back | Someone declared this event must be published. If it cannot be, the state change must not stand either |

There is deliberately **no `MustPublish` flag** on domain events. If it must
reach the bus, register it. One mechanism, one place to look.

#### The publisher contract

`IIntegrationEventPublisher` is an Application port, and its implementation is
constrained normatively:

```csharp
namespace Common.Application;

public enum OutboxLane
{
    /// <summary>Published to the message broker. A public contract.</summary>
    Broker,

    /// <summary>Dispatched in-process after commit to IProjectionHandler&lt;T&gt;.
    /// Never leaves the service and is not a contract.</summary>
    Local
}

public interface IIntegrationEventPublisher
{
    /// <summary>
    /// Stages a message for delivery after the current transaction commits.
    /// </summary>
    Task StageAsync(object message, OutboxLane lane, CancellationToken ct);
}
```

The implementation **must**:

- Write an outbox row on the **same `DbContext`** the command handler is using,
  so it enlists in the same transaction.

The implementation **must not**:

- Call the broker transport directly — `IBus.Publish` inside a handler
  reintroduces the dual-write the outbox exists to eliminate.
- Introduce a **second** outbox table set alongside the existing one. Two outbox
  implementations means two dispatchers, two retention policies, two sets of
  ordering guarantees, and one of them will be the one nobody monitors.

All three are mistakes a competent developer makes in good faith, which is why
they are prohibitions rather than guidance.

**One exemption: sagas.** A MassTransit state machine (§9.6) sends and publishes
directly from its activities rather than through this port. That is correct and
deliberate — a saga is Infrastructure, it already runs inside a consume
transaction with `UseInMemoryOutbox` configured on its receive endpoint, so its
outgoing messages are deferred until the consumer completes and its state
persists. Routing saga output through the application-level outbox would add a
second staging hop with no additional guarantee. The prohibition applies to
**Application code**, which is where the dual-write risk actually lives.

### 9.4 The transactional outbox

The core problem: a handler must change the database *and* publish a message.
These are two systems. Without care, the process can crash between them — the
order is placed but nobody is told, or the message is sent and the transaction
rolls back.

The outbox makes them one atomic operation by writing the message to the same
database, in the same transaction, and dispatching it afterwards.

```mermaid
sequenceDiagram
    participant H as Handler
    participant DB as SQL Server
    participant D as Outbox dispatcher
    participant P as Projection handlers
    participant MQ as RabbitMQ

    rect rgb(240, 245, 250)
    Note over H,DB: One transaction
    H->>DB: UPDATE Orders
    H->>DB: INSERT OutboxMessages (Broker and/or Local)
    H->>DB: COMMIT
    end

    loop every 500 ms
        Note over D,DB: Claim: CTE + UPDATE ... OUTPUT, sets LockedUntil
        D->>DB: lease up to 100 rows (UPDLOCK, READPAST)

        Note over D,MQ: Then each row independently — one failure affects one row
        alt Lane = Broker
            D->>MQ: publish
        else Lane = Local
            D->>P: IProjectionHandler
        end

        alt delivered
            D->>DB: ProcessedAt = now, LockedUntil = null
        else threw
            D->>DB: Attempts += 1, LastError, back off LockedUntil
        end
    end
```

```sql
CREATE TABLE ordering.OutboxMessages
(
    Id             BIGINT IDENTITY(1,1) PRIMARY KEY,
    MessageId      UNIQUEIDENTIFIER NOT NULL UNIQUE,
    CorrelationId  UNIQUEIDENTIFIER NOT NULL,
    MessageType    VARCHAR(300)     NOT NULL,
    Payload        NVARCHAR(MAX)    NOT NULL,
    Lane           VARCHAR(16)      NOT NULL,   -- 'Broker' | 'Local'
    OccurredAt     DATETIMEOFFSET   NOT NULL,
    ProcessedAt    DATETIMEOFFSET   NULL,
    Attempts       INT              NOT NULL DEFAULT 0,
    LastError      NVARCHAR(2000)   NULL,
    LockedUntil    DATETIMEOFFSET   NULL     -- lease; also carries retry backoff
);

-- Filtered index: the dispatcher only ever scans unprocessed rows, and the
-- index stays small regardless of table size.
CREATE INDEX IX_Outbox_Unprocessed
    ON ordering.OutboxMessages (OccurredAt)
    INCLUDE (Lane, Attempts, LockedUntil)
    WHERE ProcessedAt IS NULL;
```

`Lane` is what makes one table serve both after-commit destinations (§7.5).
`Broker` rows are published; `Local` rows are handed to in-process projection
handlers. Both get the same durability, the same retry accounting and the same
monitoring — which is the argument against a second, separate mechanism for
local reactions.

**Two types map to this table, deliberately.** The staging path writes whole
rows through EF Core; the dispatcher reads a narrow projection of the columns
its claim returns. Collapsing them into one type produces a class whose
`ProcessedAt` is always null on the read path and whose `LastError` is never
populated on the write path — see Appendix D:

| Type | Used by | Shape |
|---|---|---|
| `OutboxMessage` | EF entity, `db.OutboxMessages` | All columns |
| `OutboxClaim` | Dapper, the dispatcher's `OUTPUT` projection | Id, MessageId, CorrelationId, MessageType, Payload, Lane, Attempts, OccurredAt |

```csharp
namespace Common.Infrastructure.Outbox;

public sealed class OutboxMessage
{
    public long Id { get; private set; }
    public Guid MessageId { get; private set; }
    public Guid CorrelationId { get; private set; }
    public string MessageType { get; private set; } = null!;
    public string Payload { get; private set; } = null!;
    public OutboxLane Lane { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }

    public static OutboxMessage Stage(
        object message, OutboxLane lane, Guid correlationId,
        DateTimeOffset now, MessageTypeMap types) => new()
    {
        // One identity, not two. An integration event already carries its
        // MessageId and CorrelationId in the envelope the mapper filled in
        // (§9.3), and DeliverAsync copies the row's values onto the transport —
        // so minting a second GUID here would give the body one id and the
        // broker header another. The inbox dedupes on the transport id (§9.5),
        // which would then disagree with the id a support tool reads out of the
        // payload, and the only way to notice is to compare two logs.
        //
        // A Local-lane row carries a domain event, which has no envelope and
        // never reaches a broker, so the row mints both.
        MessageId     = message is IIntegrationEvent e ? e.MessageId     : Guid.CreateVersion7(),
        CorrelationId = message is IIntegrationEvent c ? c.CorrelationId : correlationId,
        MessageType   = types.NameOf(message.GetType()),
        Payload       = JsonSerializer.Serialize(
                            message, message.GetType(), OutboxJson.Options),
        Lane          = lane,
        OccurredAt    = now
    };
}

/// <summary>
/// Dapper projection of the claim's OUTPUT clause. Read-only, and its members
/// must match that clause exactly — Dapper binds by name and leaves an
/// unmatched member at its default, so a column added here and not there is a
/// DateTimeOffset.MinValue nobody notices until a metric reads 55 years.
/// </summary>
public sealed record OutboxClaim(
    long Id, Guid MessageId, Guid CorrelationId,
    string MessageType, string Payload, string Lane, int Attempts,
    DateTimeOffset OccurredAt);
```

#### The type name is a persisted contract

`MessageType` is written by one deployment and read by another. That makes the
obvious implementation — `AssemblyQualifiedName` out, `Type.GetType` back —
wrong in a way that only shows in production:

```
Ordering.Domain.Orders.OrderPlacedDomainEvent, Ordering.Domain,
Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
```

Every row carries the assembly version that staged it. Bump it — which a release
pipeline does automatically — and `Type.GetType` returns `null` for every row
written before the deploy. The dispatcher then exhausts its attempts on a batch
of perfectly good messages and abandons them. Nothing is lost, nothing is
delivered, and the only symptom is outbox depth climbing after a release that
looked clean. Trimming, single-file publish and moving a type between assemblies
break it the same way.

The fix is a name the code chooses rather than one the runtime computes:

```csharp
namespace Common.Infrastructure.Outbox;

/// <summary>
/// The assemblies whose events may be staged. Mutable and resolved before the
/// map, so a test host can add its own without replacing the registration —
/// the production assemblies are always in the list (§4.2).
/// </summary>
public sealed class MessageTypeSource(params Assembly[] assemblies)
{
    private readonly List<Assembly> _assemblies = [.. assemblies];

    public IEnumerable<Assembly> Assemblies => _assemblies;

    public MessageTypeSource Add(Assembly assembly)
    {
        _assemblies.Add(assembly);
        return this;
    }
}

/// <summary>
/// Two-way map between a stageable type and its persisted name. Built from the
/// source above, so it cannot list a name for a type that no longer exists —
/// and, being a singleton built at startup, a duplicate name fails the host
/// rather than the first message.
/// </summary>
public sealed class MessageTypeMap
{
    private readonly FrozenDictionary<string, Type> _byName;
    private readonly FrozenDictionary<Type, string> _byType;

    public MessageTypeMap(IEnumerable<Assembly> assemblies)
    {
        // FullName, not AssemblyQualifiedName: namespace and type name, no
        // version and no assembly. For contracts the namespace is already
        // versioned (§9.1), so this IS the contract. For domain events it is
        // internal, and a rename is then a migration the team chose rather than
        // one a build number made for it.
        var pairs = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false }
                     && (t.IsAssignableTo(typeof(IIntegrationEvent))
                      || t.IsAssignableTo(typeof(IDomainEvent))))
            .Select(t => (Name: t.FullName!, Type: t))
            .ToArray();

        var clash = pairs.GroupBy(p => p.Name).FirstOrDefault(g => g.Count() > 1);
        if (clash is not null)
            throw new InvalidOperationException(
                $"Two staged types share the name '{clash.Key}'. The outbox " +
                "column cannot distinguish them.");

        _byName = pairs.ToFrozenDictionary(p => p.Name, p => p.Type);
        _byType = pairs.ToFrozenDictionary(p => p.Type, p => p.Name);
    }

    public string NameOf(Type type) =>
        _byType.TryGetValue(type, out var name) ? name
            : throw new InvalidOperationException(
                $"{type.Name} is not a stageable message type. Staging it would " +
                "write a row the dispatcher cannot resolve.");

    /// <summary>The Local lane's payload types — §12.4 round-trips each.</summary>
    public IEnumerable<Type> StageableDomainEvents =>
        _byType.Keys.Where(t => t.IsAssignableTo(typeof(IDomainEvent)));

    public Type Resolve(string name) =>
        _byName.TryGetValue(name, out var type) ? type
            : throw new InvalidOperationException(
                $"Unknown message type '{name}'. A type was renamed or removed " +
                "while rows naming it were still unprocessed — drain the outbox " +
                "before deleting a message type (§7.4).");
}
```

Both directions throw, and both throw at the point of the mistake. `NameOf`
fails when something unstageable is staged — in the transaction, so the command
fails rather than the outbox filling with rows nobody can deliver. `Resolve`
fails on the dispatcher, where the message that names a departed type is the one
that lands in the retry log with its own name in it.

> **A renamed message type is a migration.** The rule that follows from this map
> is the one nobody remembers under deadline: a type may not be renamed or
> deleted while unprocessed rows still name it. Deploy the rename in one release
> with both names resolving to the same type, drain, then remove the old name in
> the next — the same shape as every backward-compatible schema change (ADR-007).

#### The payload is a persisted format too

The name is half of it. `Payload` is JSON written by one deployment and read by
another, and on the `Local` lane it holds a **domain event** — a type §5.5
explicitly describes as "free to change with the code". Both statements are
true and together they are a trap: a member renamed between the stage and the
deliver silently deserialises to its default, because that is what
`System.Text.Json` does with a property it cannot match.

Two rules, and one line of code that makes the first checkable:

```csharp
namespace Common.Infrastructure.Outbox;

public static class OutboxJson
{
    /// <summary>
    /// The single options instance used to stage and to deliver. Both sides
    /// must agree, and the way they stop agreeing is one of them picking up a
    /// host-wide default that was changed for an API's benefit.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        // Explicitly the defaults that matter, rather than inherited ones:
        // property names as declared, numbers as numbers, no case-insensitive
        // rescue on the way back in — a payload that only round-trips because
        // matching is lenient is a payload that will not survive a rename.
        PropertyNamingPolicy      = null,
        PropertyNameCaseInsensitive = false,
        NumberHandling            = JsonNumberHandling.Strict
    };
}
```

**Domain events on the `Local` lane must round-trip through these options**, and
that is asserted in §12.4 — `Every_stageable_domain_event_round_trips_through_the_outbox_options`,
written out there with the other outbox tests. It does **not** join §12.6's
contract suite, which selects on the `Common.Contracts.` namespace and so can
never see a domain event, by the same rule that keeps domain types out of
contracts.

The assertion catches the private constructor, the computed-property-with-no-setter
and the interface-typed member on the day it is introduced rather than on the
day a deploy happens to land mid-batch.

**And a renamed member is a migration, exactly as a renamed type is.** The drain
rule above covers both: unprocessed rows name types *and* describe shapes, and
the outbox is empty for a few seconds many times a day. Draining before a rename
costs nothing; discovering afterwards that yesterday's rows deserialise with a
`Total` of zero costs an afternoon and a corrected read model.

> **The alternative — stage a DTO instead of the domain type — was considered
> and rejected.** It would decouple the persisted shape from the domain, at the
> price of a second type per event, a mapper, and a place for the two to
> disagree. The `Local` lane is drained in seconds, which is what makes the
> cheaper option viable: the exposure is one batch, not one release cycle.
> That reasoning stops holding the moment the lane backs up for hours, so if the
> §13.7 outbox-age alert becomes routine rather than exceptional, revisit this
> before the backlog makes the decision for you.

The dispatcher runs as a background service in two phases: an atomic **claim**
that leases a batch of rows, then **per-row delivery** where each message
succeeds or fails on its own.

> **Every row is delivered and accounted for independently.** Wrapping a whole
> batch in one transaction is the obvious implementation and is wrong: a single
> failing projection would roll back the batch and block every healthy `Broker`
> row behind it, so a read-model bug in this service would stop publishing to
> every other service. The lanes can only be alerted on separately (§13.6) if
> they can actually fail separately.

```csharp
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopes, ILogger<OutboxDispatcher> log)
    : BackgroundService
{
    private const int MaxAttempts = 10;

    // Atomic claim: selects and leases in one statement, so two replicas
    // cannot take the same row. READPAST skips rows another replica holds.
    private const string ClaimSql =
        """
        WITH claimable AS (
            SELECT TOP (100) *
            FROM   ordering.OutboxMessages WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE  ProcessedAt IS NULL
              AND  Attempts < @MaxAttempts
              AND  (LockedUntil IS NULL OR LockedUntil < SYSDATETIMEOFFSET())
            ORDER BY OccurredAt
        )
        UPDATE claimable
        SET    LockedUntil = DATEADD(second, 60, SYSDATETIMEOFFSET())
        OUTPUT inserted.Id, inserted.MessageId, inserted.CorrelationId,
               inserted.MessageType, inserted.Payload, inserted.Lane,
               inserted.Attempts, inserted.OccurredAt;
        """;

    private const string CompleteSql =
        """
        UPDATE ordering.OutboxMessages
        SET    ProcessedAt = SYSDATETIMEOFFSET(), LockedUntil = NULL
        WHERE  Id = @Id;
        """;

    // Increments the attempt counter and backs off exponentially by pushing
    // the lease forward. This is what makes the cap — and the abandoned-row
    // alert in §13.6 — reachable.
    private const string FailSql =
        """
        UPDATE ordering.OutboxMessages
        SET    Attempts    = Attempts + 1,
               LastError   = LEFT(@Error, 2000),
               LockedUntil = DATEADD(second,
                                     POWER(2, CASE WHEN Attempts > 8 THEN 8 ELSE Attempts END) * 5,
                                     SYSDATETIMEOFFSET())
        WHERE  Id = @Id;
        """;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));

        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await ProcessBatchAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The claim itself failed — database unreachable. Next tick.
                log.LogError(ex, "Outbox claim failed; retrying next tick.");
            }
        }
    }

    /// <summary>
    /// One claim-and-deliver pass. Returns the number of rows completed.
    /// Public so tests drive it directly instead of racing a timer — see §12.4.
    /// </summary>
    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        // Disposed every pass — the loop runs twice a second, so a leaked
        // connection here exhausts the pool within a minute.
        using var connection = sp.GetRequiredService<IDbConnectionFactory>().Create();

        // OutboxClaim, not OutboxMessage — the claim projects only the columns
        // the OUTPUT clause returns. See Appendix D.
        var claimed = (await connection.QueryAsync<OutboxClaim>(
            ClaimSql, new { MaxAttempts })).AsList();

        var completed = 0;

        foreach (var message in claimed)
        {
            try
            {
                await DeliverAsync(sp, message, ct);
                await connection.ExecuteAsync(CompleteSql, new { message.Id });
                completed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad message does not affect the other 99.
                await connection.ExecuteAsync(FailSql,
                    new { message.Id, Error = ex.ToString() });

                log.LogError(ex,
                    "Outbox message {MessageId} on lane {Lane} failed, attempt {Attempt} of {Max}.",
                    message.MessageId, message.Lane, message.Attempts + 1, MaxAttempts);
            }
        }

        return completed;
    }

    private static async Task DeliverAsync(
        IServiceProvider sp, OutboxClaim message, CancellationToken ct)
    {
        // Through the map, not Type.GetType: the column holds a name this code
        // chose, and it has to survive the version bump of the assembly that
        // wrote it.
        var type    = sp.GetRequiredService<MessageTypeMap>().Resolve(message.MessageType);
        var payload = JsonSerializer.Deserialize(
                          message.Payload, type, OutboxJson.Options)!;

        if (message.Lane is "Broker")
        {
            await sp.GetRequiredService<IPublishEndpoint>().Publish(payload, type, c =>
            {
                c.MessageId     = message.MessageId;
                c.CorrelationId = message.CorrelationId;
            }, ct);
            return;
        }

        // Local lane: this service's own projection handlers, running safely
        // outside the write transaction that produced the event (§7.5).
        // OccurredAt comes from the row, not the payload: the invoker is
        // generic and unconstrained, so it has no typed access to a member the
        // payload may or may not have (§13.3). It is the time the aggregate
        // raised the event — Stage() is called inside the write transaction —
        // so the lag §13.7 measures includes the commit, which is the honest
        // reading of "how stale is this read model".
        await ProjectionInvoker.InvokeAllAsync(sp, payload, type, message.OccurredAt, ct);
    }
}
```

`ProjectionInvoker` resolves and calls the handlers for a runtime type. It uses
the same cached-delegate approach as the dispatcher in §6.2, so the reflection
cost is paid once per event type rather than once per message:

```csharp
internal static class ProjectionInvoker
{
    private static readonly ConcurrentDictionary<Type, Invoker> Cache = new();

    public static Task InvokeAllAsync(
        IServiceProvider sp, object payload, Type eventType,
        DateTimeOffset occurredAt, CancellationToken ct)
        => Cache.GetOrAdd(eventType, static t =>
               (Invoker)Activator.CreateInstance(
                   typeof(Invoker<>).MakeGenericType(t))!)
           .InvokeAllAsync(sp, payload, occurredAt, ct);

    private abstract class Invoker
    {
        public abstract Task InvokeAllAsync(
            IServiceProvider sp, object payload,
            DateTimeOffset occurredAt, CancellationToken ct);
    }

    private sealed class Invoker<TEvent> : Invoker
    {
        public override async Task InvokeAllAsync(
            IServiceProvider sp, object payload,
            DateTimeOffset occurredAt, CancellationToken ct)
        {
            var handlers = sp.GetServices<IProjectionHandler<TEvent>>().ToArray();

            // A Local row is staged only when IProjectionRegistry found a
            // handler (§7.5). Finding none here means the handler was
            // implemented but never registered — fail loudly rather than
            // marking the row processed having done nothing.
            if (handlers.Length == 0)
                throw new InvalidOperationException(
                    $"No IProjectionHandler<{typeof(TEvent).Name}> is registered, " +
                    "but a Local outbox row was staged for it. Check the §6.2 scan.");

            // Sequential, not concurrent: two projections writing the same read
            // table in parallel is a deadlock waiting for load to find it.
            foreach (var handler in handlers)
                await handler.HandleAsync((TEvent)payload, ct);

            // Raised-to-applied (§13.7), recorded after the handlers rather
            // than before: the SLO is about when the read model became
            // correct, not when work on it started. Resolved from sp because
            // this type is static and cached — it has no constructor to inject.
            sp.GetRequiredService<MessagingMetrics>().Projected(
                typeof(TEvent).Name,
                sp.GetRequiredService<TimeProvider>().GetUtcNow() - occurredAt);
        }
    }
}
```

If any handler throws, the exception propagates to the per-row `catch` above and
the whole row is retried — meaning **every** handler for that event runs again.
That is the reason §6.6 insists projections are idempotent; with more than one
handler per event it is not a theoretical concern.

Consequences of the per-row design worth stating explicitly:

- **A message that keeps failing reaches `Attempts = 10` and stops being
  claimed.** It stays in the table with `ProcessedAt` still `NULL` and its
  `LastError` populated, which is exactly what the abandoned-row alert (§13.6)
  detects and what `outbox-abandoned.md` tells the operator to read. Replaying
  it is a matter of resetting `Attempts` to zero.
- **The retention purge must delete only rows with `ProcessedAt IS NOT NULL`.**
  Purging on age alone would silently destroy the abandoned rows that the alert
  exists to surface.
- **Strict global ordering is not guaranteed.** Rows are claimed in `OccurredAt`
  order, but a failed message backs off while later ones proceed, and multiple
  replicas run concurrently. Consumers must not assume ordering — which the
  out-of-order guard in §6.6 already assumes they cannot.
- **The 60-second lease bounds crash recovery.** If a dispatcher dies mid-batch,
  its claimed rows become available again a minute later rather than being stuck
  behind a lock that no longer has an owner.

#### Handler contracts

Three handler interfaces exist, and confusing them is the most likely mistake in
this area. They differ by where the message came from:

```csharp
namespace Common.Application;

/// <summary>
/// Reacts to this service's OWN events after commit, via the Local outbox lane.
/// Read-model projections, local cache invalidation. Never a public contract.
/// </summary>
public interface IProjectionHandler<in TEvent>
{
    Task HandleAsync(TEvent domainEvent, CancellationToken ct);
}

/// <summary>
/// Reacts to an integration event published by ANOTHER service, delivered by
/// the broker. Invoked by the consumer adapter below, behind the inbox filter.
/// </summary>
public interface IIntegrationEventHandler<in TEvent> where TEvent : class
{
    Task HandleAsync(TEvent integrationEvent, CancellationToken ct);
}
```

#### Empty is a decision, not a default

Five places resolve handlers as a collection, and `GetServices<T>()` returning
nothing is the one failure this architecture cannot detect structurally — it
looks exactly like "nothing to do". Each site therefore states which it means:

| Site | Empty means | Behaviour |
|---|---|---|
| `ValidationBehavior` (§6.3) | most commands have no validator | proceed |
| `IProjectionRegistry` (§7.5) | this event has no projection — the question being asked | return false, stage no `Local` row |
| `ProjectionInvoker` (§9.4) | a `Local` row was staged, so a handler was found earlier | **throw** |
| `IntegrationEventConsumer` (§9.4) | the endpoint binds this type, so something should handle it | **throw** |
| `Dispatcher` (§6.2) | no behaviours — never valid, nothing would ever commit | prevented by explicit registration + test (§6.3) |

The two that throw are the two where an empty list is reachable only through
misconfiguration *and* where silence destroys data: an acked broker message is
suppressed by the inbox forever, and a completed `Local` row is never retried.

| Interface | Source | Delivery | Retry |
|---|---|---|---|
| `ICommandHandler<,>` | HTTP request | Dispatcher, in transaction | None — the caller retries |
| `ICommandHandler<,>` | Command **message** | Broker → `CommandConsumer` → dispatcher, in transaction | Broker redelivery |
| `IProjectionHandler<>` | Own domain event | Local outbox lane, after commit | Outbox `Attempts` |
| `IIntegrationEventHandler<>` | Another service's **event** | Broker → `IntegrationEventConsumer` → inbox | Broker redelivery |

`ICommandHandler` appears twice deliberately: a command is the same application
operation whether a user submitted it or a saga sent it, and it must not grow a
second implementation because of how it arrived.

The bridge from the broker to `IIntegrationEventHandler` is a single generic
MassTransit consumer. This is the only place a MassTransit type meets
application code, which is what ADR-014 depends on:

```csharp
namespace Ordering.Infrastructure.Messaging;

public sealed class IntegrationEventConsumer<TEvent>(
    IEnumerable<IIntegrationEventHandler<TEvent>> handlers,
    MessagingMetrics metrics, TimeProvider clock)
    : IConsumer<TEvent>
    where TEvent : class, IIntegrationEvent
{
    public async Task Consume(ConsumeContext<TEvent> context)
    {
        // Publish-to-consume lag, read straight off the message (§13.3). The
        // IIntegrationEvent constraint is what makes OccurredAt reachable here
        // — without it this method sees only `object`-shaped generics.
        metrics.Delivered(typeof(TEvent).Name,
            clock.GetUtcNow() - context.Message.OccurredAt);

        // Configuring this consumer for TEvent is a statement that something
        // handles TEvent. Zero handlers is a misconfiguration, and acking the
        // message would be worse here than anywhere else: the inbox filter
        // (§9.5) commits its row once Consume returns, so redelivery is
        // suppressed and the message is gone for good. Throwing sends it to
        // retry and then the error queue, which §13.6 alerts on.
        if (!handlers.Any())
            throw new InvalidOperationException(
                $"No IIntegrationEventHandler<{typeof(TEvent).Name}> is registered, " +
                $"but {typeof(TEvent).Name} is bound on this endpoint. Check the §6.2 scan.");

        // Duplicate suppression happens in the inbox filter (§9.5), which is
        // configured on the receive endpoint ahead of this consumer.
        foreach (var handler in handlers)
            await handler.HandleAsync(context.Message, context.CancellationToken);
    }
}
```

Commands need the mirror of this. They arrive on their own queue, they are not
integration events, and they dispatch into the **application** pipeline rather
than to a projection handler — so a command that arrives by message goes through
exactly the same behaviours (§6.3) as one that arrives by HTTP:

```csharp
namespace Ordering.Infrastructure.Messaging;

/// <summary>
/// Bridges an inbound command message to the application dispatcher. TMessage
/// is a wire contract; TCommand is the application command it maps to.
/// </summary>
public sealed class CommandConsumer<TMessage, TCommand>(
    IDispatcher dispatcher,
    ICommandMessageMapper<TMessage, TCommand> mapper,
    MessagingMetrics metrics,
    ILogger<CommandConsumer<TMessage, TCommand>> log)
    : IConsumer<TMessage>
    where TMessage : class
    where TCommand : ICommand<Result>
{
    public async Task Consume(ConsumeContext<TMessage> context)
    {
        // Mapping is explicit: the wire type is a contract, the command is an
        // application type, and CancelOrder.Reason is a string that has to be
        // parsed back into CancellationReason (§9.6).
        var command = mapper.Map(context.Message);

        var result = await dispatcher.SendAsync(command, context.CancellationToken);

        // A domain rejection is an answer, not a delivery failure. The message
        // was received, understood and refused, and no redelivery changes that
        // — so it is acked, counted and logged rather than thrown (§9.8).
        //
        // This is the last place that can tell a rejection from a fault. An
        // exception from the dispatcher propagates and MassTransit retries it,
        // which is correct: that is a fault. Everything below is the other case.
        if (result.IsFailure)
        {
            metrics.Rejected(typeof(TMessage).Name, result.Error.Code);

            log.LogWarning(
                "{MessageType} rejected by the domain: {ErrorCode} {ErrorDescription}. " +
                "CorrelationId {CorrelationId}.",
                typeof(TMessage).Name, result.Error.Code, result.Error.Description,
                context.CorrelationId);
        }
    }
}
```

```csharp
// Commands Ordering accepts — §3.2's "Accepts" column. The queue name must
// match Endpoints.OrderingQueue in §9.6, or the saga sends into a void.
cfg.ReceiveEndpoint("ordering-commands", e =>
{
    e.UseMessageRetry(r =>
    {
        // A malformed contract does not parse itself on the fourth attempt.
        // Retrying it burns a minute of backoff and delays every message
        // behind it before reaching the same error queue.
        //
        // Domain rejections are not here because they never throw — the
        // consumer acks them (§9.8). This list is for faults that are terminal
        // rather than for outcomes that are not faults at all.
        r.Ignore<ContractMappingException>();

        r.Exponential(5, TimeSpan.FromSeconds(1),
                         TimeSpan.FromMinutes(1),
                         TimeSpan.FromSeconds(2));
    });
    e.UseInMemoryOutbox();
    e.UseConsumeFilter(typeof(InboxFilter<>), context);

    // One per command in §3.2's Accepts column. The saga sends four; a type
    // missing here is sent into a queue that ignores it.
    e.ConfigureConsumer<CommandConsumer<CancelOrder, CancelOrderCommand>>(context);
    e.ConfigureConsumer<CommandConsumer<ConfirmOrder, ConfirmOrderCommand>>(context);
    e.ConfigureConsumer<CommandConsumer<MarkOrderShipped, MarkOrderShippedCommand>>(context);
    e.ConfigureConsumer<CommandConsumer<FlagOrderForReview, FlagOrderForReviewCommand>>(context);
});
```

Inventory and Payments declare `inventory-commands` and `payments-commands` the
same way, for `ReserveStock`/`ReleaseStock` and `AuthorisePayment`
respectively. **Three queues are addressed by the saga (§9.6) and each needs a
receive endpoint in its owning service** — a command sent to an undeclared queue
is not an error, it is silence.

```csharp
// Consumer wiring only — the complete endpoint, with retry and the inbox
// filter, is in §9.8.
cfg.ReceiveEndpoint("ordering-catalog-events", e =>
{
    // One registration per event type this service subscribes to. The list
    // must match Ordering's Consumes column in §3.2 — a handler with no
    // registration here is never invoked, and looks correct while doing
    // nothing.
    e.ConfigureConsumer<IntegrationEventConsumer<ProductPublished>>(context);
    e.ConfigureConsumer<IntegrationEventConsumer<PriceChanged>>(context);
    e.ConfigureConsumer<IntegrationEventConsumer<ProductDiscontinued>>(context);
});
```

The outbox guarantees **at-least-once** delivery, never exactly-once. A crash
between publishing and marking processed republishes the message. This is
correct and expected — which is why consumers must be idempotent.

Retain processed rows for a few days for debugging, then delete them on a
schedule. An outbox table nobody prunes grows without bound and eventually
degrades the filtered index scan.

```sql
-- ProcessedAt IS NOT NULL is load-bearing, not defensive. Purging on age
-- alone would delete the abandoned rows (Attempts >= 10, never processed)
-- that the §13.6 alert exists to surface — turning permanent data loss into
-- a clean, empty table.
DELETE TOP (5000) FROM ordering.OutboxMessages
WHERE  ProcessedAt IS NOT NULL
  AND  ProcessedAt < DATEADD(day, -7, SYSDATETIMEOFFSET());
```

### 9.5 Idempotent consumers — the inbox

The consumer-side counterpart. Before handling a message, record its ID; if it
is already recorded, skip.

The inbox table lives in the **service's own database** alongside the outbox —
database-per-service (§7.1) applies to technical tables as much as business
ones, and a shared inbox would couple every consumer's deployment together.

```sql
CREATE TABLE ordering.InboxMessages
(
    MessageId   UNIQUEIDENTIFIER NOT NULL,
    Endpoint    VARCHAR(300)     NOT NULL,   -- receive endpoint, not message type
    HandledAt   DATETIMEOFFSET   NOT NULL,
    CONSTRAINT PK_InboxMessages PRIMARY KEY (MessageId, Endpoint)
);
```

The second key column is the **receive endpoint**, and that choice is the whole
point of the composite key. One service can legitimately bind the same message
type on more than one endpoint — a normal-priority queue and a bulk/replay
queue, say — and each must process the message independently. Keying on
`MessageId` alone would let whichever finished first suppress the other.

It must **not** be the message type. A message has exactly one type, so
`(MessageId, MessageType)` is functionally `(MessageId)` — a composite key that
looks meaningful and distinguishes nothing.

It is also not the *handler*. `IntegrationEventConsumer<T>` (§9.4) runs every
registered `IIntegrationEventHandler<T>` for the message, and one inbox row
covers them all — which is correct, because they succeed or fail together and
are retried together. Ordering's two `ProductPublished` handlers
(`OrderSummaryProjection` and `ProductPriceProjection`) share one row.

Retention is the same story as the outbox, and needs the same purge — an inbox
nobody prunes grows for the life of the service and its composite-key index
degrades with it:

```sql
-- Older than the broker's maximum redelivery window. Pruning sooner would
-- let a late redelivery through as if it were new, which is exactly the
-- duplicate this table exists to stop.
DELETE TOP (5000) FROM ordering.InboxMessages
WHERE  HandledAt < DATEADD(day, -7, SYSDATETIMEOFFSET());
```

The window is a real constraint, not a round number: it must exceed the
broker's longest possible redelivery delay, including time a message spends in
the error queue before being replayed. Seven days is a starting point to check
against RabbitMQ's configured limits, not a default to accept.

Both purges — inbox and outbox (§9.4) — run from the same hosted service on a
slow schedule, batched so neither holds a long lock.

```csharp
namespace Common.Infrastructure.Inbox;

public sealed class InboxMessage(Guid messageId, string endpoint, DateTimeOffset handledAt)
{
    public Guid MessageId { get; private set; } = messageId;
    public string Endpoint { get; private set; } = endpoint;
    public DateTimeOffset HandledAt { get; private set; } = handledAt;
}
```

```csharp
// The service DbContext — not a separate one. Same database, one migration
// history, and EF-based handlers can share its transaction.
public sealed class InboxFilter<T>(OrderingDbContext db) : IFilter<ConsumeContext<T>>
    where T : class
{
    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        var messageId = context.MessageId
            ?? throw new InvalidOperationException("Message has no MessageId.");

        // The queue this message arrived on — the same type on a different
        // endpoint is a different unit of work.
        var endpoint = context.ReceiveContext.InputAddress.AbsolutePath.TrimStart('/');

        var alreadyHandled = await db.InboxMessages
            .AnyAsync(m => m.MessageId == messageId && m.Endpoint == endpoint);

        if (alreadyHandled)
            return;   // Silently drop the duplicate.

        db.InboxMessages.Add(new InboxMessage(messageId, endpoint, DateTimeOffset.UtcNow));

        // Ordering matters: the handler runs FIRST, and the inbox row is only
        // committed if it succeeded. Recording before would mark a message
        // handled that never was, losing it permanently on the next delivery.
        await next.Send(context);
        await db.SaveChangesAsync();
    }
}
```

> **The inbox is duplicate *suppression*, and only sometimes an atomic
> guarantee.** Whether the handler's work and the inbox record commit together
> depends entirely on how the handler writes:
>
> | Handler style | Atomic with the inbox row? |
> |---|---|
> | Writes through the injected `OrderingDbContext` | **Yes** — one `SaveChangesAsync`, one transaction |
> | Writes through `IDbConnectionFactory` + Dapper, like the projection in §6.6 | **No** — separate connection, separate transaction |
>
> For the second kind, a crash between `next.Send` returning and
> `SaveChangesAsync` committing leaves the work done and the message
> unrecorded, so redelivery runs it again.
>
> That is acceptable — but only because handlers are idempotent anyway. The
> inbox removes the *common* duplicate, not every duplicate. Treating it as a
> universal correctness guarantee rather than a partial optimisation is how
> at-least-once delivery quietly becomes at-most-once thinking.

Idempotency is easier still when the operation is naturally idempotent —
`MERGE`, `SET status = 'Confirmed'`, or an aggregate method that returns early
when already in the target state. Prefer that where the domain allows it; use
the inbox where it does not.

### 9.6 The order fulfilment saga

A saga coordinates a workflow across services without a distributed
transaction. Each step has a compensating action, and the saga's state is
persisted so the workflow survives a restart.

```mermaid
stateDiagram-v2
    [*] --> AwaitingStock : OrderPlaced

    AwaitingStock --> AwaitingPayment : StockReserved
    AwaitingStock --> [*] : StockReservationFailed → CancelOrder
    AwaitingStock --> [*] : StockTimeout 5m → CancelOrder

    AwaitingPayment --> Confirmed : PaymentAuthorised → ConfirmOrder
    AwaitingPayment --> Compensating : PaymentDeclined → ReleaseStock
    AwaitingPayment --> Compensating : PaymentTimeout 15m → ReleaseStock

    Compensating --> [*] : StockReleased → CancelOrder
    Compensating --> [*] : ReleaseTimeout 10m → CancelOrder + FlagOrderForReview

    Confirmed --> [*] : ShipmentDispatched → MarkOrderShipped
    Confirmed --> [*] : DespatchTimeout 3d → FlagOrderForReview
```

The diagram has exactly the states the machine declares and no others. Earlier
it showed `Cancelled` and `Shipped` as states; they are terminal *outcomes*, and
`SetCompletedWhenFinalized()` deletes the instance at that point, so a state
for them would be one no saga is ever observed in. A picture that shows states
the code does not have is a specification the code silently fails to meet —
which is how the missing payment timeout survived: the diagram claimed it.

Commands are contracts too, owned by the service that accepts them (§3.2), and
each owns its payload types:

```csharp
namespace Common.Contracts.Inventory.V1;

public sealed record ReserveStock(Guid OrderId, IReadOnlyList<StockLine> Lines);
public sealed record ReleaseStock(Guid OrderId);

// Not PlacedLine. Reserving stock needs no price, and Inventory's command must
// not have to change because Ordering versioned an event (§9.1).
public sealed record StockLine(Guid ProductId, int Quantity);
```

```csharp
namespace Common.Contracts.Payments.V1;

public sealed record AuthorisePayment(
    Guid OrderId, Guid CustomerId, decimal Amount, string Currency);
```

```csharp
namespace Common.Contracts.Ordering.V1;

// Reason is a STRING code, not Ordering's CancellationReason enum. A published
// contract carrying a domain type drags Ordering.Domain into every service that
// references the contract assembly (§9.1, §4.3) — and pins the enum's member
// names as wire format, so renaming one becomes a breaking change to everybody.
public sealed record CancelOrder(Guid OrderId, string Reason);

// Despatch is Shipping's fact; recording it on the order is Ordering's
// decision, so the saga sends a command rather than Ordering subscribing to
// ShipmentDispatched directly. The aggregate still enforces the transition.
public sealed record MarkOrderShipped(Guid OrderId, string TrackingNumber);

// Escalation path for a wait with no automatic compensation (§9.6). This does
// NOT touch the Order aggregate: the order's own state has not changed, and
// "a human should look at this" is a fact about the process, not about the
// order. It lands in an operations table instead.
public sealed record FlagOrderForReview(Guid OrderId, string Reason);

public static class ReviewReasons
{
    public const string NotDespatched     = "not_despatched";
    public const string StockNotReleased  = "stock_not_released";
}

/// <summary>
/// The wire vocabulary for CancelOrder.Reason. Ordering's handler parses these
/// back into CancellationReason; the mapping is one method in one place, and
/// an unknown code fails loudly rather than defaulting.
/// </summary>
public static class CancelReasons
{
    public const string OutOfStock      = "out_of_stock";
    public const string StockTimeout    = "stock_timeout";
    public const string PaymentDeclined = "payment_declined";
    // A declined payment and one that never answered compensate identically
    // and mean opposite things: the first is the customer's bank saying no,
    // the second is the PSP saying nothing. They are one dimension value apart
    // on orders.cancelled (§13.3) and a different incident.
    public const string PaymentTimeout  = "payment_timeout";
    public const string CustomerRequest = "customer_request";
}

// Likewise a string: PaymentReference is Ordering's value object, and the
// reference itself originates in Payments as an opaque provider token.
public sealed record ConfirmOrder(Guid OrderId, string PaymentReference);
```

> **A contract may not name a domain type.** It is the easiest rule in this
> document to break, because the domain type is always right there and always
> more expressive. The test is mechanical: if the contract assembly needs a
> project reference to any `*.Domain`, the contract is wrong. Enums are the
> most common offender — they look like primitives and are not.

Endpoint addresses are declared once so the saga reads as intent rather than as
string handling:

```csharp
namespace Ordering.Infrastructure.Messaging;

internal static class Endpoints
{
    // "queue:" is MassTransit's short-address form, resolved against the
    // configured transport. Names must match the ReceiveEndpoint declarations
    // in each owning service.
    public static readonly Uri InventoryQueue = new("queue:inventory-commands");
    public static readonly Uri PaymentsQueue  = new("queue:payments-commands");
    public static readonly Uri OrderingQueue  = new("queue:ordering-commands");
}
```

> The alternative is `EndpointConvention.Map<ReserveStock>(...)` at startup,
> which lets activities call `.Send(ctx => ...)` with no address. It reads more
> cleanly and fails at runtime rather than compile time if a mapping is missed.
> Explicit addresses are used here because a blueprint should show where the
> message goes.

```csharp
using static Ordering.Infrastructure.Messaging.Endpoints;

public sealed class OrderFulfilmentSaga : MassTransitStateMachine<OrderFulfilmentState>
{
    // Every state in the §9.6 diagram, including the ones a saga could
    // technically skip by finalising early. Confirmed exists because the order
    // is not done at payment — it is waiting for despatch, and a wait the
    // machine cannot represent is a wait it cannot time out.
    public State AwaitingStock   { get; private set; } = null!;
    public State AwaitingPayment { get; private set; } = null!;
    public State Confirmed       { get; private set; } = null!;
    public State Compensating    { get; private set; } = null!;

    public Event<OrderPlaced>              OrderPlaced             { get; private set; } = null!;
    public Event<StockReserved>            StockReserved           { get; private set; } = null!;
    public Event<StockReservationFailed>   StockReservationFailed  { get; private set; } = null!;
    public Event<PaymentAuthorised>        PaymentAuthorised       { get; private set; } = null!;
    public Event<PaymentDeclined>          PaymentDeclined         { get; private set; } = null!;
    public Event<StockReleased>            StockReleased           { get; private set; } = null!;
    public Event<ShipmentDispatched>       ShipmentDispatched      { get; private set; } = null!;

    // One schedule per wait. "Every wait has a timeout" is a rule the machine
    // must be able to express, not a habit to remember at each transition.
    public Schedule<OrderFulfilmentState, StockReservationExpired> StockTimeout { get; private set; } = null!;
    public Schedule<OrderFulfilmentState, PaymentAuthorisationExpired> PaymentTimeout { get; private set; } = null!;
    public Schedule<OrderFulfilmentState, DespatchExpired> DespatchTimeout { get; private set; } = null!;
    public Schedule<OrderFulfilmentState, StockReleaseExpired> ReleaseTimeout { get; private set; } = null!;

    public OrderFulfilmentSaga()
    {
        InstanceState(x => x.CurrentState);

        Event(() => OrderPlaced,   x => x.CorrelateById(m => m.Message.OrderId));
        Event(() => StockReserved, x => x.CorrelateById(m => m.Message.OrderId));
        // ... remaining correlations

        Schedule(() => StockTimeout, x => x.StockTimeoutTokenId, s =>
        {
            s.Delay = TimeSpan.FromMinutes(5);
            s.Received = e => e.CorrelateById(m => m.Message.OrderId);
        });

        // Payment authorisation involves a third party and is the wait most
        // likely to hang. Longer than stock because a PSP retry is normal.
        Schedule(() => PaymentTimeout, x => x.PaymentTimeoutTokenId, s =>
        {
            s.Delay = TimeSpan.FromMinutes(15);
            s.Received = e => e.CorrelateById(m => m.Message.OrderId);
        });

        // Despatch is measured in days, and unlike the other two it has no
        // automatic compensation — payment is taken and stock is gone. The
        // timeout escalates to a human instead. A wait with no compensating
        // action still needs a bound; "no timeout" is not the alternative.
        Schedule(() => DespatchTimeout, x => x.DespatchTimeoutTokenId, s =>
        {
            s.Delay = TimeSpan.FromDays(3);
            s.Received = e => e.CorrelateById(m => m.Message.OrderId);
        });

        // Compensation is a wait like any other. Stock that is never released
        // is stock nobody can sell, and a saga stuck mid-compensation is the
        // worst place to be stuck — the order is already failing.
        Schedule(() => ReleaseTimeout, x => x.ReleaseTimeoutTokenId, s =>
        {
            s.Delay = TimeSpan.FromMinutes(10);
            s.Received = e => e.CorrelateById(m => m.Message.OrderId);
        });

        Initially(
            When(OrderPlaced)
                .Then(ctx =>
                {
                    ctx.Saga.OrderId    = ctx.Message.OrderId;
                    ctx.Saga.CustomerId = ctx.Message.CustomerId;
                    ctx.Saga.Total      = ctx.Message.TotalAmount;
                    ctx.Saga.Currency   = ctx.Message.Currency;
                    ctx.Saga.StartedAt  = ctx.Message.OccurredAt;
                })
                .Schedule(StockTimeout, ctx => new StockReservationExpired(ctx.Saga.OrderId))
                // Send, not Publish — these are commands with one owner.
                // Mapped, not forwarded: ReserveStock owns its line type, so
                // versioning OrderPlaced does not version Inventory's command.
                .Send(InventoryQueue, ctx => new ReserveStock(
                    ctx.Saga.OrderId,
                    ctx.Message.Lines
                       .Select(l => new StockLine(l.ProductId, l.Quantity))
                       .ToArray()))
                .TransitionTo(AwaitingStock));

        During(AwaitingStock,
            When(StockReserved)
                .Unschedule(StockTimeout)
                // Currency travels with the amount — a bare decimal is a
                // charge waiting to be made in the wrong denomination.
                .Send(PaymentsQueue, ctx => new AuthorisePayment(
                    ctx.Saga.OrderId, ctx.Saga.CustomerId,
                    ctx.Saga.Total, ctx.Saga.Currency))
                // Arm the next wait in the same activity that begins it.
                .Schedule(PaymentTimeout, ctx => new PaymentAuthorisationExpired(ctx.Saga.OrderId))
                .TransitionTo(AwaitingPayment),

            When(StockReservationFailed)
                .Unschedule(StockTimeout)
                // String codes, not the domain enum — see the contracts above.
                .Send(OrderingQueue, ctx => new CancelOrder(
                    ctx.Saga.OrderId, CancelReasons.OutOfStock))
                .Finalize(),

            When(StockTimeout.Received)
                .Send(OrderingQueue, ctx => new CancelOrder(
                    ctx.Saga.OrderId, CancelReasons.StockTimeout))
                .Finalize());

        During(AwaitingPayment,
            When(PaymentAuthorised)
                .Unschedule(PaymentTimeout)
                .Send(OrderingQueue, ctx => new ConfirmOrder(
                    ctx.Saga.OrderId, ctx.Message.Reference))
                // Not Finalize: the order is confirmed, not finished. It is now
                // waiting on Shipping, and that wait needs a state to live in.
                .Schedule(DespatchTimeout, ctx => new DespatchExpired(ctx.Saga.OrderId))
                .TransitionTo(Confirmed),

            When(PaymentDeclined)
                .Unschedule(PaymentTimeout)
                // Why we are compensating, recorded on entry. Both exits from
                // Compensating below are shared, and by the time one runs the
                // triggering event is gone — so the reason has to be state, not
                // something re-derived from the transition that is finishing.
                .Then(ctx => ctx.Saga.CancelReason = CancelReasons.PaymentDeclined)
                // Compensate: stock was reserved and must be released.
                .Send(InventoryQueue, ctx => new ReleaseStock(ctx.Saga.OrderId))
                .Schedule(ReleaseTimeout, ctx => new StockReleaseExpired(ctx.Saga.OrderId))
                .TransitionTo(Compensating),

            When(PaymentTimeout.Received)
                // Same compensation as a decline — an answer that never came
                // and an answer of "no" leave the same stock reserved. Not the
                // same reason: the stock branch above already distinguishes
                // out_of_stock from stock_timeout, and collapsing the payment
                // pair would make the PSP going quiet indistinguishable from
                // customers being declined on the one dashboard that asks.
                .Then(ctx => ctx.Saga.CancelReason = CancelReasons.PaymentTimeout)
                .Send(InventoryQueue, ctx => new ReleaseStock(ctx.Saga.OrderId))
                .Schedule(ReleaseTimeout, ctx => new StockReleaseExpired(ctx.Saga.OrderId))
                .TransitionTo(Compensating));

        During(Confirmed,
            When(ShipmentDispatched)
                .Unschedule(DespatchTimeout)
                .Send(OrderingQueue, ctx => new MarkOrderShipped(
                    ctx.Saga.OrderId, ctx.Message.TrackingNumber))
                .Finalize(),

            When(DespatchTimeout.Received)
                // Escalation, not compensation. The saga finalises because it
                // has nothing further to coordinate; a human now owns the order.
                .Send(OrderingQueue, ctx => new FlagOrderForReview(
                    ctx.Saga.OrderId, ReviewReasons.NotDespatched))
                .Finalize());

        During(Compensating,
            When(StockReleased)
                .Unschedule(ReleaseTimeout)
                // The reason recorded on entry, not a literal: this transition
                // is reached from a decline and from a timeout alike.
                .Send(OrderingQueue, ctx => new CancelOrder(
                    ctx.Saga.OrderId, ctx.Saga.CancelReason))
                .Finalize(),

            When(ReleaseTimeout.Received)
                // Cancel the order regardless — the customer must not be left
                // waiting on Inventory. The stranded reservation is escalated
                // separately, because it is Inventory's to resolve.
                .Send(OrderingQueue, ctx => new CancelOrder(
                    ctx.Saga.OrderId, ctx.Saga.CancelReason))
                .Send(OrderingQueue, ctx => new FlagOrderForReview(
                    ctx.Saga.OrderId, ReviewReasons.StockNotReleased))
                .Finalize());

        SetCompletedWhenFinalized();
    }
}
```

> **Commands are sent; events are published.** §9.1 defines an integration event
> as a published fact with any number of interested consumers, and `Publish` is
> the fan-out that matches. `ReserveStock`, `AuthorisePayment`, `CancelOrder`
> and `ConfirmOrder` are **commands** — imperative, addressed to exactly one
> owning service. `Publish`ing them delivers to every subscriber, so a second
> service that binds the type for any reason starts silently executing your
> business commands. Use `Send` with an explicit destination.
>
> The events the saga *reacts* to — `StockReserved`, `PaymentDeclined` — are
> genuine events and are published by their owners in the normal way.

#### Where an escalation lands

`FlagOrderForReview` is the one command here that changes no business state. Its
handler writes an operations row and stops — no aggregate is loaded, because
nothing about the order has changed. What changed is that the *process* stalled,
and that is not a fact the domain model should carry:

```sql
-- A work queue, not a log. A row means "a human still needs to look at this";
-- resolving one deletes it. There is no ResolvedAt, because a nullable
-- timestamp nothing sets is an alert that fires once and never clears — and
-- because "resolved" and "gone" are the same state for a queue.
CREATE TABLE ordering.OrderReviews
(
    OrderId  UNIQUEIDENTIFIER NOT NULL,
    Reason   VARCHAR(64)      NOT NULL,
    RaisedAt DATETIMEOFFSET   NOT NULL,
    CONSTRAINT PK_OrderReviews PRIMARY KEY (OrderId, Reason)
);

CREATE INDEX IX_OrderReviews_RaisedAt ON ordering.OrderReviews (RaisedAt);
```

> The audit trail of *what was escalated and when* lives in the event history,
> not here (§9.6) — so deleting the row loses nothing. Keeping a resolved row
> would mean building the back-office surface to set the flag, which this
> document does not have and does not need for the escalation to work.

```csharp
public sealed class FlagOrderForReviewHandler(IUnitOfWork unitOfWork)
    : ICommandHandler<FlagOrderForReviewCommand, Result>
{
    public async Task<Result> HandleAsync(
        FlagOrderForReviewCommand command, CancellationToken ct)
    {
        // Written through the unit of work, not a second Dapper connection.
        // Every command runs inside TransactionBehavior (§6.3); a handler that
        // opens its own connection commits outside that transaction, so its
        // write survives a command that failed. Harmless for an idempotent
        // escalation row, and a data-corruption bug the first time the pattern
        // is copied to a handler that is not.
        await unitOfWork.ExecuteRawAsync(
            """
            IF NOT EXISTS (SELECT 1 FROM ordering.OrderReviews
                           WHERE OrderId = @OrderId AND Reason = @Reason)
                INSERT INTO ordering.OrderReviews (OrderId, Reason, RaisedAt)
                VALUES (@OrderId, @Reason, SYSDATETIMEOFFSET());
            """,
            new { command.OrderId, command.Reason }, ct);

        return Result.Success();
    }
}
```

> **The rule this illustrates: a command handler writes through `IUnitOfWork`
> and nothing else.** Repositories for aggregates, `ExecuteRawAsync` for the
> occasional table that has no aggregate — both land in the one transaction the
> behaviour opened. `IDbConnectionFactory` belongs to *queries* (§6.5) and to
> *projections*, which run after commit by design (ADR-018). Its appearance in a
> command handler means a write outside the transaction, which is exactly the
> case §6.3's boundary was drawn to prevent.

This table is alerted on in §13.6 — an outstanding review is a stalled order
the saga has already given up on, so nothing else will surface it.

Saga design rules:

- **Every forward step has a compensating action.** If you cannot describe the
  compensation, the step is not safe to take.
- **Every wait has a timeout** — and where no compensation exists, the timeout
  escalates instead. A saga waiting forever for a message that will never
  arrive is an order stuck in limbo and a support ticket.
- **The saga holds only coordination state**, never business logic. Deciding
  *whether* an order can be cancelled is `Order.Cancel`'s job; the saga only
  decides *when* to ask.
- **Compensation is not rollback.** Releasing stock is a new business fact, not
  an undo. The reservation happened, and both facts belong in the audit trail —
  which is the *event* history, not the saga. `SetCompletedWhenFinalized()`
  deletes the instance on completion, and the outbox purges processed rows after
  a week, so anything that must be explicable months later is a domain event on
  the aggregate (`OrderCancelledDomainEvent` carries its `CancellationReason`), never a
  saga row.
- **Persist saga state in SQL Server**, in the service's own database. Not for
  atomicity — the saga's effects reach other services as messages and never
  share a transaction with them (ADR-002, §9.7). The reasons are operational:
  one database per service to back up, one migration history, one connection
  pool, and the saga table sits next to the orders it coordinates when someone
  is debugging at 03:00.

#### Saga state

The instance carries only what the transitions need. Every field in §9.6's
state machine, and nothing else:

```csharp
public sealed class OrderFulfilmentState : SagaStateMachineInstance
{
    // MassTransit correlates on this. CorrelateById(m => m.Message.OrderId)
    // in §9.6 means it always holds the order's id.
    public Guid CorrelationId { get; set; }

    public string CurrentState { get; set; } = null!;

    // Same value as CorrelationId, kept as a named property because eight call
    // sites read better as ctx.Saga.OrderId than as ctx.Saga.CorrelationId.
    // Assigned once in Initially; never written again.
    public Guid OrderId { get; set; }

    public Guid CustomerId { get; set; }
    public decimal Total { get; set; }
    public string Currency { get; set; } = null!;
    public DateTimeOffset StartedAt { get; set; }

    // Set on entry to Compensating, read by both exits from it. `null!` like
    // CurrentState and Currency above: the state machine guarantees it is
    // written before any transition reads it, so the property is not nullable
    // even though the column is — a saga that never compensates stores NULL,
    // and that is a fact about the row rather than a case the code handles.
    public string CancelReason { get; set; } = null!;

    // One token per schedule — Unschedule needs the specific token, so two
    // waits cannot share a field.
    public Guid? StockTimeoutTokenId { get; set; }
    public Guid? PaymentTimeoutTokenId { get; set; }
    public Guid? DespatchTimeoutTokenId { get; set; }
    public Guid? ReleaseTimeoutTokenId { get; set; }
}
```

No `RowVersion`. The repository below runs `ConcurrencyMode.Pessimistic`, which
takes row locks rather than comparing a version column — carrying one anyway
would imply an optimistic strategy the saga does not use.

```sql
CREATE TABLE ordering.OrderFulfilmentStates
(
    CorrelationId        UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    CurrentState         VARCHAR(64)      NOT NULL,
    OrderId              UNIQUEIDENTIFIER NOT NULL,
    CustomerId           UNIQUEIDENTIFIER NOT NULL,
    Total                DECIMAL(19,4)    NOT NULL,
    Currency             CHAR(3)          NOT NULL,
    StartedAt              DATETIMEOFFSET   NOT NULL,
    -- Why the saga is compensating; NULL until it is. VARCHAR because it holds
    -- a CancelReasons code (§9.6), the same vocabulary the wire uses.
    CancelReason           VARCHAR(32)      NULL,
    StockTimeoutTokenId    UNIQUEIDENTIFIER NULL,
    PaymentTimeoutTokenId  UNIQUEIDENTIFIER NULL,
    DespatchTimeoutTokenId UNIQUEIDENTIFIER NULL,
    ReleaseTimeoutTokenId  UNIQUEIDENTIFIER NULL
);

-- Backs the "unfinalised saga" alert (§13.6) and the stuck-saga runbook.
-- Without it that alert is a query with no table.
CREATE INDEX IX_OrderFulfilmentStates_StartedAt
    ON ordering.OrderFulfilmentStates (StartedAt)
    INCLUDE (CurrentState);
```

```csharp
// In AddMassTransitMessaging (§4.2). The repository is not optional:
// MassTransit throws at startup without one, and the in-memory repository
// used in tests (§12.5) discards every in-flight order on restart.
cfg.AddSagaStateMachine<OrderFulfilmentSaga, OrderFulfilmentState>()
   .EntityFrameworkRepository(r =>
   {
       r.ExistingDbContext<OrderingDbContext>();
       // Pessimistic: two events for the same order can arrive concurrently
       // (StockReserved and a timeout), and optimistic retry on a state
       // machine replays transitions that already ran.
       r.ConcurrencyMode = ConcurrencyMode.Pessimistic;
   });
```

Because the repository shares `OrderingDbContext`, the saga table lives in the
service's own database and its migrations travel with the service's — which is
what "in the service's own database" above buys, and what the in-memory
repository in §12.5 deliberately trades away for test speed.

### 9.7 Synchronous calls

Some interactions genuinely need an answer now — the order form must show a
price before the customer submits. For those, use gRPC between services rather
than HTTP+JSON: it is faster, and the generated client and contract-first
`.proto` remove a category of drift.

Note which caller this is. The example below belongs to the **BFF**, rendering
a form: if Catalog is slow the user sees a spinner. The *command* path never
calls Catalog — it reads a local price projection (§6.4), because a write that
depends on another service being up is a write that inherits its downtime.
"Needs an answer now" is a property of a screen, not of a transaction.

#### The hop budget

> **Decision — maximum one synchronous downstream hop per inbound request.** See [ADR-017](#adr-017--one-synchronous-hop).

`Client → Gateway → A → B` is permitted. `A → B → C` is **not**, and neither is
any deeper chain. Stated as a number rather than as advice, because "avoid long
chains" is unenforceable in code review while "one hop" is checkable.

**The budget is depth, not fan-out.** A service may call two or three peers
concurrently and still be within budget — what it may not do is call a service
that itself calls another. Depth multiplies latency and failure probability;
concurrent fan-out costs only the slowest call. That is the rule; §10.1's
two-BFF diagram illustrates it, and is a picture of the pattern rather than of
this platform.

**Here the budget is barely spent.** The BFF makes exactly one call — the
pricing hop to Catalog below — and Catalog calls nobody, so the deepest chain
in the platform is `Client → Gateway → BFF → Catalog`. The allowance for
fan-out is stated because it is the rule the next reviewer will need, not
because anything uses it yet.

That said, fan-out is not free — each additional call adds a failure mode and
another dependency to the caller's availability. Beyond about three, the data
should be arriving by event and being projected locally instead.

The arithmetic is why. Each hop multiplies availability and adds its full
latency to the caller's p99. Four services at 99.9% chained give 99.6% — from
43 minutes of monthly downtime to nearly three hours, with no single service
having failed its own SLO.

> **Synchronous calls inside message consumers are forbidden by default.** A
> consumer that calls another service converts a durable, retryable, queued
> operation into one that fails when the callee is down — discarding the main
> reason to have used a broker. Where it is genuinely unavoidable, it requires a
> written architecture-review exception recorded as an ADR, not a code comment.

If you find yourself needing a second hop, the answer is almost always that the
data should have arrived by event and been projected locally.

#### Timeout hierarchy

Timeouts must **decrease** at every level inwards. If an inner timeout exceeds
an outer one, the outer layer abandons the request while the inner work
continues — consuming a connection and a thread for an answer nobody will read.

| Layer | Typical | Constraint |
|---|---|---|
| Gateway request timeout | 30–60 s | Highest |
| Service operation total | 10–30 s | < gateway |
| Outbound client total (incl. retries) | 3–5 s | < service operation |
| Outbound per-attempt | 1–2 s | (× attempts) **+ backoff** ≤ client total |

The values are configuration and will differ per system. **The ordering is the
invariant** — that part is not negotiable, and it is what to assert in a
configuration-validation test at startup.

#### Rules for every synchronous call

1. **Timeout.** One to two seconds per attempt, per the table above; never
   infinite. Where in that band is decided by the arithmetic below, not by
   taste — the attempts plus their backoff have to fit the client total.
2. **Circuit breaker.** After a threshold of failures, fail fast rather than
   queueing threads against a dead service.
3. **A fallback.** Cached data, a degraded response, or a clear error — decided
   in advance, not improvised during an incident.
4. **Retry only idempotent operations.** Retrying a `POST` that creates a
   payment creates two payments. `GET` and explicitly idempotent endpoints only.
5. **Within the hop budget.** See above.

The configuration below satisfies the table rather than merely gesturing at it,
and the budget is worked out including the waiting: 3 × 1.4 s of attempts plus
150 ms + 300 ms of backoff is 4.65 s, which fits inside the 5 s ceiling with
room for jitter to widen the delays:

```csharp
// Web.Bff/Program.cs (§4.1). Not Ordering's — see the paragraph above, and
// §4.2's helper, which deliberately registers none of this. The BFF is the
// only host in this blueprint that calls a peer synchronously, which makes it
// the only one holding client credentials (§11.5).
services.AddTransient<ClientCredentialsHandler>();
services.AddSingleton<ITokenCache, CachingTokenClient>();
services.AddOptions<ServiceIdentityOptions>()
        .BindConfiguration("Identity:Client")
        .ValidateDataAnnotations()
        .ValidateOnStart();

services
    // http, not https: TLS terminates at the ingress and traffic inside the
    // cluster is plain (§10.1). The host is the Service name YARP also routes
    // to (§10.2) — "catalog" resolves to nothing.
    .AddGrpcClient<Pricing.PricingClient>(o =>
        o.Address = new Uri("http://catalog-api:8080"))
    // Resilience is registered FIRST so it sits outermost, and the credential
    // handler runs inside it. That ordering matters: a retry then re-attaches
    // a token, which is what recovers the case where the first attempt failed
    // because the token expired in flight. Registered the other way round, all
    // three attempts would reuse the same dead token.
    .AddStandardResilienceHandler(options =>
    {
        // Outermost bound. Defaults to 30s, which would breach the hierarchy.
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(5);

        options.Retry.MaxRetryAttempts   = 2;          // 3 attempts in total
        options.Retry.BackoffType        = DelayBackoffType.Exponential;
        options.Retry.UseJitter          = true;
        options.Retry.Delay              = TimeSpan.FromMilliseconds(150);

        // 3 × 1.4 s + 150 ms + 300 ms = 4.65 s. The delays are part of the
        // budget, not an extra on top of it — see the trap below.
        options.AttemptTimeout.Timeout   = TimeSpan.FromSeconds(1.4);

        options.CircuitBreaker.FailureRatio      = 0.5;
        options.CircuitBreaker.MinimumThroughput = 10;
        options.CircuitBreaker.BreakDuration     = TimeSpan.FromSeconds(15);
    })
    // Registered AFTER resilience, so it sits inside it (§11.5).
    .AddHttpMessageHandler<ClientCredentialsHandler>();
```

> **Trap — `TotalRequestTimeout` left at its default.** It defaults to 30
> seconds, which is longer than most services' own operation budget and longer
> than some gateway timeouts. Every resilience handler in the system must set it
> explicitly.
>
> **And the sum that has to fit inside it includes the backoff.** The obvious
> budget is `AttemptTimeout × (MaxRetryAttempts + 1)`; the real one adds the
> delays *between* those attempts, which for exponential backoff is
> `Delay × (2ⁿ − 1)`. Leave the delays out and the arithmetic clears the ceiling
> while the configuration does not: at 1.5 s and a 200 ms base the attempts
> alone come to 4.5 s against a 5 s total and look fine, but the two waits push
> the real worst case to 5.1 s — so the third attempt is cancelled part-way and
> the request fails having never completed the retry that was meant to save it.
> The failure looks like a slow dependency rather than a misconfigured client,
> which is why it needs an assertion and not a review.

Assert this at startup rather than trusting review:

```csharp
[Fact]
public void Resilience_timeouts_respect_the_hierarchy()
{
    var o = GetConfiguredOptions();

    var attempts = o.AttemptTimeout.Timeout * (o.Retry.MaxRetryAttempts + 1);

    // The waits between attempts, not just the attempts. Exponential backoff
    // from a base d over n retries sums to d × (2ⁿ − 1); a linear or constant
    // policy would be d × n. Omitting this term is what lets a configuration
    // that overruns its own ceiling pass a test written to prevent exactly that.
    var backoff = o.Retry.BackoffType switch
    {
        DelayBackoffType.Exponential =>
            o.Retry.Delay * ((1 << o.Retry.MaxRetryAttempts) - 1),
        _ => o.Retry.Delay * o.Retry.MaxRetryAttempts
    };

    (attempts + backoff)
        .ShouldBeLessThanOrEqualTo(
            o.TotalRequestTimeout.Timeout,
            "the last attempt must be able to finish inside the total budget, " +
            "otherwise it is cancelled part-way and the retry never had a chance " +
            "to help (§9.7).");

    o.TotalRequestTimeout.Timeout
        .ShouldBeLessThan(ServiceOptions.OperationTimeout);
}
```

### 9.8 Failure handling

| Failure | Handling |
|---|---|
| Transient (network, deadlock, timeout) | Retry with exponential backoff and jitter, 3–5 attempts |
| Persistent (bad data, bug) | Move to the error queue after retries; alert |
| Poison message | Never redeliver indefinitely; cap attempts and quarantine |
| Consumer down | Messages queue in the broker; monitor queue depth |
| Broker down | Outbox holds messages; they flush on reconnect |

Retry and idempotency are configured per receive endpoint, and Ordering has
three, each with a different policy. The **projection** endpoint from §9.4,
carrying Catalog's events into local read models:

```csharp
cfg.ReceiveEndpoint("ordering-catalog-events", e =>
{
    e.UseMessageRetry(r => r.Exponential(
        retryLimit: 5,
        minInterval: TimeSpan.FromSeconds(1),
        maxInterval: TimeSpan.FromMinutes(1),
        intervalDelta: TimeSpan.FromSeconds(2)));

    // Defers any Publish/Send until the consumer completes, so a retry does
    // not re-emit messages the failed attempt already sent.
    e.UseInMemoryOutbox();

    // Duplicate suppression — §9.5. On this endpoint and on ordering-commands,
    // and on any endpoint added later: at-least-once delivery is a property of
    // the broker, not of the message type or of what the consumer does with it.
    // The saga endpoint below is the one exception, and says why.
    e.UseConsumeFilter(typeof(InboxFilter<>), context);

    e.ConfigureConsumer<IntegrationEventConsumer<ProductPublished>>(context);
    e.ConfigureConsumer<IntegrationEventConsumer<PriceChanged>>(context);
    e.ConfigureConsumer<IntegrationEventConsumer<ProductDiscontinued>>(context);
});
```

And the **saga** endpoint, which receives the fulfilment events (§9.6):

```csharp
cfg.ReceiveEndpoint("ordering-fulfilment-saga", e =>
{
    e.UseMessageRetry(r => r.Exponential(
        retryLimit: 5,
        minInterval: TimeSpan.FromSeconds(1),
        maxInterval: TimeSpan.FromMinutes(1),
        intervalDelta: TimeSpan.FromSeconds(2)));

    e.UseInMemoryOutbox();

    // No InboxFilter here. The saga is idempotent by construction: a redelivered
    // StockReserved finds the instance already past AwaitingStock and the
    // transition is simply not applicable. Adding an inbox row would suppress
    // legitimate redelivery after a mid-transition crash.
    e.ConfigureSaga<OrderFulfilmentState>(context);
});
```

> **The inbox is the default; the saga is the documented exception.** Every
> receive endpoint applies `InboxFilter<>` — the projection endpoint above and
> `ordering-commands` (§9.4) both do, and what the consumer dispatches to is not
> the criterion: a redelivered command is as duplicable as a redelivered event.
> State machines opt out because their state *is* the idempotency check. Any
> other opt-out needs the same kind of written justification, in the endpoint
> that takes it.

And the **command** endpoint, `ordering-commands` (§9.4), which is the one whose
retry policy is not the plain exponential five:

```csharp
e.UseMessageRetry(r =>
{
    // A malformed contract does not become well-formed on the fourth attempt.
    r.Ignore<ContractMappingException>();
    r.Exponential(5, TimeSpan.FromSeconds(1),
                     TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(2));
});
```

**A domain rejection is not on that list because it never throws.** §9.4's
consumer acks it, counts `command.domain_rejected` and logs it at warning. The
reasoning took a correction worth recording, because the intermediate position
was wrong in a way that looked careful:

| Position | Problem |
|---|---|
| Retry it | A shipped order is still shipped on the fifth attempt. Five backoffs, then the error queue anyway |
| Throw, exclude from retry | Reaches the error queue **once** instead of after a minute — but a routine outcome now sits in a queue whose depth alert pages a human |
| Ack, count, log | The queue holds only faults, so depth > 0 stays a page worth answering |

The middle option was the previous revision of this document. It fixed the
backoff and left the alert, which is the half-fix that reads as done: the
message arrives faster at a place it should never have been.

The objection to acking was that a swallowed command disappears. That was true
when there was nothing else recording it, and stopped being true once the
counter and the log existed. **An outcome with a metric and a log line is not
silent**; a message in a queue nobody drains is closer to it.

> **A saga must not wait on a command succeeding.** Acking means the sender
> learns nothing from the rejection — by design, since a reply channel would put
> the saga back into synchronous coupling with the receiver (§9.7). Every saga
> step therefore has a timeout that fires whether the command was refused, never
> delivered, or is still in flight (§9.6), and `command.domain_rejected` is where
> a person finds out *which*. A command that fails often enough to need a
> happy-path answer is a command that should be an event the saga subscribes to.

The distinction generalises: **retry is for faults that time might fix.** A
broker blip, a deadlock, an expired token — retry those. A message the receiver
cannot interpret will be rejected identically five times and hold the queue open
while it happens, so it belongs in the error queue on the first attempt. A
command the domain refused belongs in neither: it is not a fault, and the queue
is not where answers go.

**Alert on error-queue depth greater than zero.** A message in the error queue
is a business process that stopped. It needs a human, and the alert is how they
find out before the customer does.

**That alert is only defensible because domain rejections do not land there.**
An expected outcome sharing a queue with genuine faults makes depth > 0 routine,
and an alert that fires routinely trains its recipients to close it — which
costs more than the noise it was meant to surface. Keeping the queue to faults
is what lets the threshold stay at zero, which is the only threshold nobody has
to interpret.

Rejections get their own instrument instead. `MessagingMetrics.Rejected`
(§13.3) writes `command.domain_rejected`, tagged with the message type and the
`Error.Code` (§10.5) — a closed vocabulary, so the tag cannot explode. It
belongs on a dashboard rather than on a pager: a spike in
`order.already_shipped` is usually a saga bug, and a spike in
`order.products_unavailable` means Catalog stopped publishing (§6.6). Neither
is an incident at 3am, and both are invisible without the counter.

> **The tag is an `Error.Code` and nothing else.** It is tempting to reach for a
> cancellation reason here — `payment_declined` reads like something worth
> counting, and it *is* counted, on `orders.cancelled` (§13.3). But the two
> vocabularies describe opposite events. A payment-declined cancellation is a
> command the domain **accepted**: the saga sent `CancelOrder`, the aggregate
> allowed the transition, the handler returned success. It can never appear on
> a counter of commands the domain refused.
>
> Mixing them produces a series that looks meaningful and measures nothing, and
> the mistake is invisible in a dashboard — both are lowercase snake_case
> strings on a counter tagged `error`. §11.4 keeps `CancelReasons` and
> `OrderErrors` apart in code for the same reason; the metric has to keep them
> apart too.

The rule the two encode together: **the error queue is a work list, not a
metric.** Anything that routinely lands there needs a fix, a counter, or
somewhere else to go — and a queue nobody drains to zero has stopped being an
alert.

---

## 10. API Gateway

### 10.1 Responsibilities

The gateway is the single entry point for external clients. It handles what is
genuinely cross-cutting at the edge, and nothing else.

**It does:** routing · JWT signature and claims validation · rate limiting ·
CORS · request/response logging with correlation IDs · response compression ·
request size limits.

**It is not the outermost edge.** TLS terminates at the cloud load balancer or
Kubernetes Ingress, which then forwards plain HTTP inside the cluster. That
matters beyond TLS: everything the gateway does per-client — rate limiting
above all — reads `RemoteIpAddress`, which is the *ingress* address until
`UseForwardedHeaders` runs (§4.2). A gateway that assumes it is the edge
rate-limits the whole world as one client.

**It does not:** contain business logic · aggregate responses from several
services · transform payloads · access any database · know about domain
concepts.

> **Trap — the gateway that became a service.** Response aggregation is the
> gateway's most tempting feature and the start of its decline. Aggregation
> requires knowing what the pieces mean, which is business logic, which means
> the gateway now redeploys with every service change and becomes a shared
> bottleneck owned by nobody. Put aggregation in a **BFF** — a separate,
> team-owned service behind the gateway, one per client type.

```mermaid
graph LR
    Web[Web app] --> GW[Gateway<br/>YARP]
    Mob[Mobile app] --> GW
    Ext[Partner API] --> GW

    GW --> BFFW[Web BFF]
    GW --> BFFM[Mobile BFF]
    GW --> CAT[Catalog]
    GW --> ORD[Ordering]

    BFFW --> CAT
    BFFW --> ORD
    BFFM --> CAT
    BFFM --> ORD
```

The shape of the pattern, not this platform's topology: a second BFF per client
type is what the pattern looks like at scale, and the fan-out to two services is
what §9.7 permits. What is actually built here is one `Web.Bff` making one call
to Catalog (§2.2, §9.7) — the diagram is the ceiling, not the inventory.

### 10.2 YARP configuration

```json
{
  "ReverseProxy": {
    "Routes": {
      "catalog-public": {
        "ClusterId": "catalog",
        "Match": { "Path": "/api/v1/catalog/{**catch-all}", "Methods": [ "GET" ] },
        "RateLimiterPolicy": "anonymous",
        "Transforms": [
          { "PathRemovePrefix": "/api" },
          { "RequestHeader": "X-Forwarded-Prefix", "Set": "/api" }
        ]
      },
      "ordering": {
        "ClusterId": "ordering",
        "Match": { "Path": "/api/v1/orders/{**catch-all}" },
        "AuthorizationPolicy": "authenticated",
        "RateLimiterPolicy": "authenticated",
        "Transforms": [ { "PathRemovePrefix": "/api" } ]
      },
      "inventory-admin": {
        "ClusterId": "inventory",
        "Match": { "Path": "/api/v1/inventory/{**catch-all}" },
        "AuthorizationPolicy": "inventory:admin",
        "RateLimiterPolicy": "authenticated",
        "Transforms": [ { "PathRemovePrefix": "/api" } ]
      },
      "web-bff": {
        "ClusterId": "web-bff",
        "Match": { "Path": "/bff/{**catch-all}" },
        "AuthorizationPolicy": "authenticated",
        "RateLimiterPolicy": "authenticated",
        "Transforms": [ { "PathRemovePrefix": "/bff" } ]
      }
    },
    "Clusters": {
      "catalog": {
        "LoadBalancingPolicy": "PowerOfTwoChoices",
        "HealthCheck": {
          "Active": {
            "Enabled": true,
            "Interval": "00:00:10",
            "Timeout": "00:00:05",
            "Path": "/health/ready"
          }
        },
        "Destinations": {
          "d1": { "Address": "http://catalog-api:8080/" }
        }
      },
      "ordering": {
        "Destinations": { "d1": { "Address": "http://ordering-api:8080/" } }
      },
      "inventory": {
        "Destinations": { "d1": { "Address": "http://inventory-api:8080/" } }
      },
      "web-bff": {
        "Destinations": { "d1": { "Address": "http://web-bff:8080/" } }
      }
    }
  }
}
```

The two `"authenticated"` values on the `ordering` route are **not** the same
policy. `AuthorizationPolicy` resolves through `IAuthorizationPolicyProvider`
and `RateLimiterPolicy` through the rate limiter's own registry (§10.3); they
share a name here because both mean "a signed-in caller", and each has to be
registered separately.

**Both must be registered somewhere in the gateway's own container.**
`AuthorizationPolicy` is resolved through
`IAuthorizationPolicyProvider` when YARP loads this configuration; a name it
cannot resolve invalidates the route, which YARP then **drops**. The failure is
not a 500 and not a 403 — the path simply stops existing, and the gateway comes
up healthy serving whichever routes happened to validate. `authenticated` comes
from `AddCommonWebDefaults` (§13.2) and `inventory:admin` from the gateway's own
`Program.cs` (§4.2); `catalog-public` names none, which is the only correct way
to declare a route public.

The `web-bff` route is what makes the BFF reachable, and it is easy to skip:
the BFF has an image, a chart, a Keycloak client and a CI filter without one,
and none of those fail. §10.1 calls the gateway the single entry point for
external clients — so a service behind it with no route is not deployed
privately, it is deployed unreachably. `/bff` rather than `/api`, because a
client picks one or the other: aggregated responses shaped for a screen, or the
service APIs shaped for a resource.

Note the shape of the policy name. `inventory:admin` is a **permission**, not a
role, for the reason §11.4 gives — and the gateway is where role-shaped names
creep back in, because a route file feels like infrastructure rather than
authorization code.

**Every route carries a `RateLimiterPolicy`, including the admin ones.** YARP
applies no limit when the property is absent, so a route opts out of §10.1's
rate limiting by omission — and the route most likely to be forgotten is the
one added last, under pressure, to expose something internal. The
`inventory-admin` route is authenticated and narrowly authorised, which is
exactly the reasoning that
justifies leaving it unlimited and exactly why that reasoning is wrong: an
authorised client with a broken retry loop is still a flood, and this one
reaches an inventory database. Assert the invariant rather than reviewing for
it — deserialise the `Routes` section in a test and require the property on
every entry.

`PowerOfTwoChoices` picks two destinations at random and routes to the less
loaded of the pair. It avoids both the herd behaviour of least-requests and the
blindness of round-robin, at negligible cost.

In Kubernetes, destinations are Service DNS names and the platform handles
discovery. Active health checks still matter — they let YARP stop routing to a
pod that is failing but not yet failing its readiness probe.

#### API versioning and deprecation at the edge

Versions are URL segments because path matching is trivial to route on and
unambiguous in a log line. Header-based versioning is harder to test, harder to
curl, and invisible in access logs.

**One shape, end to end**, because the alternative is arithmetic every reader
has to redo:

| | Path |
|---|---|
| Client calls | `/api/v1/orders/{id}/cancel` |
| Gateway matches | `/api/v1/orders/{**catch-all}` |
| Gateway strips | `/api` |
| Service receives | `/v1/orders/{id}/cancel` |
| Service maps | `MapGroup("/v1/orders")` (§11.4) |

The version sits **before** the resource, and the prefix the gateway strips is
`/api` for every route *under `/api`* rather than a per-service prefix.
(`web-bff` strips `/bff` because it is a second namespace, not a service under
the first — one strip per namespace is still the rule, and `/bff` has exactly
one.) Stripping `/api/orders` instead would work, but only if the external path
were
`/api/orders/v1/orders/{id}` — the service segment twice, once for the gateway
and once for the service's own group — which is the shape a per-service strip
quietly requires and nobody chooses on purpose.

During a deprecation window the gateway routes **both** versions to the same
cluster. A new version is a **new route entry**, which means it re-declares the
policies rather than inheriting them — the route above it in the file is not a
base class, and a version added by copying only the `Match` and the `Transforms`
is a public, unlimited copy of an authenticated, limited endpoint:

```json
"ordering-v1": {
  "ClusterId": "ordering",
  "Match": { "Path": "/api/v1/orders/{**catch-all}" },
  "AuthorizationPolicy": "authenticated",
  "RateLimiterPolicy": "authenticated",
  "Transforms": [ { "PathRemovePrefix": "/api" } ]
},
"ordering-v2": {
  "ClusterId": "ordering",
  "Match": { "Path": "/api/v2/orders/{**catch-all}" },
  "AuthorizationPolicy": "authenticated",
  "RateLimiterPolicy": "authenticated",
  "Transforms": [ { "PathRemovePrefix": "/api" } ]
}
```

> **Trap — mismatched prefix strips on dual-version routes.** Both routes must
> strip the *same* prefix, so the service receives `/v1/...` and `/v2/...` and
> can route internally. Stripping `/api/v1` on one route and `/api` on the
> other sends the service two different path shapes for the same endpoint. It
> works in whichever one was tested first.
>
> **The in-process API tests do not catch this.** They call the service
> directly, on `/v1/orders/...` (§12.4), so they exercise everything after the
> strip and nothing before it. Path composition is gateway configuration, and
> §15.1's config test — every route's policies resolve — is the only place it
> is checked; extend it to assert each route's stripped path against the group
> its service maps.

Deprecation is signalled with standard headers rather than a changelog nobody
reads (RFC 8594 / RFC 9745):

```csharp
app.MapGroup("/v1")
   .AddEndpointFilter(async (ctx, next) =>
   {
       ctx.HttpContext.Response.Headers["Deprecation"] = "true";
       ctx.HttpContext.Response.Headers["Sunset"] = "Wed, 31 Dec 2026 23:59:59 GMT";
       ctx.HttpContext.Response.Headers["Link"] =
           "</api/v2/orders>; rel=\"successor-version\"";
       return await next(ctx);
   });
```

Retire v1 only when telemetry confirms no traffic remains on it — not when the
Sunset date passes. The date is a commitment to clients; the metric is the
evidence.

### 10.3 Rate limiting

```csharp
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("anonymous", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window      = TimeSpan.FromMinutes(1),
                QueueLimit  = 0
            }));

    options.AddPolicy("authenticated", context =>
        RateLimitPartition.GetTokenBucketLimiter(
            partitionKey: context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                          ?? context.Connection.RemoteIpAddress?.ToString()
                          ?? "unknown",
            factory: _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit          = 300,
                TokensPerPeriod     = 300,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit          = 10,
                AutoReplenishment   = true
            }));

    options.OnRejected = async (context, ct) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString();

        await context.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title  = "Too many requests",
            Type   = "https://tools.ietf.org/html/rfc6585#section-4"
        }, ct);
    };
});
```

The `authenticated` policy is only correct if `UseAuthentication` has already
run when the limiter middleware executes — see the pipeline in §4.2. The
`?? RemoteIpAddress` fallback exists for the genuinely anonymous request that
still matches an authenticated route, not as a safety net for pipeline order;
if the order is wrong the fallback absorbs *every* request and the policy
degrades to a second copy of `anonymous` with a larger budget.

**The v1 decision is per-replica, best-effort, and the numbers above are written
knowing it.** `System.Threading.RateLimiting` partitions in process, so with N
gateway replicas the effective limit is N × the configured value: three replicas
turn the `authenticated` policy's 300/minute into 900/minute for a client
unlucky enough — or determined enough — to spread requests across all three.

That is acceptable here because these limits exist to blunt accidental
hammering and cheap abuse, and a bound that is loose by a factor of the replica
count still does that. It is **not** acceptable for anything a customer is
billed against or promised in a contract. Partner quotas, per-tenant plans and
anything with a number in a service agreement need a shared counter — a sliding
window on the **coordination** Redis (§8.1, `{service}:ratelimit:`), never the
cache instance, whose `allkeys-lru` policy would evict a counter mid-window and
reset somebody's quota with no error and no log line.

Two things to settle before building that, because both are easy to discover
late: the counter's TTL must outlive the window it measures, and the limiter
needs a stated behaviour when Redis is unreachable. **Fail open** is the right
default at the edge — a rate limiter that returns 429 because its own
dependency is down converts a Redis incident into a full outage — but it is a
decision to make deliberately, not one to inherit from whichever library was
used.

### 10.4 Correlation

The gateway assigns a correlation ID to every request that lacks one, and it
propagates through every service, log line, message and trace. This is what
makes a production incident diagnosable.

It ships in `Common.Web` as one extension, called by both the gateway and every
service (§4.2), above everything that logs — a log line written before it has no
correlation ID.

**One thing sits above it: `UseExceptionHandler`.** That is deliberate, and it
is why the ID is written onto the *request* below rather than only into the log
scope. An exception unwinding past this middleware disposes the
`LogContext` scope before the handler catches it, so the scope is gone by the
time §10.5 builds the response — but `Request.Headers` is not, which is exactly
where `CustomizeProblemDetails` reads it from. The correlation ID reaches the
client on the one response where the log scope cannot carry it:

```csharp
namespace Common.Web;

public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
{
    // Resolved once, outside the delegate: this runs on every request, and
    // ILoggerFactory is a singleton whose per-request lookup buys nothing.
    var logger = app.ApplicationServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Common.Web.CorrelationId");

    return app.Use(async (context, next) =>
    {
        const string Header = "X-Correlation-Id";

        // FirstOrDefault on absent headers is null; an empty header value is
        // not, and would otherwise become a correlation ID of "".
        var supplied = context.Request.Headers[Header].FirstOrDefault();

        var correlationId = string.IsNullOrWhiteSpace(supplied)
            ? Activity.Current?.TraceId.ToString() ?? Guid.CreateVersion7().ToString()
            : supplied;

        context.Request.Headers[Header] = correlationId;
        context.Response.Headers[Header] = correlationId;

        // BeginScope, the Microsoft.Extensions.Logging primitive — not
        // Serilog's LogContext. OpenTelemetry is the whole logging stack here
        // (Appendix B), and it reads scopes; §13.3's LoggingBehavior uses the
        // same call for the same reason.
        using (logger.BeginScope(new Dictionary<string, object>
               {
                   ["CorrelationId"] = correlationId
               }))
            await next();
    });
}
```

### 10.5 Error responses

Every service returns RFC 9457 `application/problem+json`, so clients handle one
error shape regardless of which service produced it.

```csharp
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Instance =
            $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";

        context.ProblemDetails.Extensions["correlationId"] =
            context.HttpContext.Request.Headers["X-Correlation-Id"].FirstOrDefault();

        context.ProblemDetails.Extensions["traceId"] =
            Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
    };
});
```

| Situation | Status | Notes |
|---|---|---|
| Validation failed | 400 | `errors` extension, field-keyed |
| No or invalid token | 401 | |
| Authenticated but not permitted | 403 | Do not leak whether the resource exists |
| Aggregate not found | 404 | |
| Concurrency conflict, no precondition sent | 409 | From `DbUpdateConcurrencyException` |
| `If-Match` / `If-Unmodified-Since` failed | **412** | The client *did* send a precondition and it did not hold. Distinguishing this from 409 tells the client whether retrying with a fresh ETag is the fix |
| Domain rule violated | 422 | The request was well-formed but not allowed |
| Downstream dependency unavailable | 503 | With `Retry-After` where known. Never 500 — the fault is not in this service |
| Rate limited | 429 | With `Retry-After` |
| Unhandled | 500 | **Never** include the exception message or stack trace |

#### `Error`, and why its code is a closed vocabulary

The table maps *situations* to statuses. `Error` is what a handler returns to
say which situation it is, and it is three fields rather than a string because
two of them have consumers that are not the client:

```csharp
namespace Common.Application;

/// <summary>
/// A failure a handler chose to return, as opposed to one it threw. Code is a
/// stable identifier, Description is for a person, and Type selects the status.
/// </summary>
public sealed record Error(string Code, string Description, ErrorType Type)
{
    public static Error NotFound(string code, string description) =>
        new(code, description, ErrorType.NotFound);

    public static Error Rule(string code, string description) =>
        new(code, description, ErrorType.Rule);

    public static Error Unavailable(string code, string description) =>
        new(code, description, ErrorType.Unavailable);
}

/// <summary>
/// Three cases, not four. There is deliberately no Validation member: a
/// malformed request never reaches a handler, so no handler can return one.
/// </summary>
public enum ErrorType { NotFound, Rule, Unavailable }
```

```csharp
namespace Ordering.Application.Orders;

/// <summary>
/// The catalogue. Every Error the service can return is constructed here and
/// nowhere else — which is what makes Code a bounded set rather than whatever
/// string the nearest handler happened to type.
/// </summary>
public static class OrderErrors
{
    public static readonly Error NotFound =
        Error.NotFound("order.not_found", "No order with that id.");

    public static readonly Error AlreadyShipped =
        Error.Rule("order.already_shipped", "A shipped order cannot be cancelled.");

    // 422, not 400: the request was well-formed and the validator passed it.
    // The products are unpriceable, which is a fact about this service's state
    // and not something the caller phrased wrongly.
    public static Error ProductsUnavailable(IReadOnlyList<ProductId> missing) =>
        Error.Rule("order.products_unavailable",
            $"No price for {missing.Count} product(s).");
}
```

**`Code` is a metric dimension, so it has to be closed.** §9.8 tags
`command.domain_rejected` with it, and a tag whose value set is unbounded is a
cardinality incident waiting for the first handler that interpolates an id into
a code. Constructing every `Error` in a static catalogue is the same discipline
`CancellationReasons` (§11.4) gets from a `FrozenDictionary`, applied where a
dictionary would be overkill: the values are `static readonly` fields, so the
set is enumerable by reflection and reviewable by reading one file.

Note what is *not* in the code: no order id, no customer id, no count. Those
belong in `Description`, which is written for a person and never tagged onto an
instrument. `ProductsUnavailable` above is the shape to copy — one code, a
description that varies.

**`Type` selects the status**, in one place, so the mapping in the table above
is executed rather than remembered:

```csharp
// Result.ToHttpResult() (§11.4's endpoints call it) — the whole reason
// ErrorType exists rather than each endpoint deciding.
ErrorType.NotFound    => Results.Problem(statusCode: 404),
ErrorType.Rule        => Results.Problem(statusCode: 422),
ErrorType.Unavailable => Results.Problem(statusCode: 503),
```

**Three of the table's rows are not reachable from here, and that is the point.**
Each belongs to a mechanism that runs before or beside a handler, and giving
`Error` a member for it would put two producers on one status:

| Status | Produced by | Why not `Error` |
|---|---|---|
| 400 | `ValidationBehavior` throwing `ValidationException` (§6.3) | The `errors` extension is field-keyed, and `Error` has no field. A malformed request is rejected before any handler runs, so no handler can return one |
| 401 / 403 | The authentication and authorization middleware (§11.4) | Decided before the endpoint's delegate is entered |
| 409 / 412 | `DbUpdateConcurrencyException` and the precondition filter | A different conversation with the client — retry with a fresh ETag, rather than the request was understood and refused |

That asymmetry is why `Rule` maps to 422 rather than 409: 409 is already spoken
for by the concurrency case, and a domain refusal is not a race.

---

## 11. Identity and authorization

### 11.1 Keycloak as the identity provider

> **Decision — do not build an identity service.** See [ADR-009](#adr-009--keycloak-not-a-hand-built-identity-service).

Authentication is a solved problem with a long tail of security-critical detail:
password hashing and rotation, MFA, account recovery, session revocation, token
introspection, brute-force protection, breach detection. Implementing it is a
large amount of work that produces no business differentiation and a great deal
of liability.

Keycloak is used here because it is Apache 2.0, self-hostable, runs as a
container, and speaks standard OIDC. The realistic alternatives:

| Option | Licence | When it fits |
|---|---|---|
| **Keycloak** | Apache 2.0 | Self-hosted, no per-user cost, full control |
| Microsoft Entra ID | Commercial | Already an Azure/Microsoft 365 organisation |
| Auth0 / Okta | Commercial | Willing to pay per-user for lower operational burden |
| Duende IdentityServer | Commercial above a revenue threshold | Deep .NET integration, custom flows |
| OpenIddict | Apache 2.0 | Building the IdP in-process is genuinely required |

Because everything speaks OIDC, swapping providers changes configuration rather
than code.

### 11.2 Token flow

```mermaid
sequenceDiagram
    participant U as User
    participant W as Web app
    participant K as Keycloak
    participant G as Gateway
    participant O as Ordering

    U->>W: Sign in
    W->>K: Authorization code + PKCE
    K-->>W: Access token (JWT) + refresh token
    W->>G: GET /api/v1/orders  (Bearer)
    G->>K: JWKS (cached)
    G->>G: Validate signature, issuer, audience, expiry
    G->>O: forward + X-Correlation-Id
    O->>O: Re-validate token, evaluate policy
    O-->>G: 200
    G-->>W: 200
```

**Services re-validate the token.** The gateway validating it is not sufficient:
anything that reaches a service by another path — a misconfigured network
policy, another service, a port-forward — would otherwise be unauthenticated.
Validation is cheap; assume the network is hostile.

### 11.3 Service configuration

```csharp
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Identity:Authority"];
        options.Audience  = "commerce-api";
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            ClockSkew                = TimeSpan.FromSeconds(30),
            NameClaimType            = "preferred_username",
            RoleClaimType            = "roles"
        };
    });
```

The default `ClockSkew` is five minutes, which means a revoked or expired token
keeps working for five minutes longer than it should. Thirty seconds is enough
to absorb real clock drift between NTP-synced hosts.

### 11.4 Permission-based authorization

Role checks scattered through controllers (`[Authorize(Roles = "Admin")]`)
become unmaintainable once roles multiply. Authorize on **permissions**, and map
roles to permissions in one place.

```csharp
// This is the block in §4.2's Program.cs — one copy in the code, repeated here
// because this is where the permission model is explained. The permission
// STRINGS are the contract with Keycloak's claim mapper; the policies are how
// ASP.NET Core checks them.
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("orders:read",   p => p.RequireClaim("permission", "orders:read"))
    .AddPolicy("orders:write",  p => p.RequireClaim("permission", "orders:write"))
    .AddPolicy("orders:cancel", p => p.RequireClaim("permission", "orders:cancel"));
```

> **A policy name is a reference, and nothing checks it.**
> `RequireAuthorization("orders:cancel")` takes a string. Misspell it, or
> register the policy in a helper the host never calls, and there is no
> compiler error, no `ValidateOnBuild` failure and no startup warning — the
> endpoint throws `InvalidOperationException` the first time somebody cancels
> an order, which is to say in production, on the path that matters. The
> gateway's version of the same mistake is quieter still (§10.2): YARP drops
> the route instead of throwing.
>
> This is the `GetServices<T>()` problem in a different costume — a lookup by
> name that returns nothing and is only observed at the call site. Assert it
> the same way: enumerate the endpoint policy names from
> `EndpointDataSource` in a test and require each to resolve through
> `IAuthorizationPolicyProvider`.

> **Decision — Minimal APIs, not MVC controllers.** See
> [ADR-015](#adr-015--minimal-apis-not-mvc-controllers). The endpoint layer in this
> architecture is a thin translation from HTTP to a command or query. Controllers
> add a base class, attribute routing, model binding conventions and an action
> filter pipeline to do that, and their filter pipeline duplicates the dispatcher
> pipeline that already exists. Minimal APIs express the same thing with less
> ceremony, and endpoint groups give the same route and policy grouping.

```csharp
public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/orders")
                       .WithTags("Orders")
                       .RequireAuthorization();

        group.MapPost("/{id:guid}/cancel",
            async (Guid id, CancelOrderRequest request,
                   IDispatcher dispatcher, CancellationToken ct) =>
            {
                // Parse at the boundary, through the same method the message
                // path uses (§9.4). Binding CancellationReason straight from
                // JSON would publish the enum's member names as API surface,
                // and an unknown value would surface as a model-binding error
                // rather than a 400 naming the field.
                if (!CancellationReasons.TryParse(request.Reason, out var reason))
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["reason"] = [$"Unknown cancellation reason '{request.Reason}'."]
                    });

                var result = await dispatcher.SendAsync(
                    new CancelOrderCommand(id, reason), ct);

                return result.ToHttpResult();
            })
            .RequireAuthorization("orders:cancel")
            .WithName("CancelOrder");
    }
}
```

Endpoint classes reference Application and Domain contracts only — never
`DbContext`, a concrete repository, or the bus. That is the composition-root
rule from §4.2, and it is enforced by an architecture test rather than review.

The request type carries the **wire code**, not the enum, for the reason §9.4
gives about `CancelOrder.Reason` — and the parse is the same one, because two
parses drift and the drift only shows on whichever path is less tested:

```csharp
namespace Ordering.Application.Orders.CancelOrder;

public sealed record CancelOrderRequest(string Reason);

// Non-generic Result, not Result<Unit>: CommandConsumer constrains TCommand to
// ICommand<Result> (§9.4), and a command reachable by message must satisfy it.
// Result IS the void payload — a Unit type alongside it would be a second way
// to say the same thing, and only one of them would compile here.
public sealed record CancelOrderCommand(Guid OrderId, CancellationReason Reason)
    : ICommand<Result>;

/// <summary>
/// The one place a wire code becomes a domain enum. Both entry points call it:
/// this endpoint and the CancelOrder message mapper (§9.4). An unknown code
/// fails loudly — Enum.TryParse over the member names would silently accept
/// "CustomerRequest" as well, making the enum's spelling part of the API.
///
/// Both callers fail, differently, because their callers differ. The endpoint
/// returns 400 naming the field, because a person can fix a request. The
/// message mapper throws ContractMappingException, which §9.4's retry policy
/// ignores so the message reaches the error queue on the first attempt — a
/// sibling service sending a code we do not know is a deployment problem, and
/// no amount of backoff resolves it.
/// </summary>
public static class CancellationReasons
{
    private static readonly FrozenDictionary<string, CancellationReason> ByCode =
        new Dictionary<string, CancellationReason>(StringComparer.Ordinal)
        {
            [CancelReasons.OutOfStock]      = CancellationReason.OutOfStock,
            [CancelReasons.StockTimeout]    = CancellationReason.StockTimeout,
            [CancelReasons.PaymentDeclined] = CancellationReason.PaymentDeclined,
            [CancelReasons.PaymentTimeout]  = CancellationReason.PaymentTimeout,
            [CancelReasons.CustomerRequest] = CancellationReason.CustomerRequest
        }.ToFrozenDictionary();

    public static bool TryParse(string? code, out CancellationReason reason) =>
        ByCode.TryGetValue(code ?? "", out reason);

    // The reverse, for anything that has the enum and needs the vocabulary
    // back — §13.3's metric tag, and the saga when it re-publishes. Built by
    // inverting the map above rather than written twice: a second table is a
    // second thing to forget when a reason is added.
    private static readonly FrozenDictionary<CancellationReason, string> ToCodeMap =
        ByCode.ToFrozenDictionary(p => p.Value, p => p.Key);

    public static string ToCode(CancellationReason reason) => ToCodeMap[reason];
}
```

Coarse permission checks live at the endpoint. **Resource-level checks — "is
this the customer's own order?" — belong in the handler**, where the data is
available:

```csharp
internal sealed class CancelOrderHandler(
    IOrderRepository orders, ICurrentUser currentUser, TimeProvider clock)
    : ICommandHandler<CancelOrderCommand, Result>
{
    public async Task<Result> HandleAsync(
        CancelOrderCommand command, CancellationToken ct)
    {
        var order = await orders.GetAsync(new OrderId(command.OrderId), ct);
        if (order is null)
            return Result.Failure(OrderErrors.NotFound);

        // Deliberately a 404, not a 403 — a 403 confirms the order exists.
        if (currentUser.IsAuthenticated
            && order.CustomerId.Value != currentUser.Id
            && !currentUser.HasPermission("orders:admin"))
            return Result.Failure(OrderErrors.NotFound);

        // The aggregate still owns the transition — this handler decides who
        // may ask, not whether the order is in a state that permits it (§5.3).
        order.Cancel(command.Reason, clock.GetUtcNow());

        // No metric here, for the reason §6.4 gives: this runs inside the
        // transaction, and a cancellation counted before the commit is counted
        // again by an execution-strategy replay. It is recorded by the
        // projection, from OrderCancelledDomainEvent (§13.3).

        // No SaveChangesAsync: TransactionBehavior owns the commit (§6.3).
        return Result.Success();
    }
}
```

The `IsAuthenticated` guard is not a loophole, and leaving it out is the bug.
`CancelOrderCommand` is dispatched from two places — the endpoint above and a
`CommandConsumer` (§9.4) when the saga compensates — and the second has no
caller. A message-borne cancellation is the system acting on its own decision,
already authorised at the endpoint that started the saga; checking it against
"the current user" would compare an order's owner to nobody and refuse every
compensation. Handlers reachable both ways must say which check applies to
which path.

The port and its one implementation:

```csharp
// Ordering.Application — a port, because handlers must not see HttpContext.
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid Id { get; }                      // throws if not authenticated
    bool HasPermission(string permission);
}
```

```csharp
// Ordering.Infrastructure — registered by AddOrderingInfrastructure (§4.2),
// which also calls AddHttpContextAccessor(). Scoped: it is per request.
public sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? User => accessor.HttpContext?.User;

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated == true;

    public Guid Id => Guid.Parse(
        User?.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException(
            "No authenticated caller. Guard with IsAuthenticated — a handler " +
            "reached by a consumer (§9.4) has no HttpContext."));

    // The same claim type §11.4's policies require, so an endpoint policy and
    // a resource check can never disagree about what a permission is.
    public bool HasPermission(string permission) =>
        User?.HasClaim("permission", permission) == true;
}
```

**Three places have to agree on which claim identifies a user**, and they do:
`ClaimTypes.NameIdentifier` here, in §10.3's rate-limit partition key, and in
the `TestAuthHandler` of §12.4. Keycloak's `sub` maps to it under the default
inbound claim mapping.

`NameClaimType = "preferred_username"` in §11.3 does **not** compete with this.
It sets what `ClaimsPrincipal.Identity.Name` returns — a display name, for logs
and audit lines — while `NameIdentifier` stays the stable subject identifier.
Reading `Identity.Name` as the key to a record would work in every test and
break the first time somebody changed their username.

`orders:admin` is a **claim**, not one of §11.4's registered policies. Policies
gate endpoints; a resource check asks a question the endpoint could not have
answered before loading the data. The permission strings come from the same
vocabulary either way.

### 11.5 Service-to-service authentication

A host that calls a peer authenticates with the OAuth 2.0 client credentials
grant, holding its own client ID and secret with a narrow scope. Never reuse a
user's token for a background operation — it expires, it carries the wrong
permissions, and it makes the audit trail lie about who did what.

**In this blueprint that is exactly one host: the BFF** (§9.7). The gateway
forwards the caller's token unchanged rather than exchanging it for one of its
own; Ordering and Catalog exchange events over the broker and read local
projections (§6.4, ADR-002), so neither ever presents itself to the other.

That is not a simplification for the sake of the example — it is what ADR-002
and ADR-017 add up to. The mechanism below is worth understanding precisely
because the number of hosts using it is the number of synchronous couplings in
the platform, and both are meant to stay at one. "Every host gets the full
identity block" is the natural-looking generalisation and the wrong one; so is
reading this section and concluding the services talk to each other.

Mechanically this is a `DelegatingHandler` attached to every outbound client
(§9.7), so no call site has to remember it:

```csharp
public sealed class ClientCredentialsHandler(
    ITokenCache tokens, IOptions<ServiceIdentityOptions> identity)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        // Cached until shortly before expiry; one token fetch serves many calls.
        var token = await tokens.GetAsync(identity.Value.Scope, ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await base.SendAsync(request, ct);
    }
}
```

It must sit **inside** the resilience pipeline. Registering it outside means a
retry reuses the token from the first attempt, which defeats the main reason a
retry would help after a 401 — see the ordering in §9.7.

#### The scope has to become an audience

`ServiceIdentityOptions.Scope` is `commerce-api` (§14.1, §15.4) and §11.3
validates `Audience = "commerce-api"`. Those are **not** the same claim, and
nothing so far makes one imply the other: a client-credentials token carries
`scope: commerce-api` and, by default, an `aud` of `account`. Catalog would
reject the platform's only permitted synchronous hop, at the one moment there is
no user to blame it on.

The realm has to close the gap. In Keycloak the client scope `commerce-api`
needs an **audience mapper** adding `commerce-api` to `aud`, and the BFF's
service-account client needs that scope assigned as default:

| Realm object | Setting | Why |
|---|---|---|
| Client scope `commerce-api` | Mapper of type *Audience*, included audience `commerce-api`, added to the access token | Puts the value in `aud` that every API validates |
| Client `web-bff` | Service accounts enabled, `commerce-api` a **default** client scope | Client-credentials tokens request no scope explicitly; a client scope left optional is silently absent |
| Clients for browser flows | Same scope, so a user's token validates at the same services | One audience for the whole platform (§11.3) — per-service audiences are a later split, not a v1 one |

This is realm configuration, not code, which is exactly why it earns a test
rather than a paragraph — nothing in the solution compiles differently when the
audience mapper is missing.

It is also the **one** suite that runs a real Keycloak. §12.4's fixture
deliberately does the opposite: it points at an unreachable authority and swaps
the JWT scheme for `TestAuthHandler`, because the several hundred tests that
merely need *a* principal should not pay for an identity provider or fail when
one is slow. That fixture therefore cannot see this defect at all — it never
validates a token Keycloak issued. So this suite gets its own fixture, starting
the Keycloak container with the realm import from §14.1 and the real JWT scheme:

```csharp
[Fact]
public async Task Bff_client_credentials_token_is_accepted_by_a_service()
{
    var token = await Realm.ClientCredentialsAsync("web-bff");

    token.Audiences().ShouldContain("commerce-api");
    (await Catalog.GetAsync("/products/1", token)).StatusCode
        .ShouldBe(HttpStatusCode.OK);
}

[Fact]
public async Task A_client_without_the_scope_is_rejected()
{
    // The negative half matters more: a mapper that adds the audience to every
    // token would pass the test above and grant the platform to any client the
    // realm happens to hold.
    var token = await Realm.ClientCredentialsAsync("unrelated-client");

    (await Catalog.GetAsync("/products/1", token)).StatusCode
        .ShouldBe(HttpStatusCode.Unauthorized);
}
```

### 11.6 Secrets

| Environment | Mechanism |
|---|---|
| Local development | .NET user secrets — never `appsettings.json` |
| CI | Pipeline secret store, masked in logs |
| Kubernetes | External Secrets Operator syncing from Vault / Azure Key Vault |

Enable secret scanning in CI. A secret committed to git is compromised even
after the commit is reverted, and the rotation must happen regardless.

### 11.7 Extension points — multi-tenancy, personal data, compliance

None of these are built in the baseline. All three are expensive to retrofit
into a design that ignored them, and cheap to leave a seam for. This section
defines the seams and the rules that apply *if* the extension is enabled.

| Extension | Seam | Baseline rule |
|---|---|---|
| **Multi-tenancy** | `TenantId` on the integration event metadata envelope; a logging enrichment hook; an ambient `ITenantContext` resolved from token claims | No tenant is required. **If** tenancy is enabled, `TenantId` must appear in every Redis key — `{service}:cache:{tenant}:...`, after the keyspace segment rather than before it, so §8.1's eviction split still reads off position two (§8.3) — plus every query predicate and every log scope |
| **Personal data erasure** | `PersonalDataDeleteRequestedV1` in `Common.Contracts` | Not published in the baseline. The consumer shape is defined below so services are built ready for it |
| **PCI / HIPAA / SOC 2** | — | Decide before handling regulated data, not after. Record the constraints as an ADR |

#### Personal data erasure under database-per-service

GDPR Article 17 erasure is genuinely hard here: there is no central customer
table to delete a row from. Personal data is distributed across every service
that stores it, and no service knows what the others hold.

The wrong answer is a central "user data service" that owns the deletion — it
would need read access to every database, which destroys the ownership model.

The right answer is choreography. The request is broadcast; each service deletes
what it owns and reports back:

```mermaid
sequenceDiagram
    participant P as Privacy service
    participant B as Bus
    participant O as Ordering
    participant N as Notifications
    participant S as Shipping

    P->>B: PersonalDataDeleteRequestedV1 {SubjectId, RequestId}
    B->>O: consume (inbox)
    B->>N: consume (inbox)
    B->>S: consume (inbox)
    O->>O: anonymise Orders.CustomerId, purge address
    N->>N: delete NotificationLog rows
    S->>S: anonymise Shipment recipient
    O->>B: PersonalDataDeleteCompletedV1 {RequestId, "ordering"}
    N->>B: PersonalDataDeleteCompletedV1 {RequestId, "notifications"}
    S->>B: PersonalDataDeleteCompletedV1 {RequestId, "shipping"}
    P->>P: all services reported → close request
```

Rules for each service's consumer:

- **Delete or anonymise, per record.** An order that must be retained for tax
  law is anonymised — customer identifiers replaced, address cleared, the
  financial record preserved. A notification log row is deleted outright. The
  owning service makes that call; nobody else can.
- **Write an audit record** of what was erased and when. That record itself
  contains no personal data — a subject ID hash, a timestamp, a count.
- **Idempotent.** The message is delivered at least once, and a second erasure
  of already-erased data must succeed silently.
- **Report completion.** The privacy service tracks which services have
  responded and escalates on timeout. Silence is not success.

> **The Privacy service is part of the extension, not of the baseline.** It
> appears in no bounded-context table (§3.2), no solution tree (§4.1) and no PR
> (Appendix C), and enabling erasure means adding it — a context owning the
> request aggregate, the expected-responder set as configuration, and a
> completion SLO. Naming it in the diagram is what makes the seam checkable:
> the alternative is discovering at enablement time that nothing was ever
> designed to close the request. A service missing from the responder list is
> the failure mode to design against, because it fails as *silence*, and
> silence is the one outcome choreography cannot distinguish from success.

The one thing that must be designed for from the start: **integration events
carry identifiers, not personal data**. An `OrderConfirmed` carrying a customer's
name and email means erasure must also purge the broker, every consumer's inbox,
and any log that recorded the payload — which is not practically possible.
Carrying `CustomerId` keeps the personal data inside the service that owns it,
which is the only place it can be reliably erased.

---

## 12. Test strategy and TDD

### 12.1 The pyramid

```
      ╱╲            Cross-service — a few
     ╱  ╲           A whole saga, all services in containers. Seconds.
    ╱────╲
   ╱      ╲         Integration — tens per service
  ╱        ╲        Real SQL Server, Redis, RabbitMQ via Testcontainers.
 ╱──────────╲
╱            ╲      Unit — hundreds per service
──────────────      Domain logic. No I/O. Milliseconds.
```

| Level | Scope | Dependencies | Target time | Count | Lives in |
|---|---|---|---|---|---|
| Domain unit | One aggregate or value object | None — no mocks either | < 1 ms | Hundreds | `*.Domain.Tests` |
| Application | One handler end to end | Real DB and Redis (containers), fakes for other services | < 500 ms | Tens | `*.Application.Tests` |
| API contract | HTTP in, HTTP out | `WebApplicationFactory` + containers | < 1 s | Tens | `*.Api.Tests` |
| Saga | One whole saga, coordination only | MassTransit in-memory harness — no infrastructure | < 100 ms | A few | `*.Application.Tests` |
| Contract | Every published contract against the rules it must obey | Both assemblies, reflection only | < 1 s | One suite | `Platform.IntegrationTests` |

**Neither is there an "all services in containers" level, nor an E2E one.** Both
are rows that get written into a strategy and never built — the second needs a
client the backend does not own and data that survives between runs; the first
needs every service's image, database and broker started together, which is a
local Compose environment wearing a test-runner costume and fails in ways nobody
can attribute.

What they would actually catch splits cleanly in two, and both halves are
cheaper elsewhere. **Saga coordination** — did the right command go out, in the
right order, after the right event — is exercised by the in-memory harness in
§12.5, in milliseconds. **Contract compatibility** — does the message one
service publishes still mean what its consumers expect — is a reflection test
over the contract assembly, and it is the one thing genuinely between services,
which is why `Platform.IntegrationTests` exists and holds nothing else.

What no level above covers is whether the *deployed* system responds under load
and against real infrastructure. That is a **k6 or NBomber run against
staging** (§13.7), asserting the SLOs — not a test suite, and §15.1 stages it as
what it is. Naming it accurately is the point: a load run that is honestly a
load run gets maintained; an "E2E suite" that is actually three fragile scripts
gets disabled after the second flake and stays green forever.

Every row above names a project **and has an example in this section**. Both
halves are the rule: a level with no home is a level nobody writes, and a level
whose home is empty is one nobody notices is missing.

### 12.2 The TDD cycle applied

Red, green, refactor — with a worked example, because the discipline is easier
to describe than to follow.

**Requirement:** an order cannot be cancelled once it has shipped.

**Red** — write the test first. It must fail for the right reason.

```csharp
public class OrderCancellationTests
{
    [Fact]
    public void Cannot_cancel_an_order_that_has_shipped()
    {
        var order = OrderBuilder.Shipped();

        var act = () => order.Cancel(CancellationReason.CustomerRequest, DateTimeOffset.UtcNow);

        act.ShouldThrow<DomainException>()
           .Message.ShouldContain("cannot be cancelled");
    }
}
```

Run it. It fails because `Cancel` does not check status yet — not because
`OrderBuilder.Shipped()` does not compile. A test failing to compile is not a
red test; make it compile first, then watch it fail.

**Green** — the minimum change that passes:

```csharp
public void Cancel(CancellationReason reason, DateTimeOffset now)
{
    if (Status is OrderStatus.Shipped or OrderStatus.Delivered)
        throw new DomainException($"A {Status} order cannot be cancelled.");

    Status = OrderStatus.Cancelled;
    Raise(new OrderCancelledDomainEvent(Id, CustomerId, reason, now));
}
```

**Refactor** — with the test green, improve. Add the guidance about returns, and
add the idempotency case as its own test first:

```csharp
[Fact]
public void Cancelling_twice_is_idempotent()
{
    var order = OrderBuilder.AwaitingPayment();
    order.Cancel(CancellationReason.CustomerRequest, Now);

    order.Cancel(CancellationReason.CustomerRequest, Now);

    order.Status.ShouldBe(OrderStatus.Cancelled);
    order.DomainEvents.OfType<OrderCancelledDomainEvent>().Count().ShouldBe(1);
}
```

Why this order matters: writing the test first forces you to design the API from
the caller's perspective before the implementation biases you, and it proves the
test can fail. A test written after the code has never been observed failing,
and a test that cannot fail is not a test.

### 12.3 Domain tests — no mocks

The domain has no dependencies, so its tests need no test doubles. This is the
payoff for the dependency rule in section 4.2.

```csharp
public class OrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Placing_an_order_totals_all_lines()
    {
        var order = Order.Place(
            CustomerId.New(),
            AddressBuilder.Valid(),
            [
                (ProductId.New(), 2, Money.Of(10.00m, "EUR")),
                (ProductId.New(), 1, Money.Of(5.50m,  "EUR"))
            ],
            "EUR", Now);

        order.Total.ShouldBe(Money.Of(25.50m, "EUR"));
        order.Status.ShouldBe(OrderStatus.AwaitingStock);
    }

    [Fact]
    public void Placing_an_order_raises_OrderPlacedDomainEvent()
    {
        var order = OrderBuilder.Placed();

        var placed = order.DomainEvents.OfType<OrderPlacedDomainEvent>().ShouldHaveSingleItem();
        placed.OrderId.ShouldBe(order.Id);
        placed.Total.ShouldBe(order.Total);
    }

    [Fact]
    public void An_order_must_have_at_least_one_line()
    {
        var act = () => Order.Place(CustomerId.New(), AddressBuilder.Valid(),
                                    [], "EUR", Now);

        act.ShouldThrow<DomainException>();
    }

    [Fact]
    public void Adding_the_same_product_twice_merges_the_lines()
    {
        var product = ProductId.New();

        var order = Order.Place(CustomerId.New(), AddressBuilder.Valid(),
            [ (product, 2, Money.Of(10m, "EUR")),
              (product, 3, Money.Of(10m, "EUR")) ],
            "EUR", Now);

        var line = order.Lines.ShouldHaveSingleItem();
        line.Quantity.ShouldBe(5);
    }

    [Fact]
    public void All_lines_must_share_the_order_currency()
    {
        var act = () => Order.Place(CustomerId.New(), AddressBuilder.Valid(),
            [ (ProductId.New(), 1, Money.Of(10m, "USD")) ],
            "EUR", Now);

        act.ShouldThrow<DomainException>()
           .Message.ShouldContain("currency");
    }
}
```

Test data uses builders with sensible defaults, so each test states only what it
cares about:

```csharp
internal static class OrderBuilder
{
    private static readonly DateTimeOffset DefaultNow =
        new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    // The customer is a parameter because ownership tests have to name it —
    // §12.4's 404-not-403 case turns entirely on who owns the order.
    public static Order Placed(int lines = 1, string currency = "EUR",
                               CustomerId? customer = null) =>
        Order.Place(
            customer ?? CustomerId.New(),
            AddressBuilder.Valid(),
            Enumerable.Range(0, lines)
                      .Select(_ => (ProductId.New(), 1, Money.Of(10m, currency))),
            currency, DefaultNow);

    public static Order AwaitingPayment()
    {
        var order = Placed();
        order.ConfirmStock(DefaultNow);
        return order;
    }

    public static Order Shipped()
    {
        var order = AwaitingPayment();
        order.ConfirmPayment(PaymentReference.From("test-ref"), DefaultNow);
        order.MarkShipped(TrackingNumber.From("TRK1"), DefaultNow);
        return order;
    }
}
```

### 12.4 Application tests — real infrastructure

> **Decision — integration tests use real SQL Server and Redis, not in-memory
> substitutes.** See [ADR-010](#adr-010--testcontainers-not-in-memory-providers).

The EF Core in-memory provider does not enforce foreign keys, does not
implement `rowversion` concurrency, and translates LINQ differently from the SQL
Server provider. A test suite green against it will still fail in production.
Testcontainers starts a real SQL Server in a few seconds; the fidelity is worth
it.

```csharp
public sealed class ServiceFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    // Two Redis containers, matching the production split in §8.1 — otherwise
    // the tests cannot catch a coordination key written to the evicting instance.
    private readonly RedisContainer _cache = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .WithCommand("--maxmemory-policy", "allkeys-lru")
        .Build();

    private readonly RedisContainer _coordination = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .WithCommand("--maxmemory-policy", "noeviction")
        .Build();

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder()
        .WithImage("rabbitmq:4-management-alpine")
        .Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    private Respawner _respawner = null!;

    // ValueTask, not Task: xUnit v3 redefined IAsyncLifetime (see below).
    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_sql.StartAsync(), _cache.StartAsync(),
                           _coordination.StartAsync(), _rabbit.StartAsync());

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b
                .UseSetting("ConnectionStrings:Ordering", _sql.GetConnectionString())
                .UseSetting("ConnectionStrings:RedisCache", _cache.GetConnectionString())
                .UseSetting("ConnectionStrings:RedisCoordination", _coordination.GetConnectionString())
                .UseSetting("ConnectionStrings:RabbitMq", _rabbit.GetConnectionString())
                // The host is the real one, so ValidateOnStart runs here too
                // (§15.4). Without this the fixture throws
                // OptionsValidationException out of InitializeAsync and every
                // test in the suite fails before it starts. Deliberately fake
                // and deliberately unreachable — .invalid never resolves, so a
                // test that accidentally dials the authority fails loudly
                // rather than reaching a real identity provider.
                //
                // No Identity:Client here. Ordering does not call a peer, so it
                // never binds ServiceIdentityOptions (§9.7) and supplying one
                // would be config the host ignores — which is how a fixture
                // ends up disagreeing with the deployment about what a service
                // requires, in the direction that hides a missing secret.
                .UseSetting("Identity:Authority", "https://identity.invalid/realms/test")
                .ConfigureServices(services =>
                {
                    // Replace the JWT scheme rather than configuring it: the
                    // endpoints under test are behind RequireAuthorization
                    // (§11.4), and the alternative is either 401 on every call
                    // or a fixture that fetches OIDC metadata over the network.
                    // TestAuthHandler issues the principal each test asks for,
                    // including its permission claims, so the authorization
                    // policies are exercised for real.
                    services.Configure<AuthenticationOptions>(o =>
                    {
                        o.DefaultAuthenticateScheme = TestAuthHandler.Scheme;
                        o.DefaultChallengeScheme    = TestAuthHandler.Scheme;
                    });
                    services.AddAuthentication()
                            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                                TestAuthHandler.Scheme, _ => { });

                    // Remove ONLY the outbox dispatcher, not every hosted
                    // service: MassTransit registers its bus as one, so
                    // RemoveAll<IHostedService>() would stop the broker from
                    // starting and silently disable every consumption test.
                    //
                    // The dispatcher polls every 500 ms; left running it drains
                    // outbox rows underneath assertions about them. Tests that
                    // want it call fixture.ProcessOutboxBatchAsync() explicitly.
                    var hosted = services.Single(
                        d => d.ServiceType == typeof(IHostedService)
                          && d.ImplementationType == typeof(OutboxDispatcher));
                    services.Remove(hosted);

                    // Still resolvable directly, so tests can drive one pass.
                    services.AddSingleton<OutboxDispatcher>();
                }));

        // Tests deliberately collapse the two database identities of §7.1 —
        // the container's sa login holds both DML and DDL. Production keeps
        // them separate, and migrations run as a job, never from a host (ADR-007).
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OrderingDbContext>()
                   .Database.MigrateAsync();

        // Reset between tests by truncating, which is far faster than
        // recreating the schema or wrapping every test in a rolled-back
        // transaction (which would hide transaction-related bugs).
        _respawner = await Respawner.CreateAsync(_sql.GetConnectionString(),
            new RespawnerOptions { SchemasToInclude = ["ordering"] });
    }

    public Task ResetAsync() => _respawner.ResetAsync(_sql.GetConnectionString());

    /// <summary>Runs exactly one claim-and-deliver pass. No timers, no waiting.</summary>
    public Task<int> ProcessOutboxBatchAsync(CancellationToken ct = default)
        => Factory.Services.GetRequiredService<OutboxDispatcher>().ProcessBatchAsync(ct);

    public IDbConnection CreateConnection() => new SqlConnection(_sql.GetConnectionString());

    /// <summary>
    /// Seeds the price projection (§6.6). Required before any PlaceOrder test:
    /// the handler reads prices locally, so an unseeded projection makes every
    /// order fail ProductsUnavailable rather than erroring visibly.
    /// </summary>
    public async Task SeedPriceAsync(Guid productId, decimal amount, string currency = "EUR")
    {
        using var connection = CreateConnection();
        await connection.ExecuteAsync(
            """
            MERGE ordering.ProductPrices AS t
            USING (SELECT @productId AS ProductId, @currency AS Currency) AS s
               ON t.ProductId = s.ProductId AND t.Currency = s.Currency
            WHEN NOT MATCHED THEN
                INSERT (ProductId, Currency, Amount, IsAvailable, UpdatedAt)
                VALUES (@productId, @currency, @amount, 1, SYSDATETIMEOFFSET())
            WHEN MATCHED THEN
                UPDATE SET Amount = @amount, IsAvailable = 1, UpdatedAt = SYSDATETIMEOFFSET();
            """,
            new { productId, amount, currency });
    }

    /// <summary>
    /// Persists a real aggregate through the DbContext, so the row satisfies
    /// every invariant §5 enforces. A raw INSERT drifts from the aggregate the
    /// first time it gains a column, and drifts silently.
    /// </summary>
    public async Task<Guid> SeedOrderAsync(Guid customerId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        var order = OrderBuilder.Placed(customer: new CustomerId(customerId));
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order.Id.Value;
    }

    public async Task<IReadOnlyList<OutboxMessage>> OutboxAsync()
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OrderingDbContext>()
                          .OutboxMessages.AsNoTracking().ToListAsync();
    }

    public async Task StageOutboxAsync(params OutboxMessage[] rows)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        db.OutboxMessages.AddRange(rows);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Seeds a prior attempt count through the same column the dispatcher
    /// writes. Explicit rather than hidden in a builder, so no state carries
    /// between tests (§12.8).
    /// </summary>
    public async Task SetOutboxAttemptsAsync(Guid messageId, int attempts)
    {
        using var connection = CreateConnection();
        await connection.ExecuteAsync(
            "UPDATE ordering.OutboxMessages SET Attempts = @attempts WHERE MessageId = @messageId;",
            new { attempts, messageId });
    }

    /// <summary>
    /// Clears retry backoff leases so the next pass is gated only by the
    /// attempt cap. Lets a test distinguish "backed off" from "abandoned"
    /// without sleeping.
    /// </summary>
    public async Task ExpireOutboxLeasesAsync()
    {
        using var connection = CreateConnection();
        await connection.ExecuteAsync(
            "UPDATE ordering.OutboxMessages SET LockedUntil = NULL WHERE ProcessedAt IS NULL;");
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Task.WhenAll(_sql.DisposeAsync().AsTask(),
                           _cache.DisposeAsync().AsTask(),
                           _coordination.DisposeAsync().AsTask(),
                           _rabbit.DisposeAsync().AsTask());
    }
}
```

> **`IAsyncLifetime` returns `ValueTask` in xUnit v3.** In v2 both members
> returned `Task`; v3 changed `InitializeAsync` to `ValueTask` and derives the
> interface from `IAsyncDisposable`, so `DisposeAsync` returns `ValueTask` too.
> The v2 shape does not implement the v3 interface, and the compiler's message
> points at the class rather than at the version.
>
> The wider point belongs in Appendix B, not here. That register pins exact
> versions because four dependencies changed licence in two years — but a pin is
> a claim about an API as well as a licence. Pinning a major you have not
> compiled against buys the licence guarantee and none of the correctness.

The test scheme itself. Tests state who they are in headers, so authorization
runs against a real principal rather than being switched off:

```csharp
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme         = "Test";
    public const string UserHeader     = "X-Test-User";
    public const string PermissionsHeader = "X-Test-Permissions";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // No header means anonymous, not "authenticated as nobody" — otherwise
        // every 401 test silently passes.
        if (!Request.Headers.TryGetValue(UserHeader, out var userId))
            return Task.FromResult(AuthenticateResult.NoResult());

        List<Claim> claims = [new(ClaimTypes.NameIdentifier, userId.ToString())];

        // The same claim type §11.4's policies require. A test that grants
        // itself "orders:cancel" is exercising the policy, not bypassing it.
        if (Request.Headers.TryGetValue(PermissionsHeader, out var granted))
            claims.AddRange(granted.ToString()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => new Claim("permission", p)));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
        return Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme)));
    }
}
```

> **Do not give the test principal every permission.** A fixture that hands out
> a blanket claim set makes the §11.4 policies untestable and, worse, makes them
> *look* tested — the endpoints are reached, the assertions pass, and the one
> behaviour nobody ever exercises is the refusal. Grant per test exactly what
> that test's user should have, and keep at least one test per policy that
> grants nothing and expects the rejection.

```csharp
[Collection(nameof(IntegrationCollection))]
public class PlaceOrderHandlerTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync();

        // Respawn truncates the price projection, and the write path reads
        // prices locally (§6.4). Seed here rather than per test: an unseeded
        // projection fails a PlaceOrder with ProductsUnavailable, which reads
        // as a domain assertion failing rather than missing fixture data.
        await fixture.SeedPriceAsync(SeedData.ProductId, 12.50m);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Placing_an_order_persists_it_and_writes_an_outbox_message()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var dispatcher  = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var db          = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        var result = await dispatcher.SendAsync(new PlaceOrderCommand(
            CommandId:       Guid.CreateVersion7(),
            CustomerId:      Guid.CreateVersion7(),
            Items:           [ new PlaceOrderItem(SeedData.ProductId, 2) ],
            ShippingAddress: AddressBuilder.ValidDto(),
            Currency:        "EUR"));

        result.IsSuccess.ShouldBeTrue();

        var order = await db.Orders.SingleAsync(o => o.Id == new OrderId(result.Value));
        order.Status.ShouldBe(OrderStatus.AwaitingStock);
        order.Lines.ShouldHaveSingleItem().Quantity.ShouldBe(2);

        // The outbox rows are the real assertion: they prove the reactions are
        // staged atomically with the state change, and that nothing has run yet.
        // Only meaningful because the fixture removed the dispatcher — otherwise
        // this races a background service that drains these rows twice a second.
        var outbox = await db.OutboxMessages.ToListAsync();
        outbox.ShouldAllBe(m => m.ProcessedAt == null);

        // Broker lane carries the CONTRACT type (§9.3 allow-list)...
        outbox.ShouldContain(m => m.Lane == OutboxLane.Broker
                               && m.MessageType.Contains(nameof(V1.OrderPlaced)));

        // ...and the Local lane carries the DOMAIN type (§7.5). Distinct names
        // are what make this an assertion about which type is on which lane,
        // rather than merely that both lanes got a row.
        outbox.ShouldContain(m => m.Lane == OutboxLane.Local
                               && m.MessageType.Contains(nameof(OrderPlacedDomainEvent)));

        // The domain type must never reach the broker — that is the leak §9.3
        // exists to prevent, and it is only checkable because the names differ.
        outbox.ShouldNotContain(m => m.Lane == OutboxLane.Broker
                                  && m.MessageType.Contains(nameof(OrderPlacedDomainEvent)));
    }

    [Fact]
    public async Task The_same_command_id_is_processed_once()
    {
        var commandId = Guid.CreateVersion7();
        var command   = CommandBuilder.PlaceOrder() with { CommandId = commandId };

        using var scope = fixture.Factory.Services.CreateScope();
        var dispatcher  = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        var first  = await dispatcher.SendAsync(command);
        var second = await dispatcher.SendAsync(command);

        second.Value.ShouldBe(first.Value);

        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        (await db.Orders.CountAsync()).ShouldBe(1);
    }
}
```

The dispatcher gets its own tests, driven explicitly rather than by waiting on a
timer. These are the ones that cover the behaviour §13.6 alerts on — per-row
isolation and attempt accounting — and neither is observable from a test that
lets the background service run:

```csharp
[Collection(nameof(IntegrationCollection))]
public class OutboxDispatcherTests(ServiceFixture fixture) : IAsyncLifetime
{
    public ValueTask InitializeAsync() => new(fixture.ResetAsync());
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_failing_row_does_not_block_healthy_rows()
    {
        await fixture.StageOutboxAsync(
            Poison.Row(fixture.MessageTypes),          // its handler always throws
            Healthy.Row(fixture.MessageTypes), Healthy.Row(fixture.MessageTypes));

        await fixture.ProcessOutboxBatchAsync();

        var rows = await fixture.OutboxAsync();

        rows.Count(r => r.ProcessedAt is not null).ShouldBe(2);

        var poison = rows.Single(r => r.ProcessedAt is null);
        poison.Attempts.ShouldBe(1);
        poison.LastError.ShouldNotBeNullOrEmpty();
        poison.LockedUntil.ShouldNotBeNull();     // backed off, not abandoned
    }

    [Fact]
    public async Task A_row_stops_being_claimed_at_the_attempt_cap()
    {
        var poison = Poison.Row(fixture.MessageTypes);
        await fixture.StageOutboxAsync(poison);
        await fixture.SetOutboxAttemptsAsync(poison.MessageId, 9);

        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(0);   // 9 → 10

        // Clear the backoff lease, so the second pass is blocked by the
        // attempt cap and nothing else. Without this the test would pass
        // even if the cap were removed entirely.
        await fixture.ExpireOutboxLeasesAsync();

        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(0);

        var row = (await fixture.OutboxAsync()).Single();
        row.Attempts.ShouldBe(10);                // not 11 — never re-claimed
        row.ProcessedAt.ShouldBeNull();           // visible to the §13.6 alert
    }

    [Fact]
    public async Task A_local_row_with_no_registered_handler_fails_loudly()
    {
        await fixture.StageOutboxAsync(LocalRowFor<UnhandledEvent>(fixture.MessageTypes));

        await fixture.ProcessOutboxBatchAsync();

        var row = (await fixture.OutboxAsync()).Single();
        row.ProcessedAt.ShouldBeNull();           // NOT silently completed
        row.LastError.ShouldContain("IProjectionHandler");
    }
}
```

The third test is the one worth keeping forever. It asserts the failure mode
that would otherwise be invisible: a projection that never runs while every
dashboard stays green.

Contract messages come from a builder rather than inline object initialisers.
`required` members make partial construction a compile error, so every test
would otherwise repeat eight assignments to vary one:

```csharp
internal static class Contracts
{
    public static V1.OrderPlaced OrderPlaced(
        Guid orderId, decimal total = 25.00m, string currency = "EUR") => new()
    {
        MessageId     = Guid.CreateVersion7(),
        CorrelationId = orderId,
        OccurredAt    = TestClock.Now,
        OrderId       = orderId,
        CustomerId    = Guid.CreateVersion7(),
        TotalAmount   = total,
        Currency      = currency,
        Lines         = [new V1.PlacedLine(SeedData.ProductId, 1, total)]
    };
}
```

The builders those tests use are ordinary factories over `OutboxMessage`
(Appendix D). `Poison` stages a message whose registered handler always throws;
`LocalRowFor<T>` stages a `Local` row for an event type with no handler at all:

```csharp
// The map is the real one, resolved from the fixture's provider (§9.4). A test
// double here would let a test stage a type the running host cannot resolve,
// which is the one thing these builders exist to prove does not happen.
internal static class Poison
{
    public static OutboxMessage Row(MessageTypeMap types) =>
        OutboxMessage.Stage(new AlwaysThrows(), OutboxLane.Local,
                            Guid.CreateVersion7(), TestClock.Now, types);
}

internal static class Healthy
{
    public static OutboxMessage Row(MessageTypeMap types) =>
        OutboxMessage.Stage(new NoOpEvent(), OutboxLane.Local,
                            Guid.CreateVersion7(), TestClock.Now, types);
}

internal static OutboxMessage LocalRowFor<TEvent>(MessageTypeMap types)
    where TEvent : new() =>
    OutboxMessage.Stage(new TEvent(), OutboxLane.Local,
                        Guid.CreateVersion7(), TestClock.Now, types);
```

`AlwaysThrows` has a registered `IProjectionHandler<AlwaysThrows>` that throws;
`NoOpEvent` has one that does nothing; `UnhandledEvent` has none, which is
precisely what the third test exercises. All three are `IDomainEvent`
implementations in the test assembly, so the fixture registers that assembly
before the map is built — one line beside the `TestAuthHandler` replacement:

```csharp
// §9.4. Adding, not replacing: the production assemblies stay, so a test
// cannot stage a type the real host would refuse. Without this, NameOf throws
// on the first builder call and every outbox test fails before its assertion.
services.AddSingleton(new MessageTypeSource(
    typeof(V1.OrderPlaced).Assembly,
    typeof(Order).Assembly).Add(typeof(AlwaysThrows).Assembly));
```

Two assertions belong beside these. The first is the cheapest guard on the
single-identity rule of §9.1, and it takes no fixture at all — the thing that
can regress is a pure function:

```csharp
// Same project (Ordering.Application.Tests), no [Collection] and no fixture:
// Stage touches nothing. It is not a §12.3 test either — that level is
// *.Domain.Tests, and OutboxMessage is Common.Infrastructure. A fast test does
// not have to be a domain test, and moving it to reach a container it does not
// use is how a suite acquires a minute of startup for one assertion.
[Fact]
public void Stage_takes_the_message_id_from_the_envelope()
{
    var placed = Contracts.OrderPlaced(SeedData.OrderId);

    var row = OutboxMessage.Stage(
        placed, OutboxLane.Broker, correlationId: Guid.CreateVersion7(),
        now: TestClock.Now, types: TestTypeMap);

    // Both from the envelope, not minted here — and CorrelationId in
    // particular, because a caller-supplied one is passed in and ignored for
    // an IIntegrationEvent. That argument being silently dropped is the
    // regression this test exists for.
    row.MessageId.ShouldBe(placed.MessageId);
    row.CorrelationId.ShouldBe(placed.CorrelationId);
}
```

> **There is deliberately no test asserting the transport headers here.**
> Observing what reached the broker needs an `ITestHarness`, and this fixture
> does not have one: `AddMassTransitTestHarness` (§12.5) builds a standalone
> in-memory bus, whereas `ServiceFixture` runs the real host against the real
> RabbitMQ container on purpose. Bolting a harness onto it would replace the
> bus configuration these tests exist to exercise.
>
> The remaining hop — `DeliverAsync` copying the row's ids onto
> `c.MessageId`/`c.CorrelationId` — is two lines with no branching, and it is
> covered end-to-end by §9.5's inbox tests: those dedupe on `context.MessageId`,
> which only matches a second delivery if the value on the transport is the one
> the row carried. **A test that would need the fixture to become something
> else is a test that belongs elsewhere or nowhere**, and inventing a
> `fixture.Harness` to host it is how a suite acquires infrastructure nobody
> can explain later.

The second is the `Local` lane's payload contract (§9.4). It needs no
containers, but it lives here rather than in §12.6 because the set it iterates
comes from the fixture's `MessageTypeMap` and §12.6 selects on the contracts
namespace, which no domain event is in:

```csharp
[Fact]
public void Every_stageable_domain_event_round_trips_through_the_outbox_options()
{
    // Not "every IDomainEvent": the map is the set the outbox can actually
    // carry, and a type it does not know cannot reach a payload column.
    foreach (var type in fixture.MessageTypes.StageableDomainEvents)
    {
        var sample = DomainEventSamples.Create(type);
        var json   = JsonSerializer.Serialize(sample, type, OutboxJson.Options);

        JsonSerializer.Deserialize(json, type, OutboxJson.Options)
            .ShouldBeEquivalentTo(sample, $"{type.Name} cannot survive the Local lane");
    }
}
```

`DomainEventSamples.Create` is the same deliberate obstacle `ContractSamples` is
(§12.6): a new domain event with no sample fails here instead of being skipped,
which is the failure mode of every loop over types that falls back to
`Activator.CreateInstance`.

Containers start once per test collection, not per test. Truncating with
Respawn between tests keeps them isolated at a fraction of the cost.

> **A collection is per assembly, and the fixture has to live somewhere both
> can see.** `ServiceFixture` and `TestAuthHandler` are used by
> `Ordering.Application.Tests` (handler tests) and `Ordering.Api.Tests` (the
> contract tests below), and those cannot reference each other — so both live in
> `Ordering.TestSupport` (§4.1), a library rather than a test project.
>
> The `[CollectionDefinition]` does **not** move there. xUnit resolves
> collections within an assembly, so each test project declares its own, naming
> the shared fixture type:
>
> ```csharp
> [CollectionDefinition(nameof(IntegrationCollection))]
> public sealed class IntegrationCollection : ICollectionFixture<ServiceFixture>;
> ```
>
> The consequence is worth stating rather than discovering from a slow
> pipeline: two assemblies mean two collections and therefore **two sets of
> containers** — SQL Server, both Redis instances and RabbitMQ start twice per
> run. That is the price of the pyramid's levels mapping onto separate
> projects, and it is the right trade only while the levels stay separate for a
> reason. Collapsing them into one project halves the container cost and gives
> up the ability to run the fast half alone, which is what §15.1's pipeline
> ordering depends on.

#### API contract tests

The pyramid's third level (§12.1) goes through HTTP, and it exists to cover
what the levels below it structurally cannot: the endpoint's authorization, its
status codes, and its serialisation. A handler test proves the decision; only
this proves the decision reaches the wire intact.

```csharp
[Collection(nameof(IntegrationCollection))]
public class CancelOrderEndpointTests(ServiceFixture fixture) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _client = fixture.Factory.CreateClient();
        return new(fixture.ResetAsync());
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Rejects_a_request_with_no_token()
    {
        // No X-Test-User header, so TestAuthHandler returns NoResult and the
        // challenge stands. This is the test that catches UseAuthentication
        // being dropped from the pipeline (§4.2).
        var response = await _client.PostAsJsonAsync(
            $"/v1/orders/{Guid.CreateVersion7()}/cancel",
            new CancelOrderRequest(CancelReasons.CustomerRequest));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Rejects_a_caller_holding_the_wrong_permission()
    {
        // Authenticated, but with orders:read where the endpoint wants
        // orders:cancel — the case a fixture that grants everything hides.
        var response = await SendAsAsync(
            Guid.CreateVersion7(), "orders:read", Guid.CreateVersion7());

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Hides_another_customers_order_behind_a_404()
    {
        var owner = Guid.CreateVersion7();
        var orderId = await fixture.SeedOrderAsync(customerId: owner);

        var response = await SendAsAsync(
            Guid.CreateVersion7(), "orders:cancel", orderId);   // a different customer

        // 404, not 403 — §11.4. A 403 here would confirm the order exists,
        // which is the whole point of the check, and it is invisible to a
        // handler test that asserts on Result.Failure alone.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Rejects_a_reason_outside_the_wire_vocabulary()
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/orders/{Guid.CreateVersion7()}/cancel")
        {
            // The enum's member name, not the wire code — accepted by
            // Enum.TryParse and rejected here, which is the difference.
            Content = JsonContent.Create(new CancelOrderRequest("CustomerRequest"))
        };
        request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        request.Headers.Add(TestAuthHandler.PermissionsHeader, "orders:cancel");

        var response = await _client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private Task<HttpResponseMessage> SendAsAsync(
        Guid userId, string permissions, Guid orderId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/orders/{orderId}/cancel")
        {
            Content = JsonContent.Create(new CancelOrderRequest(CancelReasons.CustomerRequest))
        };
        request.Headers.Add(TestAuthHandler.UserHeader, userId.ToString());
        request.Headers.Add(TestAuthHandler.PermissionsHeader, permissions);

        return _client.SendAsync(request);
    }
}
```

Four tests, four distinct failures, none reachable from below the HTTP
boundary: a missing `UseAuthentication` (401 becomes 500 or 200), a policy name
that resolves to nothing (403 becomes 200), a resource check returning the wrong
status (404 becomes 403, leaking existence), and a reason parsed by
`Enum.TryParse` instead of the wire vocabulary (400 becomes 200, and the enum's
member names quietly become API surface). Each is a defect this document has
argued about in prose and, until now, asserted nowhere.

### 12.5 Testing the saga

Saga logic is where cross-service bugs live, and MassTransit's in-memory test
harness makes it testable without any infrastructure at all.

```csharp
[Fact]
public async Task Payment_declined_releases_stock_before_cancelling()
{
    await using var provider = new ServiceCollection()
        .AddMassTransitTestHarness(cfg =>
            cfg.AddSagaStateMachine<OrderFulfilmentSaga, OrderFulfilmentState>()
               .InMemoryRepository())
        .BuildServiceProvider(true);

    var harness = provider.GetRequiredService<ITestHarness>();
    await harness.Start();

    var orderId = Guid.CreateVersion7();

    // Every member of V1.OrderPlaced is `required`, so there is no partial
    // construction to elide — a builder keeps that from filling the test.
    await harness.Bus.Publish(Contracts.OrderPlaced(orderId));
    await harness.Bus.Publish(new StockReserved { OrderId = orderId });
    await harness.Bus.Publish(new PaymentDeclined { OrderId = orderId, Reason = "insufficient_funds" });

    // Sent, not Published — the saga issues these as commands to a single
    // owner (§9.6). The harness tracks the two separately, so asserting on
    // Published here would fail while looking like a saga defect.
    (await harness.Sent.Any<ReleaseStock>(m => m.Context.Message.OrderId == orderId))
        .ShouldBeTrue();

    // CancelOrder must not be sent until stock is confirmed released.
    (await harness.Sent.Any<CancelOrder>(m => m.Context.Message.OrderId == orderId))
        .ShouldBeFalse();

    await harness.Bus.Publish(new StockReleased { OrderId = orderId });

    // The reason, not just the send. Both exits from Compensating read
    // ctx.Saga.CancelReason (§9.6), so a transition that forgets to set it on
    // entry produces a CancelOrder carrying null — which this assertion fails
    // on and `Any<CancelOrder>` alone would not.
    (await harness.Sent.Any<CancelOrder>(m =>
        m.Context.Message.OrderId == orderId &&
        m.Context.Message.Reason  == CancelReasons.PaymentDeclined))
            .ShouldBeTrue();
}

[Fact]
public async Task Commands_are_sent_and_events_are_published()
{
    // The distinction §9.6 rests on, asserted directly: publishing a command
    // would deliver it to every subscriber, and nothing else in the suite
    // would notice.
    var harness = await StartHarnessAsync();
    var orderId = Guid.CreateVersion7();

    // Every member of V1.OrderPlaced is `required`, so there is no partial
    // construction to elide — a builder keeps that from filling the test.
    await harness.Bus.Publish(Contracts.OrderPlaced(orderId));

    (await harness.Sent.Any<ReserveStock>()).ShouldBeTrue();
    (await harness.Published.Any<ReserveStock>()).ShouldBeFalse();
}
```

### 12.6 Contract tests

The saga tests above prove one service's coordination. The only thing left that
is genuinely *between* services is the contract assembly, and its rules are all
stated elsewhere as things reviewers should notice: §9.6's "a contract may not
name a domain type", §9.1's versioned namespace, `required` members. Each is
mechanical, so each is a test rather than a review note.

This is the one suite that references every service, which is why it has its own
project and why that project holds nothing else:

```csharp
public class ContractTests
{
    // Concrete types only. The assembly also holds IIntegrationEvent (§9.1)
    // and the static code vocabularies (CancelReasons, ReviewReasons), and a
    // filter of "everything public under Common.Contracts" would demand a
    // versioned namespace of an interface that is deliberately shared across
    // all of them — and then ask ContractSamples for an instance of it.
    private static readonly Type[] Contracts =
        typeof(OrderPlaced).Assembly.GetTypes()
            .Where(t => t.IsPublic
                     && t is { IsInterface: false, IsAbstract: false }
                     && t.Namespace?.StartsWith("Common.Contracts.") == true)
            .ToArray();

    [Fact]
    public void No_contract_names_a_domain_type()
    {
        // §9.6's rule, and the one that silently drags Ordering.Domain into
        // every consuming service. Checked at the assembly level because a
        // contract cannot reference a domain type without the reference.
        typeof(OrderPlaced).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ShouldNotContain(name => name.EndsWith(".Domain"));
    }

    [Fact]
    public void Every_contract_lives_in_a_versioned_namespace()
    {
        // Common.Contracts.<Service>.V<n> — §9.1. A contract that lands one
        // namespace short is a v1 that can never be superseded.
        Contracts.ShouldAllBe(t =>
            Regex.IsMatch(t.Namespace!, @"^Common\.Contracts\.[A-Za-z]+\.V\d+$"));
    }

    [Fact]
    public void Every_contract_round_trips_through_the_bus_serialiser()
    {
        // Catches the member type System.Text.Json cannot handle — the failure
        // that otherwise appears as a message in the error queue, in staging,
        // with a deserialisation stack trace and no obvious owner.
        foreach (var type in Contracts)
        {
            var instance = ContractSamples.Create(type);
            var json     = JsonSerializer.Serialize(instance, type);

            JsonSerializer.Deserialize(json, type).ShouldBeEquivalentTo(instance);
        }
    }
}
```

`ContractSamples.Create` is the reason this suite stays honest as contracts
grow. Every member of a V1 contract is `required` (§12.5), so there is no
reflection shortcut that constructs one — a new contract without a sample fails
here rather than being quietly skipped, which is the failure mode of every
"iterate over all the types" test that defaults to `Activator.CreateInstance`.

### 12.7 Test doubles

| Dependency | Approach |
|---|---|
| Domain objects | None. Use real ones. |
| Own database | Real, via Testcontainers |
| Own Redis | Real, via Testcontainers |
| Own broker | Real container, or the MassTransit test harness |
| Another service (HTTP) | WireMock.Net — a real HTTP server with stubbed responses |
| Third-party API | WireMock.Net, plus a nightly contract test against their sandbox |
| Clock | `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` |
| Random / GUIDs | Inject a seam; never call `Guid.NewGuid()` where the value is asserted |

**Mock only what you do not own.** Mocking your own repository tests your
understanding of the mock rather than the behaviour of the system, and it is the
single most common cause of a green suite over a broken application.

### 12.8 Conventions

- **Naming:** `Method_expected_behaviour_when_condition`. Readable in the
  test-runner output without opening the file.
- **One logical assertion per test.** Several `Should` calls verifying one
  outcome is fine; testing two unrelated behaviours is not.
- **No conditionals in tests.** An `if` in a test means it is two tests.
- **No shared mutable state between tests.** Every test constructs what it needs.
- **Deterministic.** No `DateTime.Now`, no unseeded random, no reliance on
  execution order, no `Thread.Sleep` — wait on a condition instead.
- **Every consumer and projection gets a redelivery test.** Not a convention —
  a test, because the guarantee it protects is invisible in the code that
  breaks it. The inbox commits separately from a Dapper projection's write
  (§9.5), so a crash between them redelivers, and idempotency is what makes
  that harmless. The test is one line longer than the happy path: handle the
  same event twice, assert the row and every counter read the same as after
  one. A new handler that skips it fails the first time production restarts
  at the wrong moment, which is to say months later, on data nobody can
  reconstruct.

### 12.9 What not to test

Auto-properties. Framework behaviour. Third-party libraries. EF Core's mapping
in a unit test — it is covered by any integration test that reads and writes.
Private methods — test them through the public API that uses them, or extract
them into something with its own public API.

**Coverage** is a diagnostic, not a target. Below roughly 60% you are certainly
missing things worth testing; above roughly 85% you are usually testing getters
to move a number. Watch the *trend* and watch coverage of the domain layer
specifically — that is where it should be near-total, and where it is cheapest
to achieve.

---

## 13. Observability

### 13.1 The three signals

| Signal | Question it answers | Tool |
|---|---|---|
| Metrics | Is something wrong right now? | Prometheus |
| Traces | Where in the system is it wrong? | Tempo / Jaeger |
| Logs | What exactly happened? | Loki / Seq |

They are used in that order during an incident. An alert fires on a metric, a
trace localises it to a service and a span, and logs filtered by that trace ID
explain it. Correlation between the three is what makes this work, which is why
section 10.4 exists.

### 13.2 OpenTelemetry

Configure once in `Common.Web` (§4.1), referenced by every service host.
`AddObservability` is one of the pieces `AddCommonWebDefaults` composes — the
single call every `Program.cs` makes (§4.2):

```csharp
public static IHostApplicationBuilder AddCommonWebDefaults(this IHostApplicationBuilder builder)
{
    builder.AddObservability();                            // this section

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(o => { /* §11.3 */ });

    // The one policy every host shares, and the only one Common.Web may know:
    // "is there a valid token". Permission policies are per-service and are
    // registered by the service (§11.4) or, for the gateway, by the gateway.
    //
    // This is deliberately identical to ASP.NET Core's default policy, which
    // YARP would accept as the magic string "default" (§10.2). Naming it costs
    // one line and buys a route file that says what it means — that file is
    // read by people deciding whether a path is public.
    builder.Services.AddAuthorizationBuilder()
        .AddPolicy("authenticated", p => p.RequireAuthenticatedUser());

    builder.Services.AddProblemDetails(o => { /* §10.5 */ });

    // Liveness only — it must not touch dependencies (§13.5), and Common.Web
    // has no connection strings anyway. Readiness checks are registered by
    // each service's own Infrastructure, which does.
    builder.Services.AddHealthChecks();

    return builder;
}
```

Note what is **not** here. `AddCommonWebDefaults` covers what every host needs
identically. Anything needing a connection string — the SQL, Redis, broker and
outbox checks in §13.5 — belongs in `AddOrderingInfrastructure`, because
`Common.Web` cannot know them.

```csharp
public static IHostApplicationBuilder AddObservability(this IHostApplicationBuilder builder)
{
    var serviceName = builder.Environment.ApplicationName;

    builder.Logging.AddOpenTelemetry(logging =>
    {
        logging.IncludeFormattedMessage = true;
        logging.IncludeScopes           = true;

        // §13.4's "never log a secret" rule, given a mechanism. Registered
        // here because this is the only logging pipeline the host has — a
        // redaction policy configured on a library nobody installed redacts
        // nothing, and reads in review as though it does.
        logging.AddProcessor(new SensitiveDataRedactor());
    });

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r
            .AddService(serviceName, serviceVersion: BuildInfo.Version)
            .AddAttributes([
                new("deployment.environment", builder.Environment.EnvironmentName)
            ]))
        .WithMetrics(m => m
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            // Every meter an alert or SLO reads from. A condition whose signal
            // is not registered here cannot fire — it looks configured and is
            // silent, which is worse than having no alert at all.
            .AddMeter("Ordering.Orders")                       // §13.3, §13.6
            .AddMeter("Ordering.Outbox")                       // §13.6 per-lane
            // Shared names, not service-prefixed: every service emits the same
            // instruments and the service.name resource attribute separates
            // them. One dashboard query then works for all of them, and a new
            // service appears on it without anyone editing a panel.
            .AddMeter("Commerce.Requests")                     // §13.3, §13.7
            .AddMeter("Commerce.Messaging")                    // §13.3, §13.7
            .AddMeter("MassTransit")
            .AddMeter("Microsoft.Extensions.Caching.Hybrid")   // cache hit ratio
            .AddMeter("StackExchange.Redis"))
        .WithTracing(t => t
            .AddAspNetCoreInstrumentation(o =>
                o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
            .AddHttpClientInstrumentation()
            .AddEntityFrameworkCoreInstrumentation(o => o.SetDbStatementForText = true)
            .AddRedisInstrumentation()
            .AddSource("MassTransit"))
        .UseOtlpExporter();

    return builder;
}
```

Filtering health checks out of traces is not cosmetic — at a ten-second probe
interval across a dozen pods they would otherwise dominate both trace volume and
storage cost.

`SetDbStatementForText` records SQL text on spans, which is invaluable for
debugging and a data-exposure risk if queries embed sensitive literals.
Parameterised queries — which everything here uses — record the parameterised
form, so this is safe as configured. Revisit it if anyone introduces string
concatenation into SQL.

### 13.3 Domain metrics

Infrastructure metrics tell you the servers are healthy. Business metrics tell
you the business is healthy, and they catch a category of failure that CPU
graphs never will.

Placement follows the dependency rule, not the topic. `OrderMetrics` records
domain quantities — `Placed(Money total)` — so it cannot sit in
`Common.Application`, which is shared across services and references no domain.
Its only call site is `OrderSummaryProjection`, which is Infrastructure, so
Infrastructure would also compile. It belongs in `Ordering.Application` anyway:
the type is a statement about the business vocabulary — placed, cancelled,
fulfilled — and Infrastructure is where that vocabulary is *implemented*, not
where it is defined. Application is also where it has to be the moment a
handler needs it again, and moving a type to satisfy one new call site is how
its meaning drifts:

```csharp
namespace Ordering.Application.Orders;

public sealed class OrderMetrics
{
    private readonly Counter<long>   _placed;
    private readonly Counter<long>   _cancelled;
    private readonly Histogram<double> _value;
    private readonly Histogram<double> _fulfilmentSeconds;

    public OrderMetrics(IMeterFactory factory)
    {
        var meter = factory.Create("Ordering.Orders");

        _placed    = meter.CreateCounter<long>("orders.placed",
                        unit: "{order}", description: "Orders successfully placed.");
        _cancelled = meter.CreateCounter<long>("orders.cancelled",
                        unit: "{order}");
        _value     = meter.CreateHistogram<double>("orders.value",
                        unit: "EUR");
        _fulfilmentSeconds = meter.CreateHistogram<double>("orders.fulfilment.duration",
                        unit: "s", description: "Placed to confirmed.");
    }

    public void Placed(Money total)
    {
        _placed.Add(1, new KeyValuePair<string, object?>("currency", total.Currency));
        _value.Record((double)total.Amount,
            new KeyValuePair<string, object?>("currency", total.Currency));
    }

    public void Cancelled(string reason) =>
        _cancelled.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void Fulfilled(TimeSpan placedToConfirmed) =>
        _fulfilmentSeconds.Record(placedToConfirmed.TotalSeconds);
}
```

An instrument with no call site is a metric that reads zero forever, which is
indistinguishable from a system doing no work. All three call sites are in
`OrderSummaryProjection` (§6.6), and that is a rule rather than a coincidence:

> **A business metric is recorded on the committed path, never inside the
> write transaction.** A handler runs inside one, so a counter it increments
> counts orders that roll back — and counts them once per attempt when EF's
> retrying execution strategy replays the delegate (§6.3). The projection runs
> after the commit, driven by the `Local` outbox lane, which is the earliest
> point at which "an order was placed" is true.
>
> **And exactly once, which rows-affected does not give you.** The `Local` lane
> is at-least-once *and* unordered (§9.4), so "did this write change anything"
> answers the redelivery question and not the ordering one: a cancellation
> claimed before its placement changes a row, and counting it there records a
> cancelled order that `orders.placed` has not counted and — if the placement
> row is later abandoned — never will. `cancelled > placed` is unreachable in
> the write model, and a metric that can reach it is a metric no reconciliation
> can trust.
>
> So each counter is a **claim against the row**: a flag column, flipped and
> read in one `UPDATE`, with a predicate naming everything that must already be
> true. `RecordPendingFactsAsync` runs all three after every write, because any
> write may be the one that completes a pair.
>
> The rule generalises: **a business counter is state, not a side effect.** It
> fires once per fact, the fact is the row satisfying a predicate, and "it
> already fired" belongs in the same table as the fact — not in the control flow
> of whichever handler happened to arrive first.

```csharp
// RecordPendingFactsAsync — the placement claim. Money is reassembled from the
// row rather than taken from the event, because the event that triggers this
// call may be a cancellation. Through Money.Of (§5.3), which is the only way
// in and the one that normalises the padded CHAR(3) the row returns.
if (placed is not null)
    metrics.Placed(Money.Of(placed.TotalAmount, placed.Currency.Trim()));
```

```csharp
// The cancellation claim. PlacedCounted = 1 in the predicate is what orders the
// two counters; CancelReason is written by the handler through
// CancellationReasons.ToCode, the same table the parse uses, which keeps the
// dimension a bounded, stable set.
if (cancelled is not null)
    metrics.Cancelled(cancelled);
```

Fulfilment duration is recorded there for a second reason on top of that one. It
spans placement to confirmation, and the summary row is the only place that sees
both ends — the handler that confirms an order knows nothing about when it was
placed:

```csharp
// OrderSummaryProjection.RecordPendingFactsAsync (§6.6), one of three claims.
var fulfilment = await connection.QuerySingleOrDefaultAsync<FulfilmentFact>(
    """
    UPDATE ordering.OrderSummaries SET FulfilmentCounted = 1
    OUTPUT inserted.PlacedAt, inserted.ConfirmedAt
    WHERE  OrderId = @OrderId AND PlacedAt IS NOT NULL
           AND ConfirmedAt IS NOT NULL AND FulfilmentCounted = 0;
    """, args);

if (fulfilment is not null)
    metrics.Fulfilled(fulfilment.ConfirmedAt - fulfilment.PlacedAt);
```

> The projection is the right home for a *duration* metric for the same reason
> it is the right home for a denormalised name: it is the component that has
> already gathered both halves. A handler measuring this would have to re-read
> the aggregate to find its own start time.
>
> **Note what the predicate is not.** An earlier version of this measured
> `now − PlacedAt` when the `Confirmed` event arrived, guarded by
> `if (placedAt is not null)`. That guard was defensive against a case that
> could not happen — until the out-of-order fix in §6.6 made `PlacedAt`
> legitimately NULL for an order whose confirmation was claimed first. The
> guard then silently dropped the measurement, permanently, for exactly the
> orders whose delivery was disordered — which correlates with load, which is
> when the number matters. Claiming on "both timestamps present and not yet
> counted" has no such ordering assumption to be wrong about.

Note the cardinality discipline: tags are `currency` and `reason` — small,
bounded sets. **Never tag a metric with an order ID, customer ID or URL with an
embedded ID.** Each distinct tag combination is a separate time series, and
unbounded cardinality is the standard way to take down a Prometheus instance.

> **Only one of these four is alerted on, and that is deliberate.**
> `orders.placed` backs the business-volume alert (§13.6) because a drop in it
> is a symptom nothing else shows. `orders.cancelled`, `orders.value` and
> `orders.fulfilment.duration` are dashboard metrics: they answer *"how is the
> business doing"*, a question with no threshold that should wake anyone.
>
> The rule runs one way only. **Every alert and SLO row must name an instrument**
> (§13.6, §13.7) — a target with no signal reads as satisfied. An instrument
> with no alert is just a number somebody looks at, which is most of them. The
> asymmetry is worth stating because the tidy-looking mistake is to invent
> thresholds for the other three so every metric has a row, and a page for
> "cancellations up 20%" is one nobody can act on at 3 a.m.

#### The two types this section defines, and where the rest come from

Domain metrics answer business questions. The SLO table answers *"is this
service behaving"*, and its rows need instruments too — a target with no signal
is not a target, it is an intention.

§13.7's seven rows read **four** sources, and only two of them are defined here.
Naming all four is the point of the table, because the two that are not are the
ones a reader would otherwise go looking for in this section and fail to find:

| Source | Defined in | Signals it provides to §13.7 |
|---|---|---|
| `RequestMetrics` | here, `Common.Application` | `request.duration` — the command and query p95 rows |
| `MessagingMetrics` | here, `Common.Infrastructure` | `messaging.delivery.lag`, `projection.lag` |
| `OutboxMetrics` | §13.6, `Ordering.Infrastructure` | `outbox.oldest.age`, read once per lane |
| ASP.NET Core instrumentation | the framework, enabled in §13.2 | `http.server.request.duration` — the availability row |

`RequestMetrics` is Application because `LoggingBehavior` injects it and the
pipeline is Application. `MessagingMetrics` is Infrastructure because all three
of its call sites are — two consumers and the outbox dispatcher's invoker.
`OutboxMetrics` is separate from both because it reads the database, which is
also why it is observable rather than pushed (§13.6).

One instrument on this list feeds no SLO row: `command.domain_rejected` is a
business signal that happens to share `MessagingMetrics`' meter (§9.8). It is
mentioned here so the count in the table above matches the class below it —
a discrepancy between the two is how the previous version of this heading came
to claim there were three of them.

```csharp
// Common.Application — registered by AddOrderingApplication (§4.2) and forced
// at startup like every other metrics type (§13.6): "a behaviour injects it"
// is not the same as "something has constructed it".
public sealed class RequestMetrics
{
    private readonly Histogram<double> _duration;

    public RequestMetrics(IMeterFactory factory)
    {
        var meter = factory.Create("Commerce.Requests");
        _duration = meter.CreateHistogram<double>("request.duration",
                        unit: "s", description: "Dispatcher entry to result.");
    }

    public void Recorded(string request, string outcome, TimeSpan elapsed) =>
        _duration.Record(elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("request", request),
            new KeyValuePair<string, object?>("outcome", outcome));
}
```

```csharp
// Common.Infrastructure — registered by AddOrderingInfrastructure, because
// both call sites are Infrastructure types (§9.4).
public sealed class MessagingMetrics
{
    private readonly Histogram<double> _deliveryLag;
    private readonly Histogram<double> _projectionLag;
    private readonly Counter<long>     _rejected;

    public MessagingMetrics(IMeterFactory factory)
    {
        var meter = factory.Create("Commerce.Messaging");

        _deliveryLag   = meter.CreateHistogram<double>("messaging.delivery.lag",
                             unit: "s", description: "OccurredAt to consumer start.");
        _projectionLag = meter.CreateHistogram<double>("projection.lag",
                             unit: "s", description: "Event raised to projection applied.");
        _rejected      = meter.CreateCounter<long>("command.domain_rejected",
                             description: "Message-borne commands the domain refused (§9.8).");
    }

    public void Delivered(string message, TimeSpan lag) =>
        _deliveryLag.Record(lag.TotalSeconds,
            new KeyValuePair<string, object?>("message", message));

    public void Projected(string message, TimeSpan lag) =>
        _projectionLag.Record(lag.TotalSeconds,
            new KeyValuePair<string, object?>("message", message));

    public void Rejected(string message, string error) =>
        _rejected.Add(1,
            new KeyValuePair<string, object?>("message", message),
            new KeyValuePair<string, object?>("error", error));
}
```

The two lags read `OccurredAt` from **different places**, because they measure
different lanes. `Delivered` reads it **off the message**: it covers the broker
lane, every integration event carries the field (§9.1), and
`IntegrationEventConsumer<T>` reaches it through the `IIntegrationEvent`
constraint — so there is no header to define and nothing to keep in sync.
`Projected` reads it **off the outbox row**, which the claim now returns (§9.4).
It has to: the local lane carries domain events, and `ProjectionInvoker<TEvent>`
is deliberately unconstrained — `IProjectionHandler<T>` is satisfied by any
type, including the read-model-shaped events a projection may prefer. Every
`IDomainEvent` does carry `OccurredAt` (§5.5), so a constraint would compile
today; it would also make the metric the reason the invoker cannot accept a
plain record tomorrow. The row already has the timestamp, and reading it there
costs a column the claim was going to pay for anyway.

`IntegrationEventConsumer<T>` and `CommandConsumer<,>` take `MessagingMetrics`
as a constructor parameter. `Projected` is recorded by `ProjectionInvoker`
(§9.4), which is static and cached — it resolves `MessagingMetrics` from the
`IServiceProvider` it is already handed rather than through a constructor it
does not have.

Both lags compare a timestamp made on another machine, so both carry the same
caveat: they are useful at second granularity and meaningless below it, which is
why §13.7's targets for them are in seconds and not milliseconds. The third
instrument, `command.domain_rejected`, is a plain counter with no such
caveat — it is recorded by `CommandConsumer` at the moment the dispatcher
returns a failure, on the same machine (§9.8).

> **These need no `MetricsInitialiser` entry (§13.6), and the difference is the
> instrument kind.** An observable gauge is pull-based — the collector asks, and
> if nothing ever constructed the class there is nothing to ask, which is why
> the outbox gauges are forced. A histogram is pushed from a live call site, so
> anything recording it has already resolved the class. Forcing construction is
> the fix for a *pull* instrument with no consumer, not a habit to apply to all
> of them.

The behaviour that records the first of these is the one behaviour §6.3 never
showed:

```csharp
// Common.Application. Registered first, so it is outermost (§6.3): the span
// covers validation, idempotency, the transaction and the handler.
public sealed class LoggingBehavior<TRequest, TResult>(
    ILogger<LoggingBehavior<TRequest, TResult>> logger,
    RequestMetrics metrics,
    TimeProvider clock)
    : IPipelineBehavior<TRequest, TResult>
{
    public async Task<TResult> HandleAsync(
        TRequest request, NextDelegate<TResult> next, CancellationToken ct)
    {
        var name  = typeof(TRequest).Name;
        var start = clock.GetTimestamp();

        // A scope, not a log property: everything written inside the handler
        // inherits it, including EF Core's and MassTransit's own logging.
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["RequestType"] = name
        });

        try
        {
            var result = await next();

            logger.LogInformation("{RequestType} completed in {ElapsedMs} ms",
                name, clock.GetElapsedTime(start).TotalMilliseconds);
            metrics.Recorded(name, "ok", clock.GetElapsedTime(start));

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{RequestType} threw", name);
            metrics.Recorded(name, "error", clock.GetElapsedTime(start));
            throw;
        }
    }
}
```

> **`outcome` is `ok` or `error`, and a returned `Result.Failure` is `ok`.** The
> behaviour is generic over `TResult` and cannot see inside it without a
> constraint that would exclude queries — but the deeper reason is that a
> rejected command is a normal outcome of a working system. "Order cannot be
> cancelled once shipped" is the domain doing its job, and counting it as an
> error makes the one number that should mean *"something is broken"* track
> customer behaviour instead. Business outcomes are counted by the domain
> instruments above, where they have names.

`TimeProvider.GetTimestamp()` rather than `Stopwatch`, for the reason §5.4
gives about time: the same seam the tests replace, used everywhere including
here.

### 13.4 Structured logging

```csharp
// Good — structured, queryable, no PII.
logger.LogInformation(
    "Order {OrderId} placed by customer {CustomerId} for {Amount} {Currency}",
    order.Id, order.CustomerId, order.Total.Amount, order.Total.Currency);

// Bad — string interpolation destroys the structure; the fields cannot be
// queried and every message is a distinct string.
logger.LogInformation($"Order {order.Id} placed for {order.Total}");
```

Levels, applied consistently:

| Level | Use | Example |
|---|---|---|
| `Trace` | Developer diagnostics. Off in production. | Method entry with arguments |
| `Debug` | Diagnosable detail. Off by default in production. | Cache miss, retry attempt |
| `Information` | Business events worth an audit trail. | Order placed, payment authorised |
| `Warning` | Recovered, but someone should know. | Retry succeeded after failures, circuit half-open |
| `Error` | An operation failed. | Handler threw, message went to error queue |
| `Critical` | The service cannot function. | Database unreachable at startup |

**Never log:** passwords, tokens, full card numbers, national ID numbers, or
full request bodies on endpoints that accept them.

A rule of that shape needs a mechanism, or it is a request that every future
developer remember it. The mechanism is a log processor on the pipeline
§13.2 already builds, so a property named `Password` is redacted by default
rather than by discipline:

```csharp
// Common.Web — added to the OpenTelemetry logging pipeline in §13.2, which is
// the point: every host calls AddObservability, so the rule applies to all of
// them. In a service's own project it would protect that service alone.
// Public for the same reason `MetricsInitialiser` (§13.6) and `Program` (§4.2)
// are: the test below constructs it, and no test project lives inside
// Common.Web. One access modifier beats an InternalsVisibleTo that has to name
// its consumer.
public sealed class SensitiveDataRedactor : BaseProcessor<LogRecord>
{
    // Substring match, not equality: the field that leaks is never named
    // exactly "password" — it is "NewPassword", "card_number", "id_token".
    private static readonly string[] Sensitive =
        ["password", "secret", "token", "authorization", "cardnumber",
         "card_number", "ssn", "nationalid"];

    public override void OnEnd(LogRecord record)
    {
        if (record.Attributes is null) return;

        List<KeyValuePair<string, object?>>? scrubbed = null;

        for (var i = 0; i < record.Attributes.Count; i++)
        {
            var attribute = record.Attributes[i];
            if (!IsSensitive(attribute.Key)) continue;

            // Copy only when something actually matches — the common case is
            // no match, and this runs on every log record on every request.
            scrubbed ??= [.. record.Attributes];
            scrubbed[i] = new(attribute.Key, "[redacted]");
        }

        if (scrubbed is not null) record.Attributes = scrubbed;
    }

    private static bool IsSensitive(string key) =>
        Sensitive.Any(s => key.Contains(s, StringComparison.OrdinalIgnoreCase));
}
```

Two limits worth stating rather than discovering. The processor sees
**attributes**, not the formatted message, so
`logger.LogInformation("Token is {Value}", token)` is redacted by its key only
if that key is named sensitively — which is the argument for naming the
placeholder `{Token}` and never interpolating. And it cannot help with a whole
object logged as one attribute; that is what the "never log full request
bodies" half of the rule is for.

Assert it, because a redactor that silently stops matching is worse than none.
The test lives in `Ordering.Api.Tests` — a `Common.Web` behaviour tested once
rather than once per host, in the suite that already owns host-level concerns
(§12.1). Every host calls `AddObservability`, so a second copy in Catalog's
suite would re-assert the same processor over the same pipeline and only add a
place to forget.

Assert it through `ILogger`, not through OpenTelemetry's logger provider
directly. The Logs Bridge API (`Sdk.CreateLoggerProviderBuilder`) is shipped
behind an experimental diagnostic and is not how any host here produces a log
record; a test that used it would be green while the path in production drifted
away underneath it:

```csharp
[Fact]
public void Sensitive_attributes_are_redacted()
{
    var exported = new List<LogRecord>();

    // Built exactly as AddObservability builds it (§13.2) — ILoggingBuilder,
    // the same extension, so the test covers the seam the host uses.
    using var factory = LoggerFactory.Create(b => b.AddOpenTelemetry(o =>
    {
        o.AddProcessor(new SensitiveDataRedactor());
        o.AddInMemoryExporter(exported);
    }));

    factory.CreateLogger("test").LogInformation(
        "Login for {User} with {Password}", "ada", "hunter2");

    var attributes = exported.Single().Attributes!;
    attributes.Single(a => a.Key == "Password").Value.ShouldBe("[redacted]");

    // The other half of the assertion, and the one that catches a deny-list
    // grown careless: everything not on it survives intact.
    attributes.Single(a => a.Key == "User").Value.ShouldBe("ada");
}
```

Going through `ILogger` also means the test exercises message templates, which
is where the attribute keys come from — so the `{Token}` naming advice above is
verified by this test rather than merely stated near it.

### 13.5 Health checks

Three distinct endpoints, because Kubernetes asks three distinct questions.

Registration and exposure live in different places, for one reason: the checks
need connection strings and the endpoints do not.

**The checks** are registered by the service's own Infrastructure — the block
shown in `AddOrderingInfrastructure` (§4.2), which has the configuration:

```csharp
services.AddHealthChecks()
    .AddSqlServer(configuration.GetConnectionString("Ordering")!, name: "sql", tags: ["ready"])
    .AddRedis(configuration.GetConnectionString("RedisCache")!, name: "redis-cache", tags: ["ready"])
    .AddRedis(configuration.GetConnectionString("RedisCoordination")!, name: "redis-coordination", tags: ["ready"])
    .AddRabbitMQ(name: "rabbitmq", tags: ["ready"])
    // Observed, not gating — see the note below.
    .AddCheck<OutboxBacklogHealthCheck>("outbox", tags: ["observe"]);
```

**The endpoints** are mapped once in `Common.Web`, since the tag predicates are
identical for every service and need no configuration. `Program.cs` calls this
after `builder.Build()` (§4.2):

```csharp
namespace Common.Web;

public static IEndpointRouteBuilder MapCommonHealthEndpoints(this IEndpointRouteBuilder app)
{
    // AllowAnonymous is required, not cosmetic: the kubelet sends no token,
    // so an authenticated probe fails and the pod is restarted in a loop.
    app.MapHealthChecks("/health/live",    new() { Predicate = _ => false })
       .AllowAnonymous();

    app.MapHealthChecks("/health/ready",   new() { Predicate = c => c.Tags.Contains("ready") })
       .AllowAnonymous();

    app.MapHealthChecks("/health/startup", new() { Predicate = c => c.Tags.Contains("ready") })
       .AllowAnonymous();

    return app;
}
```

A host that registers no readiness checks therefore reports ready immediately.
That is correct for exactly two hosts here — the **gateway** and the **BFF**,
which own no database (§4.2) — and correct for neither of the six services.
Every service owns a schema, including the two with no public API: Shipping and
Notifications both ship a migrator and both register a SQL check (§4.1, §3.2).

The distinction matters because "reports ready immediately" is indistinguishable
from "readiness was never wired up". A service whose Infrastructure forgot
`AddHealthChecks().AddSqlServer(...)` takes traffic before its database is
reachable and answers the first requests with connection errors — while its
probe stays green, because an empty predicate set is a passing predicate set.
The rule that separates the two cases: **a host with a connection string has a
readiness check, and a host without one does not.**

| Endpoint | Question | On failure |
|---|---|---|
| `/health/live` | Is the process alive? | Kubernetes restarts the pod |
| `/health/ready` | Can it serve traffic? | Removed from the load balancer, not restarted |
| `/health/startup` | Has it finished starting? | Liveness probing is deferred |

**Liveness must not check dependencies.** If liveness checks the database, a
brief database outage restarts every pod simultaneously, and the restart storm
outlasts the outage. Liveness answers only "is this process wedged?".

**Readiness must not check the outbox backlog either.** A growing backlog means
events are not being *delivered*; the service can still accept commands and
serve queries perfectly well. Gating readiness on it means a RabbitMQ blip pulls
every pod out of the load balancer and converts a delivery delay into a total
outage — the failure amplifying exactly when the system is already degraded.
The outbox is tagged `observe`, scraped for metrics and alerted on (§13.6), and
deliberately not part of any probe.

### 13.6 What to alert on

Alert on symptoms users experience, not on causes. Each alert should be
actionable — if the response is "acknowledge and ignore", delete it.

| Alert | Condition | Why | Runbook |
|---|---|---|---|
| Error rate | 5xx > 1% over 5 min | Users are seeing failures | `error-rate.md` |
| Latency | p99 > 1 s over 10 min | Users are waiting | `latency.md` |
| Error queue depth | > 0 | A business process has stopped | `error-queue.md` |
| Saga age | any saga unfinalised > 1 h | Orders are stuck | `stuck-saga.md` |
| Orders awaiting review | any row in `ordering.OrderReviews` older than 1 h | A saga hit a wait it could not compensate and escalated (§9.6). It has already finalised, so the saga-age alert above will *not* catch this | `order-review.md` |
| Migration job failed | Helm `pre-upgrade` hook non-zero, or a release stuck pending | The deploy stopped before any pod rolled (§7.4); the previous version is still serving, which is why nothing else fires | `migration-failure.md` |
| Cache hit ratio collapse | `rate(cache_hits) / rate(cache_hits + cache_misses)` < 50% over 10 min, from `Microsoft.Extensions.Caching.Hybrid` | Redis lost its working set; every miss becomes a database read, and the databases are sized for a warm cache (ADR-006) | `redis-cold.md` |
| Business volume | `orders.placed` per hour drops > 50% vs the same hour last week | The most valuable alert here — it catches failures no technical metric detects. §6.6's worked case: `ordering.ProductPrices` has no row for a product, every order containing it fails validation, and the result is a 400 the customer sees, no exception, no 5xx and no lag. Week-over-week rather than a fixed floor, because a volume alert without a seasonality model is the first pager people mute | `business-volume.md` |

#### Outbox alerts are per lane

The two outbox lanes (§9.4) fail for different reasons, produce different
symptoms, and need different people. A single "outbox backlog" alert averages
them into something nobody can act on.

The runbook column is not decoration. §13.9 requires every alert to have one,
and the pairing is checkable in both directions: an alert with no runbook is a
3 a.m. page with no procedure, and a runbook with no alert is a procedure
nobody will be told to follow.

| Alert | Condition | Symptom | Likely cause | Runbook |
|---|---|---|---|---|
| **Broker lane stalled** | `outbox.oldest.age{lane="Broker"}` > 2 min | *Other services* are working from stale data; sagas stop advancing | Broker unreachable, credentials expired, queue at its length limit, network policy change | `outbox-broker.md` |
| **Local lane stalled** | `outbox.oldest.age{lane="Local"}` > 30 s | *This service's* read models are stale — users see missing or outdated list data | A projection handler throwing, read-model deadlock, schema drift after a migration | `projection-lag.md` |
| **Outbox growth** | `sum(outbox.pending.count)` > 1000 and rising over 10 min | Either lane, not keeping up | Dispatcher not running, batch size too small for load, purge job failed | `outbox-growth.md` |
| **Abandoned rows** | `sum(outbox.abandoned.count)` > 0 | Silent permanent data loss | A message that will never be delivered and is no longer being retried. The `lane` tag says whose loss: `Broker`, and other services never learned something; `Local`, and this service's read model is permanently wrong | `outbox-abandoned.md` |

Thresholds differ by an order of magnitude because the lanes have different
floors. The local lane is in-process with no network hop, so 30 seconds of lag
already means something is wrong. The broker lane crosses a network and should
absorb a short RabbitMQ blip or a rolling broker restart without paging anyone.

> **Alert on abandoned rows specifically.** The dispatcher claims rows `WHERE
> Attempts < 10` (§9.4), so a row that exceeds the cap is silently skipped
> forever. Without this alert, permanent loss of a business event looks
> identical to a healthy, empty backlog — the queue drains and the graph goes
> green precisely because the message was given up on.

All three gauges carry `lane`, so one query serves both lanes and every alert
above can say which one it is talking about:

```csharp
// Singleton, and eagerly constructed (below). Observable gauges are callbacks
// held by the Meter: if this object is never built, or is built and dropped,
// the instrument does not exist and the alert is silent.
public sealed class OutboxMetrics
{
    // The meter name is the contract with §13.2's AddMeter — an instrument on
    // an unregistered meter is collected by nothing and alerted on in vain.
    public const string MeterName = "Ordering.Outbox";

    // The tag value is the enum's own name, never a hand-written string. The
    // Lane column stores lane.ToString() and §9.4's dispatcher compares against
    // "Broker", so a lowercase tag here would give the same value three
    // spellings across SQL, C# and PromQL — and an alert querying the wrong one
    // matches no series and never fires, which looks exactly like health.
    private static KeyValuePair<string, object?> Tag(OutboxLane lane) =>
        new("lane", lane.ToString());

    public OutboxMetrics(IMeterFactory factory, IOutboxStats stats)
    {
        var meter = factory.Create(MeterName);

        meter.CreateObservableGauge(
            "outbox.oldest.age",
            () => new[]
            {
                new Measurement<double>(stats.OldestAgeSeconds(OutboxLane.Broker), Tag(OutboxLane.Broker)),
                new Measurement<double>(stats.OldestAgeSeconds(OutboxLane.Local),  Tag(OutboxLane.Local))
            },
            unit: "s");

        // Depth, per lane. The growth alert needs a count and the age gauge
        // cannot supply one: a single very old row and a backlog of ten
        // thousand recent ones read identically on oldest-age.
        meter.CreateObservableGauge(
            "outbox.pending.count",
            () => new[]
            {
                new Measurement<int>(stats.PendingCount(OutboxLane.Broker), Tag(OutboxLane.Broker)),
                new Measurement<int>(stats.PendingCount(OutboxLane.Local),  Tag(OutboxLane.Local))
            },
            unit: "{message}");

        // Also per lane, and this is the one where it matters most: a Broker
        // abandonment means other services never learned something, a Local one
        // means this service's own read model is permanently wrong. Different
        // blast radius, different recovery, and outbox-abandoned.md asks which
        // one first.
        meter.CreateObservableGauge(
            "outbox.abandoned.count",
            () => new[]
            {
                new Measurement<int>(stats.AbandonedCount(OutboxLane.Broker), Tag(OutboxLane.Broker)),
                new Measurement<int>(stats.AbandonedCount(OutboxLane.Local),  Tag(OutboxLane.Local))
            },
            unit: "{message}");
    }
}
```

`IOutboxStats` is read from a singleton on the collector's schedule, so it owns
a scope per call rather than holding a `DbContext`:

```csharp
// Every member takes the lane. Three questions about one table, each of which
// has a different answer per lane and a different runbook behind it.
public interface IOutboxStats
{
    double OldestAgeSeconds(OutboxLane lane);
    int PendingCount(OutboxLane lane);
    int AbandonedCount(OutboxLane lane);
}

internal sealed class OutboxStats(IServiceScopeFactory scopes) : IOutboxStats
{
    // Cached briefly: the collector polls every few seconds and these are
    // aggregate queries over a filtered index, not free.
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public double OldestAgeSeconds(OutboxLane lane) => _cache.GetOrCreate(
        $"oldest:{lane}", e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(5);
            using var scope = scopes.CreateScope();
            using var connection = scope.ServiceProvider
                .GetRequiredService<IDbConnectionFactory>().Create();

            return connection.ExecuteScalar<double?>(
                """
                SELECT DATEDIFF(second, MIN(OccurredAt), SYSDATETIMEOFFSET())
                FROM   ordering.OutboxMessages
                WHERE  ProcessedAt IS NULL AND Lane = @lane;
                """, new { lane = lane.ToString() }) ?? 0;
        });

    // PendingCount and AbandonedCount follow the same shape — same cache, same
    // scope-per-call, same @lane parameter — over COUNT(*) with
    // `ProcessedAt IS NULL AND Lane = @lane`, plus `AND Attempts >= 10` for the
    // second. The lane predicate is not optional on any of the three: an
    // untagged gauge cannot answer the first question its runbook asks.
}
```

> **Two gauges, because they answer different questions and fail differently.**
> `outbox.oldest.age` catches a lane that has *stopped*; `outbox.pending.count`
> catches one that is *falling behind*. Neither substitutes for the other: a
> single stuck row pins the age gauge at hours while the count stays at 1, and
> a backlog of ten thousand rows all seconds old leaves the age gauge flat.
> The alerts in the table read one each, which is why both exist.

Registration is the step that makes any of this exist, and it is the step
`ValidateOnBuild` cannot check — nothing depends on a metrics class, so the
container is happy without it (§6.2):

```csharp
// In AddOrderingInfrastructure (§4.2). Both of Infrastructure's metrics types:
// OutboxMetrics reads the database, MessagingMetrics is injected by the two
// consumers and resolved by the outbox invoker (§13.3).
services.AddSingleton<IOutboxStats, OutboxStats>();
services.AddSingleton<OutboxMetrics>();
services.AddSingleton<MessagingMetrics>();

// OrderMetrics and RequestMetrics are NOT registered here — they are
// Application types and AddOrderingApplication already registers them (§4.2).
// A second AddSingleton would not fail: the container keeps both and resolves
// the last, which is the trap. Two instances mean two sets of instruments on
// one meter, and the one MetricsInitialiser forces need not be the one the
// projection injects. The counters read zero for ever while a live instrument
// publishes to nobody.

// Singleton registration alone is lazy — the instruments appear on first
// resolve, which for a class nothing injects is never. Force construction at
// startup, for all four. MetricsInitialiser is registered by Infrastructure,
// which may reference Application; the reverse would not compile.
services.AddHostedService<MetricsInitialiser>();
```

```csharp
// Public, not internal, for the same reason `Program` is (§4.2): the test
// below names the type from another assembly, and one access modifier is a
// smaller commitment than an InternalsVisibleTo that has to name the consumer.
public sealed class MetricsInitialiser(
    OutboxMetrics _, OrderMetrics __,
    MessagingMetrics ___, RequestMetrics ____) : IHostedService
{
    // Resolving the parameters is the entire job: constructing them registers
    // the instruments with their meters.
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
```

**The test for membership is not "is it a gauge".** It is *"can this service run
for an hour without constructing it"* — and for every metrics type in this
document the answer is yes, which is why all four are here.

That includes `RequestMetrics`, and the reasoning that nearly excluded it is
worth keeping as the worked example. `LoggingBehavior` injects it, a behaviour
runs on every dispatched request, and it is tempting to conclude that any live
service has therefore constructed it. It has not. `IPipelineBehavior` runs for
what `IDispatcher` handles; a health probe is mapped by `MapHealthChecks`
(§13.5) and never enters the pipeline. A canary before cutover, a replica behind
a rate limiter, or a service whose traffic has simply stopped all publish
nothing — and **Notifications and Shipping have no public API at all** (§3.2),
so on those two the instrument would never exist under any circumstances.

`OrderMetrics` is the second worked example, and a different failure. It used to
be injected by `PlaceOrderHandler`, which constructed it on the first command;
moving the counters to the projection (§6.6) moved it into this list without
anybody editing this list. **A constructor parameter is a dependency on a call
site that may move**, which is why the rule is about reachability and not about
instrument type.

Reachability is not decidable from a type, so no test can assert the rule as
stated. What a test *can* do is refuse to let a metrics type appear without
somebody deciding, and make the decision the thing under review:

```csharp
// Types deliberately not forced, each with the reason it does not need to be.
// Empty today, and that is the point: a name lands here only when someone
// argues it in a pull request.
private static readonly Dictionary<Type, string> NotForced = new();

[Fact]
public void Every_metrics_type_is_forced_or_has_a_stated_reason_not_to_be()
{
    // The COLLECTION, not a built provider. IServiceCollection is the input to
    // BuildServiceProvider and is not itself a registered service, so asking a
    // provider for one throws — registrations cannot be enumerated after the
    // build. BuildServices() stops one step earlier than BuildProvider().
    //
    // It runs BOTH helpers, which matters here and nowhere else: the four
    // types are split across AddOrderingApplication (OrderMetrics,
    // RequestMetrics) and AddOrderingInfrastructure (OutboxMetrics,
    // MessagingMetrics). A helper that ran only the Application half would see
    // two of four and fail against a correct MetricsInitialiser — the test
    // reporting a defect in the thing it is guarding.
    var registered = BuildServices()
        .Select(d => d.ServiceType)
        .Where(t => t.Name.EndsWith("Metrics"))
        .Distinct();

    var forced = typeof(MetricsInitialiser)
        .GetConstructors().Single()
        .GetParameters().Select(p => p.ParameterType).ToHashSet();

    // Both directions. Unforced-and-unexplained is the drift this exists for;
    // forced-but-unregistered is a host that will not start.
    registered.Where(t => !forced.Contains(t) && !NotForced.ContainsKey(t))
              .ShouldBeEmpty("add it to MetricsInitialiser, or to NotForced with a reason");

    forced.ShouldBeSubsetOf(registered);
}
```

> **The naming filter is a heuristic, and it is worth being honest about which
> way it fails.** `EndsWith("Metrics")` is how the test finds candidates, so a
> metrics type named something else is invisible to it — a false negative, and
> the same silent gap the test was written to close. It never produces a false
> positive that forces a wrong decision, because `NotForced` is the escape
> hatch: a type that genuinely does not need forcing gets a line and a reason
> rather than a spurious constructor parameter.
>
> That asymmetry is deliberate. A convention test that can *block* a correct
> design gets disabled within a month; one that can only miss something is a
> net gain, and the missed case is caught by the same review that named the
> type.

`lane` is a two-value tag, so this respects the cardinality rule in §13.3.

> **An alert has three parts: a condition, a signal and a procedure.** §13.9
> pairs conditions with procedures in both directions. This is the third leg —
> every condition above resolves to instruments on a meter §13.2 registers.
> Two of the alerts in this document were written against signals that did not
> exist, and both looked correct: the dashboard is empty either way, whether
> the system is healthy or the metric was never published.
>
> Where a condition is **derived** rather than measured — the cache hit ratio
> is computed from HybridCache's hit and miss counters, not published as a
> ratio — write the expression, not an invented metric name. A name that looks
> like an instrument and is not is the hardest version of this to spot.

### 13.7 Starting SLOs

Alert thresholds without targets are arbitrary. These are **starting points** to
be replaced by measured behaviour within the first month — publishing them
matters more than their initial accuracy, because they make "is this slow?" a
question with an answer.

**Every row names the instrument it reads**, for the reason §13.2 gives: a
target whose signal is not registered cannot be measured, and reads as
satisfied. A row that cannot name one does not belong in the table.

| Metric | Target | Signal |
|---|---|---|
| Command p95 (single aggregate, excl. external calls) | < 100 ms | `request.duration`, `request` tag on a command type (§13.3) |
| Query p95 | < 80 ms | `request.duration`, `request` tag on a query type |
| Event end-to-end p95 (publish → consumer start) | < 2 s | `messaging.delivery.lag` |
| Outbox oldest unprocessed, **broker lane**, p99 | < 5 s | `outbox.oldest.age`, `lane` tag (§13.6) |
| Outbox oldest unprocessed, **local lane**, p99 | < 1 s | same gauge, other lane |
| Read-model staleness (event raised → projection applied), p99 | < 1 s | `projection.lag` |
| Availability, per service | 99.9% monthly | `http.server.request.duration`, ASP.NET Core instrumentation |

Two rows were removed rather than left unmeasurable. **Gateway added latency**
would need the gateway's own duration minus the backend's, correlated per
request — no single instrument produces it, and the number that was published
here could only ever have been guessed at. **Query p95 split by cache hit and
miss** needed a tag no query handler sets; the cache's own hit ratio (§13.6,
from the `Microsoft.Extensions.Caching.Hybrid` meter) answers the question the
split was really asking, which is whether the cache is working.

Cutting a row is the honest move when the alternative is a target nobody can
compute. An SLO that cannot be evaluated is not a weak SLO — it is a claim that
the service is meeting a bar nobody is checking.

Verify order-of-magnitude with the **k6 or NBomber SLO run against staging**
(§15.1) — the load run in CD, which asserts the targets in this table and is the
first real gate after the dev deploy. Not a "smoke test": §15.1 declines to have
one and §12.1 gives the reason, which is that a stage named for what it actually
does gets maintained. This is also not a capacity test — it catches the
regression where a query loses its index and goes from 40 ms to 4 s, which no
unit test will find.

### 13.8 Ownership

| Artefact | Owner |
|---|---|
| Golden-signal dashboards (RED, saturation) | Platform |
| Business metric dashboards | The service team |
| Gateway 5xx, infrastructure alerts, **broker-lane** outbox stalls (usually a shared-broker fault) | Platform |
| Own p95, **local-lane** outbox stalls and projection lag, abandoned rows, consumer failures | The service team |

Dashboards are **code**, checked into `deploy/observability/` as Grafana JSON or
equivalent. A dashboard clicked together in a UI is lost with the instance and
cannot be reviewed.

### 13.9 Runbooks

Every alert links to a runbook. An alert that fires at 03:00 with no procedure
attached is a page to somebody who will have to reason from scratch.

| Runbook | Covers |
|---|---|
| `docs/runbooks/error-rate.md` | Triaging a 5xx spike: which service, which endpoint, correlating to a deploy or a dependency |
| `docs/runbooks/latency.md` | p99 regression: reading the trace waterfall, the usual suspects — a lost index, a cold cache, a slow peer |
| `docs/runbooks/business-volume.md` | Orders stopped: checking the gateway, auth, the outbox and the client before assuming it is real demand |
| `docs/runbooks/error-queue.md` | Inspecting a poison message, deciding replay vs discard, replaying safely |
| `docs/runbooks/outbox-broker.md` | Broker lane stalled: checking RabbitMQ reachability, credentials, queue limits; what downstream services are missing while it is stopped |
| `docs/runbooks/projection-lag.md` | Local lane stalled: finding the throwing handler, deciding whether to serve from the write model meanwhile, replaying a projection from scratch |
| `docs/runbooks/outbox-growth.md` | Total backlog rising: dispatcher liveness, batch sizing, retention purge failure |
| `docs/runbooks/outbox-abandoned.md` | Rows past the attempt cap: reading the payload and `LastError`, deciding repair vs discard, resetting `Attempts` to replay |
| `docs/runbooks/stuck-saga.md` | Finding unfinalised sagas, reading their state, manual compensation |
| `docs/runbooks/order-review.md` | Working the `OrderReviews` queue: what each reason code means, how to resolve it, and deleting the row when done |
| `docs/runbooks/migration-failure.md` | A migration job that failed mid-deploy, and how to roll forward |
| `docs/runbooks/redis-cold.md` | Cache-loss load spike on the databases, and how to shed load while it warms |

Write each one when the corresponding alert is created, not after it first
fires.

---

## 14. Local development

### 14.1 Docker Compose — the baseline

One command starts the platform. This is the documented default; it requires
only Docker and works identically on every operating system and in CI.

```yaml
# deploy/compose/docker-compose.yml
name: commerce

services:
  sql:
    image: mcr.microsoft.com/mssql/server:2022-latest
    environment:
      ACCEPT_EULA: "Y"
      MSSQL_SA_PASSWORD: "${SQL_PASSWORD:-Local_Dev_Pa55w0rd!}"
      MSSQL_PID: Developer
    ports: [ "1433:1433" ]
    volumes: [ sql-data:/var/opt/mssql ]
    healthcheck:
      test: ["CMD-SHELL", "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$$MSSQL_SA_PASSWORD\" -C -Q 'SELECT 1'"]
      interval: 10s
      timeout: 5s
      retries: 10
      start_period: 30s

  # Two Redis instances, because eviction policy cannot be shared — §8.1.
  redis-cache:
    image: redis:7-alpine
    command: redis-server --maxmemory 256mb --maxmemory-policy allkeys-lru
    ports: [ "6379:6379" ]
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]

  redis-coordination:
    image: redis:7-alpine
    # noeviction: locks, idempotency keys and the denylist must never be
    # evicted. Appendonly so a restart does not silently release held locks.
    command: redis-server --appendonly yes --maxmemory 128mb --maxmemory-policy noeviction
    ports: [ "6380:6379" ]
    volumes: [ redis-coordination-data:/data ]
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 10s
      retries: 5

  rabbitmq:
    image: rabbitmq:4-management-alpine
    ports: [ "5672:5672", "15672:15672" ]
    volumes: [ rabbit-data:/var/lib/rabbitmq ]
    healthcheck:
      test: ["CMD", "rabbitmq-diagnostics", "check_running"]
      interval: 10s
      retries: 5

  keycloak:
    image: quay.io/keycloak/keycloak:26.0
    command: start-dev --import-realm
    environment:
      KC_BOOTSTRAP_ADMIN_USERNAME: admin
      KC_BOOTSTRAP_ADMIN_PASSWORD: admin
    ports: [ "8080:8080" ]
    volumes: [ ./keycloak/realm-export.json:/opt/keycloak/data/import/realm.json:ro ]

  otel-collector:
    image: otel/opentelemetry-collector-contrib:latest
    command: [ "--config=/etc/otel/config.yaml" ]
    volumes: [ ./otel/config.yaml:/etc/otel/config.yaml:ro ]
    ports: [ "4317:4317", "4318:4318" ]

  grafana:
    image: grafana/otel-lgtm:latest
    ports: [ "3000:3000" ]

  # ---- Application services ----

  ordering-migrator:
    build:
      context: ../..
      dockerfile: src/Services/Ordering/Ordering.Migrator/Dockerfile
    environment:
      # Migrator identity (DDL) — §7.1. Locally these differ only by SQL login;
      # in production they are separate secrets on separate workloads.
      ConnectionStrings__OrderingMigrator: "${ORDERING_MIGRATOR_CONNECTION}"
    depends_on:
      sql: { condition: service_healthy }
    restart: "no"

  ordering-api:
    build:
      context: ../..
      dockerfile: src/Services/Ordering/Ordering.Api/Dockerfile
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      # Runtime identity (DML only) — never the migrator connection.
      ConnectionStrings__Ordering: "${ORDERING_CONNECTION}"
      ConnectionStrings__RedisCache: "redis-cache:6379"
      ConnectionStrings__RedisCoordination: "redis-coordination:6379"
      ConnectionStrings__RabbitMq: "amqp://guest:guest@rabbitmq:5672"
      # The authority, to validate inbound tokens (§11.2). No Identity__Client__*:
      # Ordering calls no peer synchronously — prices come from a local
      # projection (§6.4) and the rest goes over the broker. Only the BFF holds
      # client credentials (§9.7, §11.5).
      Identity__Authority: "http://keycloak:8080/realms/commerce"
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"
    ports: [ "5101:8080" ]
    depends_on:
      ordering-migrator:  { condition: service_completed_successfully }
      redis-cache:        { condition: service_healthy }
      redis-coordination: { condition: service_healthy }
      rabbitmq:           { condition: service_healthy }

  # catalog-api, inventory-api, payments-api, shipping-worker and
  # notifications-worker follow the same shape — and "the same shape" is a
  # PAIR: a {service}-migrator one-shot plus the service itself gated on
  # `condition: service_completed_successfully`. Every service owns a database
  # and none may migrate at startup (§4.1, ADR-007), so a service added here
  # without its migrator starts against an empty schema.

  gateway:
    build:
      context: ../..
      dockerfile: src/Gateway/Gateway.Api/Dockerfile
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      # The authority, because the gateway validates JWTs like every other host
      # (§11.2). NOT Identity__Client__* — those are for calling other services
      # (§11.5), and the gateway calls nobody: YARP forwards the caller's token.
      Identity__Authority: "http://keycloak:8080/realms/commerce"
      # Locally the gateway IS the edge: nothing forwards, RemoteIpAddress is
      # already the client, and trusting X-Forwarded-For would let any caller
      # pick its own rate-limit bucket. In Kubernetes this is true (§15.3).
      Ingress__Enabled: "false"
      # Browsers hit the gateway directly in dev.
      Cors__Enabled: "true"
      Cors__Origins__0: "http://localhost:3000"
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"
    ports: [ "5000:8080" ]
    depends_on:
      keycloak: { condition: service_started }
      ordering-api: { condition: service_started }

  # The one host with client credentials, because it is the one host that calls
  # a peer synchronously (§9.7). Everything else here has the authority only.
  # Named web-bff, matching the Aspire resource (§14.2) and the YARP
  # destination (§10.2) — the gateway resolves the destination by hostname, so
  # the container name IS the routing configuration.
  web-bff:
    build:
      context: ../..
      dockerfile: src/BFF/Web.Bff/Dockerfile
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      Identity__Authority: "http://keycloak:8080/realms/commerce"
      # Required by ValidateOnStart (§15.4) — this host refuses to boot
      # without them. Local values only; production mounts a secret.
      Identity__Client__ClientId: "web-bff"
      Identity__Client__ClientSecret: "${BFF_CLIENT_SECRET:-local-dev-secret}"
      Identity__Client__Scope: "commerce-api"
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"
    ports: [ "5200:8080" ]
    depends_on:
      # Keycloak only. catalog-api is elided from this file (see the comment
      # above the gateway), and Compose rejects a dependency on a service it
      # cannot see — one undefined name fails the whole `up`, not one service.
      keycloak: { condition: service_started }

volumes:
  sql-data:
  redis-coordination-data:
  rabbit-data:
```

```bash
docker compose -f deploy/compose/docker-compose.yml up -d
```

| Endpoint | URL |
|---|---|
| Gateway | http://localhost:5000 |
| Keycloak | http://localhost:8080 (admin/admin) |
| RabbitMQ management | http://localhost:15672 (guest/guest) |
| Grafana | http://localhost:3000 |

An override file runs infrastructure in containers while services run on the
host with a debugger attached — the usual inner-loop compromise:

```bash
docker compose -f docker-compose.yml -f docker-compose.infra-only.yml up -d
dotnet run --project src/Services/Ordering/Ordering.Api
```

### 14.2 Aspire — optional accelerator

Aspire (MIT, Microsoft) replaces the Compose file with a C# program that starts
containers *and* your projects, injects connection strings and service discovery
automatically, and ships an OpenTelemetry dashboard with distributed tracing
already wired.

The practical difference is the inner loop: Compose containerises your services,
so debugging seven of them at once is awkward. Aspire runs your projects as host
processes while containerising only the infrastructure, so a single F5 gives
breakpoints across every service simultaneously.

```csharp
// src/AppHost/Program.cs
var builder = DistributedApplication.CreateBuilder(args);

// Resource names ARE connection-string names: WithReference(x) injects
// ConnectionStrings__{x.Name}. They must match the keys the code reads
// (§4.2, §8.2) exactly — configuration is case-insensitive but not
// punctuation-insensitive, so "redis-cache" would not satisfy
// GetConnectionString("RedisCache") and both Redis connections would be null.
var sql = builder.AddSqlServer("sql").WithDataVolume();

// Two Redis resources, mirroring §8.1 — the eviction policies are
// incompatible, so a single instance would silently evict held locks.
var cache = builder.AddRedis("RedisCache")
                   .WithRedisCommander();
var coordination = builder.AddRedis("RedisCoordination")
                          .WithDataVolume()       // locks must survive a restart
                          .WithPersistence();

var mq = builder.AddRabbitMQ("RabbitMq").WithManagementPlugin();

// One database per service that this AppHost runs. Inventory, Payments,
// Shipping and Notifications are omitted deliberately — adding a database
// without the service and migrator that own it creates a schema nothing
// maintains, which is the shape §4.1 rules out.
var orderingDb = sql.AddDatabase("Ordering");
var catalogDb  = sql.AddDatabase("Catalog");

var keycloak = builder.AddKeycloak("keycloak", 8080)
                      .WithRealmImport("./keycloak/realm-export.json");

// ReferenceExpression, not string concatenation: GetEndpoint() returns a
// deferred reference — the port is not allocated yet. Concatenating it with +
// would stringify the object and write a placeholder into the environment.
var authority = ReferenceExpression.Create(
    $"{keycloak.GetEndpoint("http")}/realms/commerce");

// Every host validates JWTs (§11.2), so every host needs the authority.
// Applied by one helper rather than repeated per resource — the previous
// version configured ordering-api and left catalog and the gateway with a null
// authority, which fails only at first request.
//
// Client credentials are a SEPARATE concern with a narrower audience: only a
// host that calls another service (§11.5) presents them. Passing a clientId to
// a host that makes no outbound call provisions a Keycloak client, prompts for
// a secret and mounts it, all for credentials nothing ever sends.
IResourceBuilder<ProjectResource> WithPlatformIdentity(
    IResourceBuilder<ProjectResource> project, string? callerClientId = null)
{
    project = project
        .WithEnvironment("Identity__Authority", authority)
        .WaitFor(keycloak);

    if (callerClientId is null) return project;

    return project
        .WithEnvironment("Identity__Client__ClientId", callerClientId)
        // One secret per client, not one shared across all of them: Keycloak
        // issues distinct credentials, and a shared secret would let any
        // service present itself as any other (§11.5). Prompted once and
        // stored in user secrets; in Kubernetes each is its own Secret (§15.4).
        .WithEnvironment("Identity__Client__ClientSecret",
            builder.AddParameter($"{callerClientId}-secret", secret: true))
        .WithEnvironment("Identity__Client__Scope", "commerce-api");
}

// Migrations run as a job here exactly as they do in Compose and Helm —
// ADR-007 forbids migrating at application startup, so without this the
// schema is never created and every service fails on its first query.
// One per service, because one database per service (§7.1).
var orderingMigrator = builder.AddProject<Projects.Ordering_Migrator>("ordering-migrator")
    .WithReference(orderingDb, connectionName: "OrderingMigrator")
    .WaitFor(sql);

var catalogMigrator = builder.AddProject<Projects.Catalog_Migrator>("catalog-migrator")
    .WithReference(catalogDb, connectionName: "CatalogMigrator")
    .WaitFor(sql);

var ordering = WithPlatformIdentity(
    builder.AddProject<Projects.Ordering_Api>("ordering-api")
        .WithReference(orderingDb).WaitFor(orderingDb)
        .WithReference(cache)          // → ConnectionStrings:RedisCache
        .WithReference(coordination)   // → ConnectionStrings:RedisCoordination
        .WithReference(mq).WaitFor(mq)
        // Gate on the migrator completing, not merely starting — the Compose
        // equivalent is service_completed_successfully (§14.1).
        .WaitForCompletion(orderingMigrator)
        .WithHttpHealthCheck("/health/ready"));   // authority only — no peer calls

var catalog = WithPlatformIdentity(
    builder.AddProject<Projects.Catalog_Api>("catalog-api")
        .WithReference(catalogDb).WaitFor(catalogDb)
        .WithReference(cache)
        .WithReference(coordination)
        .WithReference(mq)
        .WaitForCompletion(catalogMigrator)
        .WithHttpHealthCheck("/health/ready"));

// The gateway validates JWTs too (§11.2) — it is the component most visible
// when the authority is missing, and was the one previously left without it.
// No callerClientId: YARP forwards the caller's token rather than minting one
// of its own, so there is no "gateway" Keycloak client and no gateway secret.
WithPlatformIdentity(
    builder.AddProject<Projects.Gateway_Api>("gateway")
        .WithReference(ordering)
        .WithReference(catalog)
        // The same edge shape Compose declares (§14.1), for the same reason:
        // locally the gateway IS the edge, and browsers reach it directly.
        // Diverging here would make a SPA work under one local path and fail
        // under the other.
        .WithEnvironment("Ingress__Enabled", "false")
        .WithEnvironment("Cors__Enabled", "true")
        .WithEnvironment("Cors__Origins__0", "http://localhost:3000")
        // /health/ready, like every other resource and like the chart in
        // §15.3 — an empty readiness set is still the right question here,
        // and probing liveness instead would make the gateway the one
        // component whose local gate differs from its deployed one.
        .WithHttpHealthCheck("/health/ready")
        .WithExternalHttpEndpoints());

// The only resource with a callerClientId, matching Compose (§14.1): the BFF
// is the only host that calls a peer synchronously (§9.7). If a second one
// ever appears, ADR-017's hop budget is the thing to check first.
WithPlatformIdentity(
    builder.AddProject<Projects.Web_Bff>("web-bff")
        .WithReference(catalog)
        .WithHttpHealthCheck("/health/ready"),
    callerClientId: "web-bff");

builder.Build().Run();
```

```bash
aspire run
```

Matching the names is what makes the two local paths interchangeable: §14.1's
Compose sets `ConnectionStrings__RedisCache` by hand, Aspire derives the same
key from the resource name, and the service reads one key either way. A
mismatch here breaks only the Aspire path — which nothing in CI exercises, so it
would surface as "Aspire doesn't work on my machine" rather than as a defect.

Two deliberate local simplifications, both matching what the Compose and test
environments already do:

- **`OrderingMigrator` points at the same SQL login as the runtime connection.**
  §7.1's two identities are a production control; locally there is one `sa`
  account, exactly as §12.4's fixture notes. The *key* still differs, so the
  migrator reads the name it will read in production.
- **The client secret is an Aspire parameter**, prompted once and stored in user
  secrets, rather than a value in the AppHost. It is the same obligation §15.4
  records — a required setting needs a source in every environment — met a third
  way.

> **The AppHost is a deployment environment, and drifts like one.** It is the
> only one with no automated exercise: Compose runs in CI, Helm is applied by
> CD, and the integration fixture builds its own. Every configuration change
> lands in three places and can be forgotten in the fourth without anything
> failing. When a required key is added (§15.4), this file is the one to check
> last and the one most likely to be wrong.

**The escape hatch.** Adding one line emits a Compose file from the same model,
so the Aspire dependency is reversible:

```csharp
builder.AddDockerComposeEnvironment("compose");
```

```bash
aspire publish   # writes docker-compose.yaml to ./aspire-output
```

**What adopting Aspire costs.** The AppHost becomes the source of truth for
topology, so the team must learn its model. The API surface has moved quickly —
four major versions in roughly fifteen months. And it is a visible Microsoft
tooling opinion in an otherwise portable stack.

**What removing it costs.** Aspire is not in the production request path;
deployed containers are plain ASP.NET Core. The coupling is four things: the
AppHost project (delete it), `Common.Web` (your own code — keep it, swapping
only service discovery for DNS), the `Aspire.*` client integration packages (one
line per resource per service reverts to standard registration), and the
connection-string environment variable conventions (reproduce them in Compose).
For a platform this size, roughly one to three days of mechanical work.

> **Decision** — Compose is the documented baseline; Aspire is offered as an
> optional accelerator. See [ADR-011](#adr-011--compose-baseline-aspire-optional).

### 14.3 Seed data

Seeding runs from the migrator container, is idempotent, and is
development-only. It should produce enough data to exercise pagination and
caching — a catalogue of three products hides every performance problem you
have.

---

## 15. CI/CD and deployment

### 15.1 Pipeline

```mermaid
graph LR
    PR[Pull request] --> SEC[SCA + secret scan]
    SEC --> K{What changed?}
    K -->|source| B[Build + analyse]
    B --> UT[Unit tests]
    UT --> IT[Integration tests<br/>Testcontainers]
    IT --> IMG[Build + sign images<br/>api + migrator, same tag]
    IMG --> DEV[Deploy: dev]
    K -->|charts only| TAG[Resolve running tag]
    TAG --> DEV
    DEV --> STG[Deploy: staging]
    STG --> SLO[k6 SLO run<br/>staging]
    SLO --> APP{Manual approval}
    APP --> PRD[Deploy: production<br/>canary]
```

**Scanning runs before the fork, not after the build.** It sat downstream of
the image build, which put `deploy/**` — the directory most likely to receive a
pasted credential — on the only path that skipped it. Neither half needs a
build to run: the secret scan reads the diff, and the licence gate reads
`Directory.Packages.props`, which is the practical argument for central pinning
that §4.4 makes on other grounds. Cheapest and least dependent goes first.

Only services whose files changed are built and deployed. Path filters are what
make a monorepo practical at this size:

```yaml
- name: Detect changed services
  id: changes
  uses: dorny/paths-filter@v3
  with:
    filters: |
      # Inputs shared by every service, including the three repo-root files.
      # A version bump in Directory.Packages.props changes every binary the
      # pipeline produces (§4.4) and matches no service path — without these
      # lines, the one change the pin file exists to control is the one change
      # CI never rebuilds or retests.
      shared: &shared
        - 'Directory.Build.props'
        - 'Directory.Packages.props'
        - 'global.json'
        - 'src/BuildingBlocks/**'
        # The contract suite (§12.6) guards compatibility BETWEEN services, so
        # it belongs to all of them. Owned by none, it would run for none.
        - 'tests/Platform.*/**'
      ordering:
        - *shared
        - 'src/Services/Ordering/**'
        - 'tests/Ordering.*/**'
      catalog:
        - *shared
        - 'src/Services/Catalog/**'
        - 'tests/Catalog.*/**'
      # inventory, payments, shipping and notifications repeat those three
      # lines. Every service has an entry — the list is exhaustive by
      # construction, which is the whole point of the check below, so an
      # elision here is a formatting choice and never a missing filter.
      # The gateway is a deployable like any other — its own image (§15.2),
      # its own chart (§15.3), its own Program.cs and route file. Left out of
      # this list it is the one component whose route configuration can drop a
      # route silently (§10.2) and never be rebuilt to find out.
      gateway:
        - *shared
        - 'src/Gateway/**'
      # The BFF is a deployable too, with its own image, chart and the
      # platform's only client secret.
      bff:
        - *shared
        - 'src/BFF/**'
      # A chart or values change produces no new image and must still reach
      # the cluster. See below — this path needs a tag it did not build.
      deploy:
        - 'deploy/**'
```

**A filter list is a deployable inventory, and it drifts the way inventories
do.** Every path under `src/` must be matched by **some** filter — the
deployables (`Gateway`, `BFF`, and each directory under `Services/`) by their
own, and `BuildingBlocks` by `shared`, which is the anchor every service
inherits rather than a filter of its own. Everything under `deploy/` is matched
by `deploy`: charts are deliberately not attached to a service, because a chart
change deploys without building and takes the second path through the pipeline.

The check is one line of CI and worth more than the convention it replaces:
assert that every immediate child of `src/`, and every immediate child of
`src/Services/`, appears in at least one filter — and fail on the one that does
not. Both halves are needed, because the two failures look nothing alike. A
missing top-level entry is what left `src/Gateway/**` and `src/BFF/**`
unfiltered, both deployables that CI never rebuilt. A missing entry *under*
`Services/` is quieter still: the parent directory is spoken for by its
siblings' filters, so the inventory looks complete right up until that one
service stops being deployed.

`tests/Ordering.*/**` covers `Ordering.TestSupport` as well as the three test
projects, so a service's test helpers belong to that service and not to
`shared`. A sibling's fixtures are not something Ordering compiles against, and
putting them in `shared` would redeploy every service whenever anyone touched
Catalog's test data builders.

**There is no smoke stage after the dev deploy**, for the reason E2E is absent
from §12.1: a gate nobody has defined is a gate that gets configured to pass.
The readiness probes (§13.5) already gate the rollout — a pod that fails
`/health/ready` never takes traffic — so a separate "smoke test" step would
re-assert what Kubernetes has already enforced, or assert something nobody has
written down. The first real gate after dev is the k6 SLO run against staging,
which names its tool, its target and its assertions (§13.7).

`BuildingBlocks` appears under every service, so a change there rebuilds
everything. That is correct, and it is also the reason to keep those projects
small.

**The filter answers a narrower question than the pipeline asks.** "Which
service's source changed" is not "what must be rebuilt, retested and
redeployed" — the two diverge for every file that is not under
`src/Services/<name>/`, which is precisely the set §4.4 spends a section
arguing for. The rule that keeps them aligned: **if changing a file can change
what a service ships, that file belongs in that service's filter.**

#### A config-only deploy needs a tag it did not build

The `deploy` filter fires on a chart or values change, which skips the image
build — and `helm upgrade` then has no tag to pass. Left to the chart default
it would resolve to `image.tag: ""` (§15.3) and roll whatever that means, which
is a version nobody chose in a job nobody thought was a release.

The tag already in the cluster is the only correct answer, so read it back:

```yaml
- name: Resolve the running tag
  if: steps.changes.outputs.deploy == 'true' && steps.changes.outputs.ordering == 'false'
  run: |
    TAG=$(helm get values ordering -n "$NAMESPACE" -o json | jq -r '.image.tag')
    # Fail rather than default. A config deploy that cannot say which image is
    # running is a config deploy that must not proceed.
    [ -n "$TAG" ] && [ "$TAG" != "null" ] || exit 1
    echo "IMAGE_TAG=$TAG" >> "$GITHUB_ENV"
```

The invariant is worth stating because it is easy to lose: **a config-only
deploy must not change the running image.** It goes through the same
`helm upgrade`, the same canary (§15.5) and the same migration hook (§7.4) — the
hook is a no-op when the migrator image and its migrations are unchanged, which
is exactly why the hook has to be idempotent rather than merely correct once.

### 15.2 Container images

```dockerfile
# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Restore in its own layer so it caches across source-only changes.
# global.json first among equals: sdk:10.0-noble is a floating tag, so without
# it this build — the only one whose output ships — compiles under whatever SDK
# the base image carries that week, while developers and CI are pinned (§4.4).
# Copied in, a mismatch is a restore error rather than different analysers.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Ordering/ src/Services/Ordering/
RUN dotnet restore src/Services/Ordering/Ordering.Api/Ordering.Api.csproj

RUN dotnet publish src/Services/Ordering/Ordering.Api/Ordering.Api.csproj \
    -c $BUILD_CONFIGURATION -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app/publish .

# Chiselled images ship no shell and run as a non-root user by default.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Ordering.Api.dll"]
```

Chiselled base images contain no package manager and no shell, which removes
most of the CVE surface that routine scans would otherwise report. The trade-off
is that `kubectl exec` into a running container gives you nothing — debugging
uses ephemeral debug containers instead.

#### Every service builds two images

The migration job (§7.4) is a Helm `pre-upgrade` hook, so its image must exist
**before** the deploy that uses it. A pipeline that builds only the API image
fails at the first step of every release, pulling a tag CI never pushed.

```dockerfile
# src/Services/Ordering/Ordering.Migrator/Dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Ordering/ src/Services/Ordering/
# Restore as its own layer, same as the API image — it caches across
# source-only changes, which is most changes.
RUN dotnet restore src/Services/Ordering/Ordering.Migrator/Ordering.Migrator.csproj

RUN dotnet publish src/Services/Ordering/Ordering.Migrator/Ordering.Migrator.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

# Runtime, not aspnet — the migrator has no listener.
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Ordering.Migrator.dll"]
```

```yaml
# Both images build from the same commit and share the tag Helm resolves.
- name: Build and push
  run: |
    for target in api migrator; do
      docker buildx build \
        --file "src/Services/Ordering/Ordering.${target^}/Dockerfile" \
        --tag "${REGISTRY}/ordering-${target}:${GIT_SHA}" \
        --push .
    done
```

Both images carry the **same tag**, which is what lets `values.yaml` hold one
`image.tag` and the Helm hook interpolate it into the migrator reference
(§7.4). A migrator built from a different commit than the API it precedes is
the exact failure the pre-upgrade hook exists to prevent.

### 15.3 Deployment

Each service gets a Helm chart; an umbrella chart deploys the platform.

```yaml
# deploy/helm/ordering/values.yaml
replicaCount: 3

image:
  # Registry namespace only. Each workload appends its own name, so the chart
  # can reference both the API and the migrator (§7.4) from one tag.
  registry: registry.example.com/commerce
  api: ordering-api
  migrator: ordering-migrator
  # Supplied by CI, never "latest"; both images share it. Deliberately empty
  # rather than a default: a deploy that cannot name its tag must fail, not
  # roll something. A config-only deploy reads the running value back
  # (§15.1) instead of falling through to this.
  tag: ""
  pullPolicy: IfNotPresent

resources:
  requests: { cpu: 100m, memory: 256Mi }
  limits:   { memory: 512Mi }          # No CPU limit — see note below.

autoscaling:
  enabled: true
  minReplicas: 3
  maxReplicas: 20
  targetCPUUtilizationPercentage: 70

podDisruptionBudget:
  enabled: true
  minAvailable: 2

probes:
  liveness:  { path: /health/live,  initialDelaySeconds: 10, periodSeconds: 10 }
  readiness: { path: /health/ready, initialDelaySeconds: 5,  periodSeconds: 5 }
  startup:   { path: /health/startup, failureThreshold: 30,  periodSeconds: 2 }

identity:
  # The authority, to validate incoming JWTs (§11.2) — and nothing else.
  # Identity:Client is what a host presents when it CALLS a peer (§11.5), and
  # Ordering calls none: prices come from a local projection (§6.4) and the
  # rest goes over the broker. No clientId here means no Keycloak client, no
  # secret in the vault and nothing to rotate.
  authority: https://id.example.com/realms/commerce
```

**Shipping and Notifications get the same chart minus the Service and the
Ingress.** They consume from the broker and expose no API, so their only
listener is the health endpoint §13.5 requires — which is a reason to keep
Kestrel bound and no reason at all to route to it. The probes address the pod
directly, because kubelet reaches a container port without a Service in front
of it, and telemetry is pushed to the collector rather than scraped (§13.2), so
nothing else needs a stable name for these pods either:

```yaml
# deploy/helm/shipping/values.yaml — the two keys that are the whole difference
service:
  enabled: false
ingress:
  enabled: false
```

Both are `false` rather than absent, so the diff against Ordering's chart shows
the decision instead of hiding it in what was deleted.

> The failure to design against is not an attacker finding a worker's `/health`.
> It is a well-meaning `helm` values copy that keeps `ingress.enabled: true`
> because it came from Ordering's chart, and publishes a host with no
> authentication middleware in front of it — because a service with no public
> API never needed any. **A worker's safety comes from having no route, so the
> absence of a route is the thing to assert** — which is why it is written down
> as `false` above rather than left out. A key that is missing looks the same
> whether it was considered or forgotten.

Exactly one chart in the platform carries client credentials, and the asymmetry
is the design rather than an oversight:

```yaml
# deploy/helm/web-bff/values.yaml — the only chart with an Identity:Client
identity:
  authority: https://id.example.com/realms/commerce
  # Required by ValidateOnStart (§15.4): this host does call a peer (§9.7).
  # The secret is a reference, never a value.
  clientId: web-bff
  scope: commerce-api
  clientSecretRef:
    name: web-bff-identity
    key: client-secret
```

> **A second chart growing an `identity.clientId` is a design change, not a
> configuration change.** It means a host started calling a peer synchronously,
> which is ADR-017's budget being spent — so the review question is not "does
> the secret exist" but "why is this call not an event".

The gateway's chart is not a service chart with the database parts deleted. It
has no migrator, no client credentials, and two keys no service has — and every
one of those differences is something it will not start without, or will start
wrongly without:

```yaml
# deploy/helm/gateway/values.yaml
replicaCount: 3

image:
  registry: registry.example.com/commerce
  api: gateway
  tag: ""
  # No migrator key: the gateway owns no database (§10.1), so the umbrella
  # chart's pre-upgrade hook (§7.4) has nothing to run for it.

resources:
  requests: { cpu: 200m, memory: 128Mi }
  limits:   { memory: 256Mi }

autoscaling:
  enabled: true
  minReplicas: 3
  maxReplicas: 30          # every external request passes through here
  targetCPUUtilizationPercentage: 70

podDisruptionBudget:
  enabled: true
  minAvailable: 2

probes:
  # The same three §13.5 defines, and the gateway needs them stated as much as
  # any service: MapCommonHealthEndpoints exposes the endpoints, and a chart
  # that never references them means nothing asks. Readiness is honest here
  # even though the set is empty (§4.2) — "the process is up" is exactly the
  # question, because the gateway owns no dependency to be un-ready for.
  liveness:  { path: /health/live,  initialDelaySeconds: 10, periodSeconds: 10 }
  readiness: { path: /health/ready, initialDelaySeconds: 5,  periodSeconds: 5 }
  startup:   { path: /health/startup, failureThreshold: 30,  periodSeconds: 2 }

identity:
  # Authority only. The gateway validates JWTs (§11.2) but calls nobody —
  # YARP forwards the caller's token — so there is no clientSecretRef here
  # and no gateway entry in External Secrets (§11.5, §15.4).
  authority: https://id.example.com/realms/commerce

ingress:
  # True in every Kubernetes environment: TLS terminates at the load balancer
  # or Ingress (§10.1), so RemoteIpAddress is the ingress on every request
  # until UseForwardedHeaders runs.
  enabled: true
  # Mandatory once enabled — GetRequiredSection, so a missing value is a
  # refusal to boot rather than a rate limiter that meters the ingress
  # controller as its only client. These are the ingress controller's pod
  # CIDRs, not the cluster's: anything trusted here can set X-Forwarded-For.
  trustedNetworks:
    - 10.42.0.0/16

cors:
  # Off. Browsers reach the platform through the CDN on the same origin
  # (§10.2), so no preflight ever arrives. Off is a complete configuration;
  # on without origins is not (§15.4).
  enabled: false
  # origins: [ "https://shop.example.com" ]  # becomes mandatory the moment
  # enabled flips to true — GetRequiredSection, so the chart fails the pod
  # rather than serving a policy that rejects every browser.
```

`ingress.enabled: true` in Kubernetes and `Ingress__Enabled: "false"` in Compose
(§14.1) are not an inconsistency to reconcile — they are the same setting
correctly describing two different topologies, which is why it is a value and
not a constant.

Setting a **memory limit but no CPU limit** is deliberate. Memory is
incompressible — a leak must be bounded or it takes down the node. CPU is
compressible, and a CPU limit causes throttling that manifests as unexplained
p99 latency spikes well before the pod is actually short of capacity. Requests
still guarantee the scheduler reserves what the service needs.

`terminationGracePeriodSeconds` must exceed the longest in-flight operation, and
the application must handle `SIGTERM` by draining: stop accepting new work,
finish what is in progress, then exit. ASP.NET Core does this for HTTP requests
automatically; message consumers need `StopAsync` to be given time to finish the
current message.

### 15.4 Configuration and secrets

Every key a service requires, and where each comes from. **This table is the
inventory `ValidateOnStart` enforces** — a `[Required]` option missing from it
is a service that will not boot.

**Conditionally required is a real category, and it is not the same as
optional.** `Cors__Origins` is not needed when `Cors__Enabled` is false and is
mandatory when it is true — enabling a feature without configuring it is a
silent defect, while leaving it off is a valid topology. Writing such a key as
"optional with a fallback" collapses those two states into one, which is how
`WithOrigins([])` came to reject every browser request while starting cleanly.

**Required-for-some-hosts is a third category, and the mistake it invites runs
the other way.** `Identity__Client__*` is mandatory for a host that calls
another service and meaningless for one that does not — which in this blueprint
is **every host except the BFF**. The gateway forwards the caller's token rather
than minting its own; Ordering and Catalog talk over the broker and read local
projections (§6.4, ADR-002). One set of credentials in the whole platform is
what "async by default" looks like in the secrets inventory. Supplying the rest "for consistency" is not
harmless padding — it provisions a Keycloak client, a secret in the vault and a
mount, all of which must be rotated and audited, for credentials no code path
ever sends. Over-supply has no failing test to catch it, which is why it
survives longer than under-supply does.

The rule for the Kind column is mechanical: **if the value contains a
credential, it is a Secret.** Every connection string here does — SQL Server
carries a login, RabbitMQ a user, and Redis the per-service ACL user from §8.1.
A connection string in a ConfigMap is a password readable by anyone with
namespace read access and unencrypted at rest.

| Key | Kind | Source | Required |
|---|---|---|---|
| `ConnectionStrings__Ordering` | Secret | External Secrets → runtime identity (§7.1) | ✓ |
| `ConnectionStrings__OrderingMigrator` | Secret | External Secrets → migrator Job only | ✓ (Job) |
| `ConnectionStrings__RedisCache` | **Secret** | External Secrets — carries the §8.1 ACL user and password | ✓ |
| `ConnectionStrings__RedisCoordination` | **Secret** | External Secrets — separate ACL user, `noeviction` instance | ✓ |
| `ConnectionStrings__RabbitMq` | Secret | External Secrets | ✓ |
| `Identity__Authority` | Config | Helm `identity.authority` → ConfigMap | ✓ — **every host**, including the gateway |
| `Identity__Client__ClientId` | Config | Helm `identity.clientId` | ✓ **BFF only** — the one host that calls a peer (§9.7, §11.5) |
| `Identity__Client__Scope` | Config | Helm `identity.scope` | ✓ **BFF only** |
| `Identity__Client__ClientSecret` | Secret | `web-bff-identity` secret | ✓ **BFF only** |
| `Cors__Enabled` | Config | Helm `cors.enabled` → ConfigMap — **gateway only** | ✓ |
| `Cors__Origins__0…n` | Config | Helm `cors.origins` → ConfigMap — **gateway only** | ✓ **when `Cors__Enabled`** |
| `Ingress__Enabled` | Config | Helm `ingress.enabled` → ConfigMap — **gateway only** | ✓ — true in Kubernetes, false only where the gateway is the edge (Compose) |
| `Ingress__TrustedNetworks__0…n` | Config | Helm `ingress.trustedNetworks` → ConfigMap — **gateway only** | ✓ **when `Ingress__Enabled`**; CIDRs of the LB/Ingress, without which the rate limiter partitions everyone together |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Config | ConfigMap | — defaults |

| Kind | Source | Example |
|---|---|---|
| Non-secret config | ConfigMap → environment variables | Log level, feature flags, timeouts |
| Secrets | External Secrets Operator → Kubernetes Secret | Connection strings, client secrets |
| Per-environment | Helm values file | Replica counts, resource sizing |

Environment variables use the .NET double-underscore convention, so
`ConnectionStrings__Ordering` binds to `ConnectionStrings:Ordering`. Validate
configuration at startup and fail fast — a service that starts with a missing
setting and fails on the first request is much harder to diagnose than one that
refuses to start.

**Every options type gets this — no exceptions, and it goes in the registration
helper that owns the consumer.** `IOptions<T>` always resolves: unbound, it
hands back a default-constructed instance. So a forgotten binding is invisible
to `ValidateOnBuild` (§4.2), the service starts clean, and the failure surfaces
as behaviour rather than as an error:

```csharp
// Web.Bff/Program.cs (§9.7) — the same place that registers CachingTokenClient
// and ClientCredentialsHandler, and the only host that registers any of the
// three. Unbound, the BFF requests a token with an empty scope and gets 401s
// it will read as Catalog's fault.
services.AddOptions<ServiceIdentityOptions>()
        .BindConfiguration("Identity:Client")
        .ValidateDataAnnotations()
        .ValidateOnStart();
```

**This is the only options type in the solution, and that is the point.** The
tempting next line is a `ServiceOptions`-shaped bag — batch sizes, poll
intervals, retry caps — bound to an `Ordering` section that no environment ever
sets. It costs nothing to write and it is not free: `ValidateOnStart` now gates
boot on a section nobody supplies, `[Required]` on any member stops every host,
and `[Required]` on none makes `ValidateDataAnnotations` decorative. There is no
third outcome, because a key that never varies has nothing to validate.

> **An options type needs at least one member that differs between
> environments.** If every value in it would be the same in Compose, in the test
> fixture and in production, it is not configuration — it is a constant that has
> been given a deployment obligation and four places to be forgotten. `MaxAttempts`
> (§9.4), the dispatcher's tick, the saga's four delays (§9.6) and
> `ServiceOptions.OperationTimeout` are all constants for exactly this reason.
> `Identity:Client` earns its options type by holding a secret that must differ
> per environment, and it is the only thing here that does.

Which helper matters as much as the call itself. Binding beside the consumer is
what makes "the gateway needs no client credentials" true *by construction*
rather than by remembering — the gateway calls neither helper, so it neither
binds `Identity:Client` nor demands it. A binding hoisted into `Common.Web` for
tidiness would re-impose the requirement on every host and put us back where
§15.3 started.

`[Required]` is what makes `ValidateDataAnnotations` do anything — a bound
options class with no annotations validates successfully while empty:

```csharp
public sealed class ServiceIdentityOptions
{
    [Required] public string ClientId { get; init; } = "";
    [Required] public string ClientSecret { get; init; } = "";
    [Required] public string Scope { get; init; } = "";
}
```

> **A required setting is a deployment obligation.** `ValidateOnStart` turns a
> missing value into a refusal to boot, which is the right trade — but only if
> every environment supplies it. Adding a `[Required]` field means editing
> **four** places in the same change: Compose (§14.1), the Aspire host (§14.2),
> the Helm values (§15.3) and the secrets inventory (below). A gate with nothing
> behind it does not harden the service; it stops it.
>
> **The integration-test fixture (§12.4) is the fifth, and it fails first.**
> `WebApplicationFactory` builds the real host, so `ValidateOnStart` runs there
> too — a missing key throws `OptionsValidationException` out of
> `InitializeAsync` and takes down the whole suite before one assertion runs.
> It is also the one environment where the correct value is a *fake*: the
> fixture must supply something that satisfies `[Required]` and is unmistakably
> not a credential, because a test that passes with a real secret in it is a
> test that will one day be run against something real.
>
> This is also the argument for *not* making things configuration. The service
> name is not in `ServiceIdentityOptions` or anywhere else — it comes from
> `IHostEnvironment.ApplicationName`, which is always populated, needs no
> binding, cannot drift from the name §13.2 puts on traces, and therefore
> cannot fail to start.

The service-wide constants that are genuinely not configuration stay static:

```csharp
public static class ServiceOptions
{
    // The ceiling §9.7's timeout hierarchy asserts against. Not bound, not
    // validated, not deployable — it is a compile-time invariant.
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
}
```

### 15.5 Release strategy

Canary: route 5% of traffic to the new version, watch error rate and p99 for ten
minutes, then progress to 25%, 50%, 100%. Roll back automatically if either
metric regresses beyond threshold.

Because database migrations run ahead of the deploy and old code may still be
serving traffic, **every migration must be backward compatible with the previous
release** (section 7.4). A canary rollback with an incompatible schema change is
unrecoverable without downtime, which defeats the point of the canary.

Feature flags decouple deployment from release. Deploy the code dark, enable it
for internal users, then progressively for customers. This also gives you a
kill switch that does not require a rollback.

---

## Appendix A — Architecture decision records

Short-form ADRs. Each records what was decided, why, and what it costs. The
value is in the "consequences" column — that is what a future reader needs when
the decision looks wrong.

### ADR-001 — Database per service

**Decision.** Each service owns a SQL Server database. No shared tables, no
cross-database queries.
**Why.** A shared database couples deployment, schema evolution and scaling.
Any change to a shared table requires coordinating every service that reads it,
which reintroduces the constraint microservices exist to remove.
**Consequences.** No cross-service joins or foreign keys. Some data is
duplicated. Reporting needs a separate approach — read replicas or a warehouse
fed by events.

### ADR-002 — Async messaging as the default

**Decision.** Services integrate through events on RabbitMQ. Synchronous calls
are the exception and require an explicit justification.
**Why.** Chained synchronous calls multiply latency and failure probability; a
service that is temporarily down should queue work, not fail requests.
**Consequences.** Eventual consistency everywhere. Debugging requires
distributed tracing. UIs must handle "in progress" states.

### ADR-003 — MassTransit v8, pinned

**Decision.** Use MassTransit 8.x (Apache 2.0) and pin the major version.
**Why.** MassTransit v9 moved to a commercial licence in 2026 at $400–1,200 per
month. v8 remains Apache 2.0 and maintained into 2026, and its abstraction over
RabbitMQ keeps the broker replaceable.
**Consequences.** A migration decision is required when v8 maintenance ends.
Options: pay for v9, move to Wolverine, adopt a community fork, or use
`RabbitMQ.Client` directly. What preserves all four is that **no Application or
Domain code touches a MassTransit type**: publication goes through
`IIntegrationEventPublisher` (§9.3) and the only MassTransit surface is the
outbox dispatcher, the consumer classes and the bus configuration — all in
Infrastructure. Note that `IPublishEndpoint` and `IBus` are MassTransit types
and so are *not* the abstraction; using them as the seam would mean abstracting
MassTransit behind MassTransit.
**Review by.** Q4 2026.

### ADR-004 — No mediator library

**Decision.** Implement the command/query dispatcher and pipeline in
`Common.Application` — roughly 80 lines.
**Why.** MediatR moved to a commercial licence. The functionality used here is
small, and owning it removes a dependency, a licence obligation, and a layer of
reflection that obscures stack traces.
**Consequences.** A small amount of infrastructure code to maintain and test.
New developers cannot rely on MediatR familiarity, so the dispatcher needs to
stay simple and documented.

### ADR-005 — EF Core for writes, Dapper for reads

**Decision.** Aggregates persist through EF Core; queries use Dapper.
**Why.** EF Core's change tracking and rich mapping suit aggregate persistence.
For reads it adds overhead and invites accidental N+1 and over-fetching. Dapper
gives exact control over the SQL for the shapes the API returns.
**Consequences.** Two data access technologies. SQL in query handlers must be
maintained by hand when the schema changes — integration tests catch this.

### ADR-006 — Redis for cache and coordination, never as a store of record

**Decision.** Redis holds cached read models, idempotency keys, distributed
locks and the token denylist. Nothing that must survive its loss. These are
split across **two instances with different eviction policies**: a cache
instance under `allkeys-lru`, and a coordination instance under `noeviction`
for locks, idempotency keys and the denylist. Shared rate-limit counters belong
on the coordination instance when they are built; the gateway's v1 limiter is
in-process and per-replica, and §10.3 states what that costs.
**Why.** Redis is fast and its durability guarantees are weaker than SQL
Server's. Treating it as authoritative for anything means accepting data loss.
The split exists because eviction policy is a property of the whole keyspace:
an `allkeys-lru` instance under memory pressure will evict a held lock or a
revoked-token entry with no error and no log line (§8.1).
**Consequences.** Two Redis instances to run, two connection strings, and a
keyed-service registration so choosing the wrong one is a visible decision.
Every cached value must be reconstructible from SQL Server. A cold cache causes
a load spike on the databases, which capacity planning must allow for; a lost
coordination instance is more serious and is why it runs with persistence
enabled.

### ADR-007 — Migrations as a pre-deploy job

**Decision.** Never call `Database.Migrate()` at application startup.
**Why.** Multiple replicas race; rolling deploys run old code against a new
schema; and the runtime identity would need DDL permissions.
**Consequences.** Every migration must be backward compatible with the running
version. Destructive changes become multi-release sequences.

### ADR-008 — YARP as the gateway

**Decision.** YARP, self-hosted, with routing/auth/rate-limiting only.
**Why.** MIT, actively maintained by Microsoft, configurable in code as well as
JSON, and it runs in the same stack the team already knows. Ocelot is
comparatively quiet; managed gateways add cloud coupling and cost.
**Consequences.** The gateway is a service to operate and scale. Its config must
stay disciplined — aggregation belongs in a BFF, not here.

### ADR-009 — Keycloak, not a hand-built identity service

**Decision.** Keycloak (Apache 2.0) as the OIDC provider.
**Why.** Authentication is a solved problem with a long tail of security detail
and no business differentiation. Building it creates liability and no value.
**Consequences.** Keycloak is another component to run, upgrade and back up.
Realm configuration must be source-controlled and imported, not clicked through
the admin UI.

### ADR-010 — Testcontainers, not in-memory providers

**Decision.** Integration tests run against real SQL Server, Redis and RabbitMQ
in containers.
**Why.** The EF Core in-memory provider does not enforce foreign keys, does not
implement `rowversion` concurrency, and translates LINQ differently. Tests green
against it still fail in production.
**Consequences.** Tests need a Docker daemon and take seconds rather than
milliseconds. Mitigated by sharing containers per collection and resetting with
Respawn.

### ADR-011 — Compose baseline, Aspire optional

**Decision.** Docker Compose is the documented local development environment.
Aspire is offered as an optional accelerator.
**Why.** Compose is universal, stable and language-agnostic, which suits a
reference architecture. Aspire gives a materially better inner loop and free
distributed tracing, but adds a fast-moving dependency and a visible tooling
opinion.
**Consequences.** Two local-dev paths to document and keep working. Mitigated by
Aspire's ability to emit a Compose file from the same model, and by the low exit
cost (section 14.2).

### ADR-012 — Contracts versioned by namespace

**Decision.** Integration events live in `Common.Contracts.<Service>.V<n>`.
Breaking changes create a new version; both are published during a deprecation
window.
**Why.** It is the only mechanism that lets producers and consumers deploy
independently, which is the entire point of the architecture.
**Consequences.** Contract changes require deliberate planning. Consumer
adoption must be tracked with telemetry before a version is retired.

### ADR-013 — Dapr not adopted

**Decision.** No Dapr sidecars. Messaging is MassTransit over RabbitMQ; state is
EF Core over SQL Server; secrets come from the platform's secret store.
**Why.** Dapr's building blocks are genuinely useful in polyglot estates. In an
all-.NET platform they add a sidecar per pod, a control plane to operate, and an
abstraction over a broker the team already programs directly. Its state store
abstraction is the specific concern: it makes "any service can read any state"
mechanically easy, which erodes the data ownership rule this architecture is
built on.
**Consequences.** No portability across message brokers beyond what MassTransit
provides, and no free service invocation with mTLS. Revisit if services in other
languages become first-class.

### ADR-014 — Wolverine not adopted, but kept as the exit

**Decision.** MassTransit for messaging, a hand-rolled dispatcher for in-process
CQRS.
**Why.** Wolverine is a credible single-stack alternative covering both, with a
strong transactional inbox/outbox story. It is not adopted because it couples
the CQRS choice to the messaging choice — replacing one would mean replacing
both.
**Consequences.** Wolverine remains the most likely destination if MassTransit
v8 maintenance ends and v9's licence is declined (ADR-003). Confining every
MassTransit type to Infrastructure — behind `IIntegrationEventPublisher` on the
publish side and thin consumer adapters on the receive side — is what keeps that
migration a bounded piece of work rather than a rewrite. Switching is an
ADR-level decision, not a silent swap.

### ADR-015 — Minimal APIs, not MVC controllers

**Decision.** Endpoints are Minimal API groups.
**Why.** The endpoint layer translates HTTP to a command or query and does
nothing else. Controllers bring a base class, action filters and binding
conventions to do that, and the filter pipeline duplicates the dispatcher
pipeline.
**Consequences.** Endpoint classes must be organised deliberately — a single
`Program.cs` of two hundred `MapPost` calls is worse than controllers were. One
static class of extension methods per aggregate, registered from the composition
root.

### ADR-016 — Cursor pagination by default

**Decision.** Collection endpoints use opaque keyset cursors. `page`/`pageSize`
is not the default.
**Why.** `OFFSET n ROWS` costs proportionally to `n`, and results shift under
concurrent inserts, so a user paging through a live list sees duplicates and
skips.
**Consequences.** No "jump to page 47" and no cheap total count. Where a UI
genuinely needs either — an admin table over a bounded set — offset pagination
is an explicit, documented exception.

### ADR-017 — One synchronous hop

**Decision.** At most one synchronous downstream service call per inbound
request. Synchronous calls inside message consumers require a written exception.
**Why.** Availability multiplies and latency accumulates down a chain. Four
services at 99.9% give 99.6% — 43 minutes of monthly downtime becomes nearly
three hours with no service having missed its own target.
**Consequences.** Cross-context data must arrive by event and be projected
locally, which means designing for staleness in the UI. That is the intended
trade.

### ADR-018 — Reactions happen after commit

**Decision.** Nothing subscribes to a domain event inside the write
transaction. The dispatcher stages outbox rows and performs no other I/O;
projections, cache invalidation and integration publishing all run afterwards,
driven by the outbox (§7.5).
**Why.** An in-process handler on its own connection is a second transaction
that can commit while the aggregate rolls back. One sharing the `DbContext` is
atomic but not retryable, so a read-model bug becomes a write-path outage.
Either version deadlocks against the locks the outer transaction still holds,
under load and not under test.
**Consequences.** Read models are eventually consistent by construction, and
the lag is visible rather than pretended away. The outbox grows a second
delivery lane (`Local`) so same-service reactions get the same durability and
retry accounting as cross-service ones. A projection can be fixed and replayed
without touching the write path — which is the property that pays for the
staleness.

---

## Appendix B — Dependency licence register

Between 2024 and 2026 several long-standing free .NET libraries moved to
commercial licences. The register below is the state at the time of writing;
**verify before adopting**, because this is exactly the category of fact that
goes stale.

### Chosen — free for commercial use

| Package | Licence | Role |
|---|---|---|
| ASP.NET Core, EF Core, YARP, HybridCache | MIT | Framework |
| Dapper | Apache 2.0 | Read-side data access |
| MassTransit **8.x** | Apache 2.0 | Messaging |
| StackExchange.Redis | MIT | Redis client |
| FluentValidation | Apache 2.0 | Input validation |
| Scrutor | MIT | Assembly scanning for handler registration (§6.2) |
| OpenTelemetry .NET | Apache 2.0 | Telemetry, including logging (§13.2) — there is no separate logging library |
| Polly (`Microsoft.Extensions.Http.Resilience`) | MIT | Resilience |
| `AspNetCore.HealthChecks.SqlServer`, `AspNetCore.HealthChecks.Redis`, `AspNetCore.HealthChecks.Rabbitmq` (Xabaril) | Apache 2.0 | The readiness checks §13.5 registers. Written out in full rather than abbreviated to `.Redis`, so a text comparison against the pins in §4.4 matches on the whole name — an abbreviation here is satisfied by `StackExchange.Redis` and the check passes while the row is missing. The one third-party runtime dependency that is easy to leave off this table, because "health checks" sound like framework and are not |
| gRPC for .NET (`Grpc.AspNetCore`, `Grpc.Net.ClientFactory`, `Grpc.Tools`, `Google.Protobuf`) | Apache 2.0 / BSD-3 | The BFF's synchronous hop and its `.proto` contract (§9.7) |
| `Microsoft.AspNetCore.Mvc.Testing` | MIT | `WebApplicationFactory` for the API-contract tests (§12.4) |
| xUnit v3 | Apache 2.0 | Test framework |
| NSubstitute | BSD-3 | Mocking |
| Shouldly *or* AwesomeAssertions | BSD-3 / Apache 2.0 | Assertions |
| Testcontainers for .NET | MIT | Integration test infrastructure |
| Respawn | MIT | Test database reset |
| WireMock.Net | Apache 2.0 | HTTP stubbing |
| `Microsoft.NET.Test.Sdk` | MIT | The test host every test project needs; pinned because a major bump changes discovery |
| `Microsoft.Extensions.TimeProvider.Testing` | MIT | `FakeTimeProvider` — the clock seam §12.7 requires |
| Keycloak | Apache 2.0 | Identity provider |
| RabbitMQ | MPL 2.0 | Message broker |

### Avoided — commercial, with the replacement used here

| Package | Change | Replacement in this blueprint |
|---|---|---|
| **MediatR** v13+ | Commercial from 2025 | Hand-rolled dispatcher (§6.2) |
| **AutoMapper** v15+ | Commercial from 2025 | Explicit mapping, by hand (§9.3). A source generator such as Mapperly is the alternative, and is not pinned here because nothing in the blueprint uses one |
| **MassTransit** v9 | Commercial from 2026, $400–1,200/month | Pinned to v8 (Apache 2.0) |
| **FluentAssertions** v8+ | Per-developer-per-year from 2025 | Shouldly, or AwesomeAssertions (a fork of v7) |

### Requires a licence review

| Component | Condition |
|---|---|
| SQL Server | Per-core or CAL licensing; Developer Edition is free for non-production only |
| Duende IdentityServer | Free below a revenue threshold; commercial above it |
| Redis | Redis 7.4+ is under RSALv2/SSPLv1. Valkey (BSD) is a drop-in fork if the terms are a problem |

Add an SCA step to CI that fails the build on a licence outside an allow-list.
Discovering a licence obligation at renewal time is considerably more expensive
than discovering it at build time.

---

## Appendix C — Delivery plan

An architecture document that does not say what to build first is a description,
not a plan. This appendix sequences the work into independently reviewable pull
requests. Every PR leaves `main` building and green.

### C.1 Service build order

Two orderings matter and they are different. The **platform** is built in the PR
sequence below. The **services** are built in this order, and not in parallel:

1. **Notifications** — no domain logic, pure event consumer, no public API.
   Proves messaging, observability and the deployment pipeline end to end while
   there is nothing else to debug.
2. **Catalog** — simple domain. Establishes the CQRS structure, caching, and the
   query patterns.
3. **Ordering** — the core domain. Rich aggregate, outbox, saga.
4. **Inventory and Payments** — concurrency and third-party integration, once
   the surrounding patterns have settled.
5. **Shipping** — last, because it depends on everything upstream being stable.

The first service through the pipeline finds every gap in deployment,
observability and testing. Fixing those once is far cheaper than fixing them
again for each of the seven deployables behind it — the five remaining services
plus the gateway and the BFF, which take the same pipeline (§15.1).

### C.2 Pull request sequence

Phase names map to the `phase` column. Dependencies are PR numbers.

#### Foundation

| PR | Title | Depends | Delivers |
|---|---|---|---|
| **01** | `chore: solution structure, SDK pin, central package management, CI skeleton` | — | `global.json`, `Directory.Build.props`, `Directory.Packages.props` with **exact** versions, `.editorconfig`, solution, CI running `dotnet test`, **licence allow-list gate** |
| **02** | `feat(common): Result, Error, and domain primitives` | 01 | `Result`/`Result<T>`, `Error`, `Entity<TId>`, `AggregateRoot<TId>`, `IDomainEvent`, typed-ID pattern. **Unit tests ship in this PR** — the convention starts here |
| **03** | `feat(common): ProblemDetails, error catalogue, correlation middleware` | 02 | RFC 9457 mapping, the status-code table from §10.5, `X-Correlation-Id` middleware, `ToHttpResult()` |
| **04** | `feat(common): CQRS dispatcher and pipeline behaviours` | 02 | The dispatcher from §6.2, logging and validation behaviours, tests asserting behaviour **ordering**. No transaction behaviour yet |
| **05** | `feat(common): OpenTelemetry and structured logging defaults` | 03 | `Common.Web`: OTLP export, resource attributes, health endpoint wiring, log redaction policy |
| **06** | `feat(dev): Docker Compose — SQL Server, Redis, RabbitMQ, Keycloak, OTel` | 01 | The Compose file from §14.1, `.env.example`, documented ports, healthchecks |

#### Service template

| PR | Title | Depends | Delivers |
|---|---|---|---|
| **07** | `feat(template): service skeleton and architecture test gate` | 02–06 | Compilable empty service across five projects (§4.1), Minimal API host, health endpoints, OpenAPI. **NetArchTest gate from this PR**: domain isolation, Application ↛ EF Core, endpoints ↛ Infrastructure, Application and Domain ↛ MassTransit (§4.2, §9.3) |
| **08** | `feat(template): EF Core, repositories, IUnitOfWork, migrator host` | 07, 06 | `DbContext` sealed in Infrastructure, `IUnitOfWork` port, `*.Migrator` project, **dual connection strings** (§7.1), Testcontainers smoke test |
| **09** | `feat(common): TransactionBehavior over IUnitOfWork` | 04, 08 | §6.3 behaviour. Tests proving `SaveChanges` is called once on success and never on failure, that a handler which writes through `ExecuteRawAsync` and then returns `Result.Failure` leaves no row, and that queries never open a transaction |
| **10** | `feat(catalog): first vertical slice — command, query, cursor pagination` | 07–09 | One aggregate, one command, one cursor-paginated query. **Endpoints are deliberately unauthenticated and this is stated in the README** — closed by PR-16 |
| **11** | `feat(tooling): new-service scaffold script` | 07, 10 | Copies and renames the template: ports, database name, solution entries, Compose block. Dogfooded by PR-18 |

#### Data, cache, messaging

| PR | Title | Depends | Delivers |
|---|---|---|---|
| **12** | `feat(common): Redis helpers — HybridCache, key namespaces, distributed locks` | 06, 08 | Key-naming helper, **mandatory TTL enforced in code**, `{service}:cache\|lock\|idem\|denylist:` namespaces, the eviction-policy isolation from §8.1, Testcontainers Redis tests |
| **13** | `feat(template): MassTransit RabbitMQ registration and harness smoke` | 08, 06 | Bus connects, publish/consume proven with the in-memory harness. **Split from the outbox deliberately** to keep the review readable |
| **14** | `feat(template): transactional outbox and allow-list event mapper` | 09, 13, 10 | Outbox table and dispatcher (§9.4), `IIntegrationEventMapper` allow-list, `IIntegrationEventPublisher` with the §9.3 contract. **`MessageTypeMap` and `OutboxJson` land here, not later**: both halves of what the `MessageType` and `Payload` columns mean, and a column whose format is decided after rows exist in it is a migration nobody wants. Integration tests proving aggregate row and outbox row commit in **one** transaction, that `Stage` copies the envelope's `MessageId`, and that every stageable domain event round-trips through `OutboxJson.Options` (§12.4) |
| **15** | `feat(messaging): Contracts, inbox consumers, inbox + outbox retention purge` | 14, 12 | `Common.Contracts` with versioned records, inbox filter (§9.5), the `IntegrationEventConsumer<T>` adapter (§9.4), one purge hosted service covering **both** tables. **`Platform.IntegrationTests` starts here** with the §12.6 contract suite — no domain reference, versioned namespace, round-trip — because the rules arrive with the assembly they constrain |

#### Edge and security

| PR | Title | Depends | Delivers |
|---|---|---|---|
| **16** | `feat(security): JWT bearer with mandatory per-service re-validation` | 03, 10 | Keycloak realm import, JWT validation in `Common.Web`, permission policies, test auth handler. **Security tests: forged header without a token → 401; user A reading user B's resource → 404** |
| **17** | `feat(gateway): YARP routing, JWT, rate limiting, CORS` | 06, 16, 10 | The gateway from §10, dual-version route example with matched prefix strips, rate-limit policies, the gateway's own `inventory:admin` authorization policy, correlation ID assignment. **Two config tests, both on `ReverseProxy:Routes`: every `AuthorizationPolicy` and `RateLimiterPolicy` named resolves — an unresolvable one drops the route silently — and every route's match minus its `PathRemovePrefix` equals the group its service maps (§10.2), which the in-process API tests cannot see** |
| **18** | `feat(ordering): second service from the scaffold` | 11, 08, 16 | Proves the scaffold. Own database, own migrator, gateway route |

#### Integration and operations

| PR | Title | Depends | Delivers |
|---|---|---|---|
| **19** | `feat(bff): the BFF host, its gRPC client and the one permitted sync hop` | 05, 12, 17, 18 | `Web.Bff` (§4.1), `AddStandardResilienceHandler` defaults, the timeout hierarchy asserted at startup, one deliberate **BFF → Catalog** pricing call demonstrating ADR-017. **The only host that gets an `Identity:Client`** (§11.5) — the Keycloak client and secret arrive here and nowhere else |
| **20** | `feat(ordering): consume Catalog events into a local projection` | 15, 18, 17 | The full async path. Projection with **idempotent `MERGE` and the out-of-order guard** from §6.6 |
| **21** | `feat(ordering): order fulfilment saga` | 20, 14 | The state machine from §9.6, compensation paths, **a timeout on every wait state**, harness tests including the payment-declined compensation ordering |
| **22** | `test: expand architecture rules and document the test strategy` | 07, 10, 14 | Full composition-root rules, `docs/testing.md`, Testcontainers categories, coverage reported on the domain layer specifically |
| **23** | `feat(deploy): Helm charts, migration hooks, probes` | 17, 20, 08 | Chart per service, umbrella chart, migration job as a `pre-upgrade` hook, the probe and resource shape from §15.3 |
| **24** | `docs(ops): runbooks, secrets, dashboards-as-code, the SLO run` | 15, 20, 21 | The twelve runbooks from §13.9 — one per alert, checked both ways — per-lane outbox alerts (§13.6), `docs/secrets.md`, Grafana JSON in `deploy/observability/`, and the k6 **SLO run** against staging (§13.7, §15.1) — named for what it asserts, because §15.1 deliberately has no smoke stage |
| **25** | `ci: integration categories, canary deploy, quality gates` | 20, 22, 17 | Path-filtered per-service builds, containerised integration tests in CI, canary with automated rollback on error rate or p99 |

#### Optional

| PR | Title | Depends | Delivers |
|---|---|---|---|
| **26** | `chore(optional): consumer-driven contract tests` | 25 | Pact, only if a consumer relationship becomes contentious. Not required for completeness |

### C.3 Dependency graph

```mermaid
flowchart TD
    P01[01 Foundation] --> P02[02 Result/Domain]
    P01 --> P06[06 Compose]
    P02 --> P03[03 Error catalogue]
    P02 --> P04[04 Dispatcher]
    P03 --> P05[05 OTel]
    P02 --> P07[07 Template + arch gate]
    P03 --> P07
    P04 --> P07
    P05 --> P07
    P06 --> P07
    P07 --> P08[08 EF + UoW + Migrator]
    P06 --> P08
    P04 --> P09[09 TransactionBehavior]
    P08 --> P09
    P07 --> P10[10 Catalog slice]
    P08 --> P10
    P09 --> P10
    P07 --> P11[11 Scaffold]
    P10 --> P11
    P06 --> P12[12 Redis]
    P08 --> P12
    P08 --> P13[13 Bus smoke]
    P06 --> P13
    P09 --> P14[14 Outbox + mapper]
    P13 --> P14
    P10 --> P14
    P14 --> P15[15 Inbox + Contracts]
    P12 --> P15
    P03 --> P16[16 JWT]
    P10 --> P16
    P16 --> P17[17 Gateway]
    P06 --> P17
    P10 --> P17
    P11 --> P18[18 Ordering service]
    P16 --> P18
    P08 --> P18
    P17 --> P19[19 Sync hop]
    P18 --> P19
    P12 --> P19
    P05 --> P19
    P15 --> P20[20 Projection]
    P18 --> P20
    P17 --> P20
    P20 --> P21[21 Saga]
    P14 --> P21
    P07 --> P22[22 Arch + test docs]
    P14 --> P22
    P10 --> P22
    P17 --> P23[23 Helm]
    P20 --> P23
    P08 --> P23
    P21 --> P24[24 Runbooks + SLO]
    P15 --> P24
    P20 --> P24
    P20 --> P25[25 CI + canary]
    P22 --> P25
    P17 --> P25
    P25 --> P26[26 Optional: Pact]
```

The graph carries every edge in the tables above and no others. It is a
transcription, so it can drift silently — a missing edge suggests two PRs are
independent when the table says one blocks the other, which is the direction
that costs a wasted branch rather than a wrong build.

### C.4 Sequencing rules worth preserving

Three choices in the ordering above are deliberate and easy to lose in
replanning:

**PR-13 is split from PR-14.** Getting a bus connection working and getting the
outbox transactionally correct are separate problems. Reviewing them together
means the interesting half gets skimmed.

**PR-10 ships with unauthenticated endpoints, and says so.** Security lands in
PR-16. Naming a temporary gap in the README and scheduling its closure is
honest; discovering it in a pen test six months later is not. The alternative —
blocking the first vertical slice on the full auth stack — delays the feedback
that the slice exists to provide.

**PR-11 is dogfooded by PR-18.** The scaffold script is proven by the next real
service, not by intent. If the script cannot produce Ordering, it is not
finished.

And one rule about the whole sequence: **from PR-02 onward, production code
lands with its tests in the same pull request.** Not "tests to follow" — the
follow-up PR is the one that gets deprioritised, and a test written after the
code has never been observed failing.

---

## Appendix D — Type inventory

Code samples in this document are excerpts, not compilable units. This appendix
is the index of the types they reference, so a reader can tell at a glance
whether a name is defined somewhere in the document, deliberately elided, or a
framework type.

It exists because of a specific failure mode: a sample that calls
`ProjectionInvoker.InvokeAsync(...)` reads as complete, and nothing catches that
no such method was ever defined. Grepping this table against the samples is a
repeatable check; noticing by eye is not.

### D.1 Application ports — defined here

| Type | Section | Purpose |
|---|---|---|
| `ICommand<T>`, `IQuery<T>` | §6.2 | Request marker interfaces |
| `ICommandHandler<,>`, `IQueryHandler<,>` | §6.2 | Request handlers |
| `IPipelineBehavior<,>`, `NextDelegate<T>` | §6.2 | Cross-cutting pipeline |
| `IDispatcher` | §6.2 | Entry point for commands and queries |
| `IUnitOfWork` | §6.3 | Transaction boundary; EF sealed in Infrastructure |
| `Error`, `ErrorType` | §10.5 | `Code`, `Description`, `Type`. `Code` is a metric dimension (§9.8), so it is a closed set by construction |
| `OrderErrors` | §10.5 | Ordering's catalogue — the only place an `Error` is constructed |
| `IIdempotencyStore`, `IdempotencyEntry` | §8.5 | Idempotency-key claim, Redis-backed |
| `IIdempotentCommand` | §8.5 | Opts a command into `IdempotencyBehavior`; carries `CommandId` |
| `IDomainEventCollector` | §7.5 | Reads the change tracker without exposing it |
| `IDomainEventDispatcher` | §7.5 | Stages outbox rows; runs no handlers |
| `IProjectionRegistry` | §7.5 | Which event types have projection handlers |
| `IProductPriceReader` | §6.4 | Prices from the **local** projection — never a remote call |
| `ICurrentUser` | §11.4 | The caller, for resource-level checks. `IsAuthenticated` is false on the consumer path |
| `CancelOrderCommand`, `CancelOrderRequest`, `CancelOrderHandler` | §11.4 | The slice both entry paths converge on — HTTP and `CommandConsumer` (§9.4) |
| `CancellationReasons` | §11.4 | Wire code → `CancellationReason`; the single parse both paths call |
| `OrderMetrics` | §13.3 | Instruments on `Ordering.Orders`. **Application**, not `Common.*`: it takes `Money`. Recorded only from §6.6's projection, on the committed path |
| `ICommandMessageMapper<,>` | §6.2 | Wire contract → application command |
| `IIntegrationEvent` | §9.1 | The three envelope fields every contract carries; the constraint that lets the consumer read `OccurredAt` |
| `ServiceIdentityOptions` | §15.4 | Bound to `Identity:Client`, `ValidateOnStart`-checked. Registered by the BFF alone (§9.7) |
| `ServiceOptions` | §15.4 | Static constants only — not bound, not validated, not deployable |
| `PluggableInterfaces`, `AddPluggableFrom` | §6.2 | The one list of scanned interfaces, and the per-assembly scan that reads it |
| `IIntegrationEventMapper` | §9.3 | Domain → integration allow-list |
| `IIntegrationEventPublisher` | §9.3 | Stages outbox rows on the current transaction |
| `IProjectionHandler<T>` | §9.4 | Reacts to own events, local lane, after commit |
| `IIntegrationEventHandler<T>` | §9.4 | Reacts to another service's events |
| `OutboxLane` | §9.3 | `Broker` \| `Local` |

### D.2 Domain model — defined in §5

The sample domain. All of these live in `Ordering.Domain` and reference nothing
outside it (§4.2).

| Type | Section | Kind |
|---|---|---|
| `Entity<TId>`, `AggregateRoot<TId>` | §5.5 | Base types; `AggregateRoot` carries `DomainEvents` and `Version` |
| `IDomainEvent`, `IHasDomainEvents` | §5.5 | Domain event contracts |
| `Order`, `OrderLine` | §5.4 | The aggregate and its child entity |
| `OrderId`, `CustomerId`, `ProductId` | §5.2 | Strongly typed identifiers |
| `Money`, `Address` | §5.3 | Value objects |
| `OrderStatus`, `CancellationReason` | §5.4 | Enumerations |
| `PaymentReference`, `TrackingNumber` | §5.4 | Value objects referenced by aggregate methods |
| `DomainException` | §5.3 | Signals a broken invariant — a bug, not user input (§5.7) |
| `IOrderRepository` | §5.6 | Aggregate persistence port |
| `OrderPlacedDomainEvent`, `OrderCancelledDomainEvent` | §5.5 | Declared in full; carry domain types (`Money`, `OrderLineSnapshot`) |
| `OrderLineSnapshot` | §5.5 | Immutable copy of a line at the moment an event was raised — events must not alias the aggregate's live list |
| `OrderStockConfirmedDomainEvent`, `OrderConfirmedDomainEvent`, `OrderShippedDomainEvent` | §5.4 | Raised by `Order`; same shape, suffix per §5.5 |
| `V1.OrderPlaced`, `PlacedLine`, `V1.OrderConfirmed`, `ConfirmedLine` | §9.1 | Published event contracts — primitives only, each owning its line type |
| `ReserveStock`, `ReleaseStock`, `StockLine` | §9.6 | Inventory's command contracts |
| `AuthorisePayment` | §9.6 | Payments' command contract |
| `CancelOrder`, `ConfirmOrder`, `MarkOrderShipped`, `FlagOrderForReview`, `CancelReasons`, `ReviewReasons` | §9.6 | Ordering's command contracts; reason codes are strings, not domain enums |

### D.3 Outbox types — two, deliberately

The distinction that finding this appendix's first defect depended on:

| Type | Layer | Columns | Written by | Read by |
|---|---|---|---|---|
| `OutboxMessage` | EF entity, §9.4 | All eleven | `IIntegrationEventPublisher` staging; test fixtures | `db.OutboxMessages`, alerts, purge |
| `OutboxClaim` | Dapper record, §9.4 | The eight the `OUTPUT` clause returns | — | `OutboxDispatcher.ProcessBatchAsync` |
| `MessageTypeMap` | Singleton, §9.4 | Neither — it maps `MessageType` values to types | Built at startup from `MessageTypeSource` | `Stage` on the way in, `DeliverAsync` on the way out, §12.4's round-trip |
| `MessageTypeSource` | Singleton, §9.4 | The assembly list behind the map | §4.2's registration; §12.4's fixture `Add`s the test assembly | `MessageTypeMap`'s factory |
| `OutboxJson` | Static, §9.4 | Neither — the one `JsonSerializerOptions` both ends of the lane use | — | `Stage` and `DeliverAsync`, and §12.4's round-trip test |
| `InboxMessage` | EF entity, §9.5 | `(MessageId, Endpoint)` composite key, `HandledAt` | `InboxFilter<T>` | duplicate suppression, retention purge |

`OutboxClaim` has no `ProcessedAt`, `LastError` or `LockedUntil` — a claimed row
is unprocessed by definition, and the dispatcher writes those columns rather
than reading them. Using one type for both produces properties that are
structurally always null on one path.

### D.4 Infrastructure implementations — defined here

| Type | Section | Implements |
|---|---|---|
| `Dispatcher` | §6.2 | `IDispatcher` |
| `EfUnitOfWork` | §6.3 | `IUnitOfWork` |
| `EfDomainEventCollector` | §7.5 | `IDomainEventCollector` |
| `DomainEventDispatcher`, `ProjectionRegistry` | §7.5 | Application ports |
| `RedisIdempotencyStore` | §8.5 | `IIdempotencyStore` |
| `OrderingIntegrationEventMapper` | §9.3 | `IIntegrationEventMapper` |
| `OutboxDispatcher` | §9.4 | `BackgroundService`; `ProcessBatchAsync` is public for tests |
| `ProjectionInvoker` | §9.4 | Cached-delegate handler resolution |
| `IntegrationEventConsumer<T>` | §9.4 | `IConsumer<T>` for **events** → `IIntegrationEventHandler` |
| `CommandConsumer<TMessage,TCommand>` | §9.4 | `IConsumer<T>` for **commands** → the application dispatcher |
| `InboxFilter<T>` | §9.5 | `IFilter<ConsumeContext<T>>` |
| `ProjectedPriceReader` | §6.4 | `IProductPriceReader` over `ordering.ProductPrices` |
| `HttpContextCurrentUser` | §11.4 | `ICurrentUser` over `IHttpContextAccessor`; reads `ClaimTypes.NameIdentifier` and `permission` claims |
| `SensitiveDataRedactor` | §13.4 | `BaseProcessor<LogRecord>`; enforces the never-log list on the pipeline §13.2 builds |
| `OutboxMetrics` | §13.6 | Observable gauges on the `Ordering.Outbox` meter; singleton, eagerly constructed |
| `RequestMetrics` | §13.3 | `request.duration` on `Commerce.Requests`; injected by `LoggingBehavior`, so no forcing needed |
| `MessagingMetrics` | §13.3 | Three instruments on `Commerce.Messaging`: `messaging.delivery.lag` and `projection.lag` histograms, and the `command.domain_rejected` counter `Rejected` writes (§9.8). Injected by `IntegrationEventConsumer<T>` and `CommandConsumer<,>`; resolved from the provider by the static `ProjectionInvoker` |
| `IOutboxStats`, `OutboxStats` | §13.6 | Backlog age and abandoned count, read per-scope from a singleton |
| `MetricsInitialiser` | §13.6 | `IHostedService` whose only job is forcing the metrics singletons to be constructed |
| `OrderFulfilmentState` | §9.6 | `SagaStateMachineInstance`; persisted to `ordering.OrderFulfilmentStates` |
| `ProductPriceProjection` | §6.6 | Writes `ordering.ProductPrices` from Catalog's three product events |
| `OrderSummaryProjection` | §6.6 | Writes `ordering.OrderSummaries` |
| `PriceChangedCacheInvalidator` | §8.4 | Ordering's second `PriceChanged` handler — cache only |
| `ClientCredentialsHandler` | §11.5 | `DelegatingHandler`; attaches the M2M bearer token to the BFF's outbound calls |
| `ValidationBehavior<,>`, `TransactionBehavior<,>` | §6.3 | `IPipelineBehavior<,>` |
| `LoggingBehavior<,>` | §13.3 | `IPipelineBehavior<,>`; outermost — pushes the request scope, logs the outcome and records `request.duration` |
| `IdempotencyBehavior<,>` | §8.5 | `IPipelineBehavior<,>` over `IIdempotencyStore` |
| `UseCorrelationId`, `MapCommonHealthEndpoints`, `AddCommonWebDefaults` | §10.4, §13.5, §13.2 | `Common.Web` host extensions |
| `OrderFulfilmentSaga`, `Endpoints` | §9.6 | Saga and its command destinations |
| `ServiceFixture` | §12.4 | Testcontainers `IAsyncLifetime` fixture; owns the `WebApplicationFactory`. Lives in `Ordering.TestSupport` (§4.1) |
| `TestAuthHandler` | §12.4 | `AuthenticationHandler<AuthenticationSchemeOptions>`; issues the principal a test names in headers. Also `Ordering.TestSupport` |

### D.5 Referenced but deliberately not shown

These are named in samples and left undefined on purpose. They are ordinary,
and writing them out would add length without insight.

**The rule: any project-specific name a sample references — type *or* member —
that is not defined in D.1–D.4 must appear here.** If it is in neither, it is a
defect rather than an omission. Framework and BCL names (D.6) are out of scope;
so are members of types the document does define, since those are visible at
their declaration.

| Name | What it is |
|---|---|
| `IDbConnectionFactory`, `SqlConnectionFactory` | Opens a `SqlConnection` for Dapper reads |
| `OrderingDbContext` | The service `DbContext`; configuration in §7.2 |
| `OutboxPublisher` | `IIntegrationEventPublisher` writing `OutboxMessage` rows; resolves `MessageTypeMap` and hands it to `Stage` |
| `Result`, `Result<T>` | Non-generic `Result` is the void case — there is no `Unit` — and `Result<T>` derives from it, which is what lets `TransactionBehavior` test any command's outcome with one pattern (§6.3). `IsSuccess`/`IsFailure`, `Error`, and the `Success`/`Failure` factories |
| `ToHttpResult()` | Extension on `Result`/`Result<T>` mapping `ErrorType` to a status (§10.5) and the value to a 200 body |
| `CursorPage<T>`, `Cursor` | The pagination envelope and the opaque cursor codec (§6.5) |
| `AddCommonWebDefaults`, `AddRedisConnections`, `AddMassTransitMessaging` | `Common.Web` / Infrastructure registration helpers |
| `RedisConnections` | Keyed-service names for the cache and coordination connections (§8.1) |
| `BuildInfo` | Assembly version stamped onto OTel resource attributes (§13.2) |
| `OrderBuilder`, `AddressBuilder`, `CommandBuilder`, `SeedData` | Test data builders (§12.3) |
| `Poison`, `Healthy`, `LocalRowFor<T>`, `TestClock` | Outbox test builders (§12.4) |
| `Contracts`, `ContractSamples` | Test builders for `required`-member contract messages — one sample per contract type, and the reason the §12.6 suite cannot silently skip a new one |
| `AlwaysThrows`, `NoOpEvent`, `UnhandledEvent` | Test event types with throwing / no-op / no handler |
| `IntegrationCollection` (xUnit) | The `[CollectionDefinition]` sharing one `ServiceFixture` across test classes — declared once **per test assembly** (§12.4). Named for what it groups, and deliberately not `ServiceCollection`: that name is taken by `Microsoft.Extensions.DependencyInjection`, and the local type would win in every test file that also builds a provider |
| `ICommandMessageMapper<TMessage,TCommand>` implementations | One per command contract — e.g. parsing `CancelOrder.Reason` back to `CancellationReason` (§9.4) |
| `ConfirmOrderCommand`, `MarkOrderShippedCommand`, `FlagOrderForReviewCommand` | The other three message-borne slices, same shape as `CancelOrderCommand` (§9.4, §9.6) |
| `OrderRepository` | `IOrderRepository` over EF; also the Infrastructure assembly marker for the §6.2 scan |
| `ContractMappingException` | Thrown by an `ICommandMessageMapper` on a value it cannot map; ignored by the retry policy so it reaches the error queue immediately (§9.4, §11.4) |
| `StartHarnessAsync` | Test helper returning a started MassTransit harness (§12.5) |
| `Realm`, `Catalog`, `Audiences()` | Fixture handles on the Keycloak and Catalog containers, and a `aud`-claim reader over the decoded token — the realm-configuration assertions in §11.5 |
| `MessageTypes` | Fixture handle on the real `MessageTypeMap` (§12.4). **Not `Types`**: that name belongs to `NetArchTest.Rules.Types`, which §4.2's architecture tests call as `Types.InAssembly(...)`, and a fixture member would shadow it in any file holding both — the `ServiceCollection` collision again, two rows above |
| `TestTypeMap` | A `MessageTypeMap` over the contract and test assemblies, built once as a static in the unit tests (§12.4). Not the fixture's — the `Stage` test takes no fixture |
| `DomainEventSamples` | One sample per stageable domain event, so a new event without one fails §12.4's round-trip rather than being skipped — `ContractSamples`' counterpart for the Local lane |
| `ConcurrentRequestException` | Thrown when an idempotency key is claimed but unfinished (§8.5) |
| `InvariantViolationException` | Thrown when a command modifies more than one aggregate root (§6.3, principle 3) |
| `IAggregateRoot` | Non-generic marker on `AggregateRoot<TId>`, so the change tracker can count roots without knowing their key type |
| `StockReservationExpired`, `PaymentAuthorisationExpired`, `DespatchExpired`, `StockReleaseExpired` | Saga schedule messages — one per wait, and §9.6 has four (§9.6) |
| `FlagOrderForReviewHandler` | Writes the `OrderReviews` row; loads no aggregate (§9.6) |
| `ITokenCache`, `CachingTokenClient` | Acquires and caches the M2M access token until shortly before expiry; BFF-only (§11.5) |
| `PriceRow` | Dapper row shape behind `ProjectedPriceReader` (§6.4) |
| `SummaryRow` | Dapper row shape behind the escalated summaries query, `Products` still JSON (§6.6) |
| `FulfilmentFact` | Dapper row shape behind §6.6's fulfilment claim — `(PlacedAt, ConfirmedAt)`, non-nullable because the claim's predicate tests both columns for `IS NOT NULL` |
| `PlacedFact` | `(TotalAmount, Currency)` behind the placement claim. Non-nullable for a **weaker** reason: the predicate tests `PlacedAt`, and these are non-null only because the `MERGE` that sets `PlacedAt` sets all three together (§6.6). Splitting that statement would break these members without touching the predicate that appears to guarantee them |
| `ConcurrencyMode` | MassTransit enum selecting the saga repository's locking strategy (§9.6) |
| `GetConfiguredOptions` | Test helper returning the resilience options under assertion (§9.7) |
| `BuildServices`, `BuildProvider` | Test helpers running **both** real registration helpers — `AddOrderingApplication` and `AddOrderingInfrastructure` (§6.2, §6.3, §13.6). `BuildServices` returns the populated `IServiceCollection`; `BuildProvider` is `BuildServices().BuildServiceProvider()`. Two helpers because registrations can only be enumerated before the build |
| `OutboxBacklogHealthCheck` | Reports outbox depth as an `observe`-tagged check (§13.5) |
| `PlaceOrderCommand`, `PlaceOrderItem`, `PlaceOrderValidator`, `PlaceOrderHandler` | The worked command slice (§6.4) |
| `GetOrderSummariesQuery`, `OrderSummaryDto`, `SummaryProduct`, `OrderSummaryProjection` | The worked query and projection slice — the DTO shown at level 1 (§6.5) and rewritten at level 2 (§6.6) |
| `ProductPublished` | Catalog contract, envelope (§9.1) plus `ProductId`, `Name`, `ThumbnailUrl`, `Currency`, `Amount` — the last two so a projection has a price the moment the product exists, without waiting for a `PriceChanged` that may never come |
| `ProductDiscontinued` | Catalog contract, envelope plus `ProductId`. No reason code: §6.6 flips `IsAvailable` either way |
| `StockReserved`, `StockReservationFailed`, `StockReleased`, `PaymentAuthorised`, `PaymentDeclined`, `ShipmentDispatched` | The saga's integration events (§3.2, §9.6, `Common.Contracts`), each carrying the §9.1 envelope plus `OrderId` and what its own step decided. `PriceChanged` is not here — §9.1 declares it |

### D.6 Framework types

Not listed individually: ASP.NET Core, EF Core, MassTransit, StackExchange.Redis,
Dapper, Polly, OpenTelemetry, xUnit, Shouldly, NSubstitute, Testcontainers,
Respawn, Scrutor and FluentValidation types are assumed. Licences in Appendix B.
