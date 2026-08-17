# 4. Solution and folder structure

## 4.1 Repository layout

A single repository. Independent deployability comes from the CI pipeline
building and releasing services separately, not from separate git repositories.
A monorepo makes cross-cutting changes and contract updates atomic and reviewable.

```
/
├── src/
│   ├── BuildingBlocks/
│   │   ├── Common.Domain/              Entity, AggregateRoot, IDomainEvent (§5.5)
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
│   ├── Common.Domain.Tests/            The building blocks, under the same
│   ├── Common.Application.Tests/       *.Domain.Tests / *.Application.Tests
│   ├── Common.Infrastructure.Tests/    convention the services use (§12.1).
│   ├── Common.Web.Tests/               Common.Infrastructure's suite needs
│   │                                   Docker — its Redis half runs against
│   │                                   a Testcontainers server (§8, §12.4).
│   │                                   Common.Web is a library with no entry
│   │                                   point, so its suite drives a TestServer
│   │                                   rather than a WebApplicationFactory
│   ├── Gateway.Api.Tests/              The route file of §10.2, over the real
│   │                                   host: policy resolution, prefix strips,
│   │                                   the limiter of §10.3 driven until it
│   │                                   rejects — and §10.1's two edge
│   │                                   behaviours, compression and the body
│   │                                   ceiling, the second over a real Kestrel
│   │                                   because TestServer serves no such
│   │                                   property. No TestSupport beside it —
│   │                                   that library exists where two suites
│   │                                   share a fixture, and the gateway has one
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
│   └── backend-architecture/           This document, one file
│                                       per chapter; ADRs in Appendix A
│
├── tools/
│   └── new-service/                    The scaffold of §4.5 and its tests.
│                                       Stdlib Python, no restore — it renders
│                                       a service from Catalog at run time
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
([§7.1](07-persistence.md)) and ADR-007 forbids migrating at application startup. That includes
Shipping and Notifications, which expose no public API but still own schemas —
`Shipment`/`TrackingEvent` and `NotificationLog` respectively ([§3.2](03-bounded-contexts.md)).

It is also why [§15.2](15-cicd-deployment.md) builds **two images per service** rather than one, and why
the migrator gets its own connection string, its own SQL login and its own
Kubernetes Job. A service without a migrator has no way to create its schema
that this architecture permits.

## 4.2 The dependency rule

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
| `*.Migrator` | Infrastructure, for the `DbContext` it migrates | another service's projects; anything it does not need to apply a migration |
| `*.Api` | Application, Infrastructure (**composition root only**) | another service's projects |

**The migrator's row is the narrowest, and deliberately.** It is not a second
composition root: it builds a host, resolves the `DbContext` and calls
`Database.Migrate()` ([§7.4](07-persistence.md)), and it needs no Application,
no dispatcher and no Redis to do that. The temptation is to reach for
`AddXInfrastructure(config)` and get the context for free — which also gets
the readiness checks, the bus registration and the runtime connection string,
in the one process holding the DDL identity of [§7.1](07-persistence.md). A
migration job that can open a message broker is a migration job with reasons
to fail that have nothing to do with migrations.

`*.Domain` having no third-party dependencies is what makes domain tests
instant and mock-free. It is worth defending. Enforce it with an architecture
test rather than a code review convention:

```csharp
[Fact]
public void Domain_references_only_common_domain_and_the_framework()
{
    // The table's rule is an allow-list — "Common.Domain and nothing else" —
    // so the gate is one too, and an exact one: a blacklist only bans what
    // someone thought to name, and a System.* prefix still passes
    // System.Data.SqlClient or a serialiser. Each BCL assembly Domain starts
    // using earns its line here on purpose — extending this list is the
    // decision the gate exists to force, and System.Text.Json is the
    // extension the table forbids by name.
    string[] allowed = ["Common.Domain", "System.Runtime"];

    IEnumerable<string> referenced = typeof(Order).Assembly
        .GetReferencedAssemblies()
        .Select(a => a.Name!);

    referenced.ShouldAllBe(name => allowed.Contains(name));
}
```

### The composition-root rule

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
    // Not the service's Infrastructure namespace alone: the rule above is
    // "Application and Domain contracts only", and the concrete types it
    // bans — DbContext, IPublishEndpoint, IConnectionMultiplexer — reach an
    // endpoint transitively without any Ordering.Infrastructure dependency
    // to trip on.
    TestResult result = Types
        .InAssembly(typeof(OrderEndpoints).Assembly)
        .That().ResideInNamespaceContaining(".Endpoints")
        .ShouldNot().HaveDependencyOnAny(
            "Ordering.Infrastructure",
            "Microsoft.EntityFrameworkCore",
            "MassTransit",
            "StackExchange.Redis")
        .GetResult();

    result.IsSuccessful.ShouldBeTrue(
        $"leaked: {string.Join(", ", result.FailingTypeNames ?? [])}");
}
```

One more, for the rule [§9.3](09-messaging.md) states in prose: application code publishes through
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
    Assembly[] assemblies = [typeof(PlaceOrderHandler).Assembly, typeof(Order).Assembly];
    foreach (Assembly assembly in assemblies)
        Types
            .InAssembly(assembly)
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

### What the composition root composes

Each layer exposes exactly one registration method. `Program.cs` calls them and
does nothing else with Infrastructure — which is what makes the rule above
enforceable rather than aspirational, and what lets tests exercise the real
registration path ([§6.2](06-cqrs.md)) instead of a hand-built container.

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

    // Not AddScoped<IDispatcher, Dispatcher>: Dispatcher is internal to
    // Common.Application (§6.2), so this assembly cannot name it.
    services.AddDispatcher();

    // The same reason, one section over: DomainEventDispatcher,
    // ProjectionRegistry and its cache are all internal to Common.Application
    // (§7.5), so the three registration lines they need are not lines this
    // assembly can write either. Not three AddScoped lines — the cache is a
    // singleton, for the reason §7.5 gives beside it.
    services.AddDomainEventDispatcher();                                // §7.5

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
    this IServiceCollection services,
    IConfiguration configuration)
{
    services.AddDbContext<OrderingDbContext>(o =>
        o.UseSqlServer(
            configuration.GetConnectionString("Ordering"),   // runtime identity, §7.1
            sql => sql.EnableRetryOnFailure()));

    // Projections, cache invalidators and command mappers live here, not in
    // Application — scanning only Application would skip them all (§6.2).
    services.AddPluggableFrom(typeof(OrderRepository).Assembly);

    // §9.5's inbox filter is common code, so it names DbContext rather than
    // this service's derived type — and this alias is what makes that legal.
    // GetRequiredService, not AddScoped<DbContext, OrderingDbContext>(): the
    // second form compiles, resolves and builds a SECOND context in the same
    // scope, so the inbox row commits in its own transaction and §9.5's
    // atomic-with-the-handler row silently becomes its non-atomic one.
    services.AddScoped<DbContext>(sp => sp.GetRequiredService<OrderingDbContext>());

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
    //
    // An instance, not a factory, and that is what makes the sentence above
    // true — a test resolves the registered descriptor and calls Add on it.
    // A factory would leave a test with nothing to reach, and re-registering
    // a second source is the replacement this is written to avoid.
    services.AddSingleton(
        new MessageTypeSource(typeof(V1.OrderPlaced).Assembly, typeof(Order).Assembly));

    services.AddSingleton(sp =>
        new MessageTypeMap(sp.GetRequiredService<MessageTypeSource>().Assemblies));

    // The map's factory is lazy, and this is what makes "a duplicate name
    // fails the host" true: ValidateOnBuild checks the call site and never
    // invokes it, so without a hosted service resolving the map the
    // constructor's throw lands on a background thread in a host that has
    // been ready for hours. Registered before the dispatcher, because hosted
    // services start in order.
    services.AddHostedService<MessageTypeMapValidator>();                 // §9.4

    // The schemas the dispatcher and the retention purge compose their
    // statements against (§9.4, §9.5). Values, because Common.Infrastructure
    // is every service's and cannot hold a literal — and both from ONE local,
    // so the two tables cannot end up naming different schemas.
    const string schema = "ordering";
    services.AddSingleton(new OutboxTable(schema));
    services.AddSingleton(new InboxTable(schema));

    // The retention windows, the batch size and the per-pass ceiling (§9.5).
    // Registered rather than const: §9.5 tells the reader to check the inbox
    // window against their broker's redelivery limits, and a number a chapter
    // says to check has to be one a service can change.
    services.AddSingleton(new RetentionPolicy());

    // The payload format, and the converters that put this service's value
    // objects in it. Money has a private constructor, so without its converter
    // it deserialises to a zero amount and a null currency and nothing says so
    // (§9.4).
    services.AddSingleton<JsonConverter, MoneyJsonConverter>();
    services.AddSingleton<OutboxJson>();

    // Plain ports — not open generics, so the §6.2 scan does not see them and
    // each needs a line here. Omitting one fails at DI resolution on the first
    // request that needs it, not at startup — unless ValidateOnBuild is on.
    services.AddScoped<IProductPriceReader, ProjectedPriceReader>();      // §6.4

    // No ICurrentUser and no AddHttpContextAccessor. Both were here until
    // PR-16 and both moved to AddCommonWebDefaults (§11.4, §13.2): neither type
    // names a service, and the implementation reads IHttpContextAccessor, which
    // arrives with a FrameworkReference that only Common.Web has. Every host
    // that authenticates has a current user, which is the criterion that helper
    // exists for.

    services.AddScoped<IIdempotencyStore, RedisIdempotencyStore>();       // §8.5

    // No ITokenCache, no ClientCredentialsHandler and no ServiceIdentityOptions
    // here. Ordering makes no synchronous outbound call — the price it needs
    // comes from a local projection (§6.4) and everything else it says goes
    // over the broker. The one host in this blueprint that calls a peer is the
    // BFF (§9.7), and outbound identity belongs to it (§11.5).

    // Registered by type, not by factory: the generic overload records an
    // ImplementationType, and the integration-test fixture matches on it to
    // locate and remove this exact descriptor (§12.4). MassTransit registers
    // its bus as a hosted service too, so the fixture cannot simply call
    // RemoveAll<IHostedService>() — and a factory registration here would
    // leave ImplementationType null, so the removal it does make would match
    // nothing and the dispatcher would drain rows underneath the assertions
    // about them.
    services.AddHostedService<OutboxDispatcher>();

    // §9.4's and §9.5's retention, in the one hosted service §9.5 asks for.
    // Registered last, so it is the first stopped: hosted services stop in
    // reverse, and a deploy that interrupts a purge loses nothing an hour will
    // not redo — where the dispatcher stopping first is what keeps the
    // transport up while it drains.
    services.AddHostedService<RetentionPurgeService>();

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
    services.AddMassTransitMessaging(configuration);                     // §9

    // Readiness checks live here, not in Common.Web — they need connection
    // strings, which the shared host package does not have (§13.5).
    services
        .AddHealthChecks()
        .AddSqlServer(configuration.GetConnectionString("Ordering")!, name: "sql", tags: ["ready"])
        .AddRedis(configuration.GetConnectionString("RedisCache")!, name: "redis-cache", tags: ["ready"])
        .AddRedis(configuration.GetConnectionString("RedisCoordination")!, name: "redis-coordination", tags: ["ready"])
        // No RabbitMQ line, deliberately: AddMassTransit above registers the
        // bus health check itself — "masstransit-bus", tagged ready (§13.5).
        .AddCheck<OutboxBacklogHealthCheck>("outbox", tags: ["observe"]);

    return services;
}
```

```csharp
// Ordering.Api/Program.cs — the only file that may reference Infrastructure.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Refuse to start if any registered service has a dependency the container
// cannot satisfy, or if a singleton captures a scoped one. Both are otherwise
// discovered on the first request that happens to need them.
builder.Host.UseDefaultServiceProvider(o =>
{
    o.ValidateOnBuild = true;
    o.ValidateScopes = true;
});

builder.AddCommonWebDefaults();                                     // §13.2
builder.Services.AddOrderingApplication();                          // §6.2
builder.Services.AddOrderingInfrastructure(builder.Configuration);  // above

// Ordering's permission policies (§11.4). Deliberately not inside either
// helper: Application knows nothing about HTTP, and Common.Web must not know
// Ordering's names. A policy named by an endpoint and registered nowhere
// throws on the first request that reaches it — never at startup.
//
// RequirePermission rather than RequireClaim("permission", …), and constants
// rather than literals: the claim type belongs to Common.Web so a policy and a
// resource check cannot drift apart, and the name is written twice — here and
// at the endpoint — so the compiler should be the thing comparing them.
builder.Services
    .AddAuthorizationBuilder()
    .AddPolicy(OrderingPermissions.Write, p => p.RequirePermission(OrderingPermissions.Write))
    .AddPolicy(OrderingPermissions.Cancel, p => p.RequirePermission(OrderingPermissions.Cancel));

WebApplication app = builder.Build();

// Middleware order is behaviour, not formatting. Each line below depends on
// the ones above it, and getting it wrong fails silently rather than loudly.
app.UseExceptionHandler();        // §10.5 — outermost, catching middleware faults
app.UseCorrelationId();           // §10.4 — above everything else that logs
// §10.5's promise applied to the statuses no handler produces: a challenge
// and a forbid are written by the middleware below and carry NO BODY, so the
// platform's one error shape had two holes in it until PR-17 measured a 401.
// Since .NET 8 this middleware writes them through IProblemDetailsService.
app.UseStatusCodePages();         // §10.5 — 401 and 403 as problem+json
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
| `UseCorrelationId` before everything that logs, `UseExceptionHandler` alone above it | Early log lines and traces have no correlation ID, so the one request you need to follow is the one you cannot. The handler is the deliberate exception — it has to be outermost to catch faults in the middleware below it, and it reaches the ID through `Request.Headers` rather than the log scope ([§10.4](10-api-gateway.md)) |
| `UseAuthentication` before `UseAuthorization` | **Every authenticated request 401s** — in a `WebApplication` too. Omitting a call is repaired by auto-insertion; writing both in the wrong order is not, because the markers they set suppress it. See the callout below |
| `UseAuthentication` before `UseRateLimiter` (gateway only) | Same empty `User`, but this one does not 403 — §10.3's per-user partition key silently degrades to per-IP, and everyone behind one NAT shares a single bucket. **Silent is the measured half**: reversing the two leaves every test in `Gateway.Api.Tests` green, the authenticated-partition test included, so nothing in the repository is watching this line (see below) |
| `UseForwardedHeaders` above the limiter, and **below** the handler and the correlation ID (gateway only) | Two rules meeting, and this sample had them the wrong way round until PR-17: putting it first means a fault parsing a forwarded header unwinds past no exception handler, and anything the middleware logs runs outside the correlation scope. Neither of those two reads the address, so nothing is lost by letting them wrap it — while the limiter, which does read it, stays below. `ForwardedHeadersTests` covers the lower half: below `UseRateLimiter`, two forwarded addresses collapse onto the one connection the gateway can see |
| Both before endpoint mapping | `RequireAuthorization` has nothing to evaluate against |
| Health endpoints mapped **anonymous** | Probes 401, Kubernetes reads that as unhealthy, and the pod is killed in a loop |

Registration without middleware is the quiet failure mode here. `AddRateLimiter`
succeeds and does nothing if `UseRateLimiter` is absent — no error, no warning,
no failing test unless one specifically asserts on a limit.

> **`AddAuthentication` is not in that class, and saying it was cost this table
> a wrong row — twice, in opposite directions.** `WebApplication` adds the
> authentication and authorization middleware itself whenever the matching
> services are registered, so **deleting** `app.UseAuthentication()` from a
> service host changes nothing observable — verified by deleting it from
> `Catalog.Api/Program.cs`, after which every test in the repository still
> passed. That is the correction this callout was written for.
>
> **Reversing the two is a different matter, and the row above used to promise
> it was harmless.** Auto-insertion is suppressed by the markers the explicit
> calls set, and it repairs an *omission* rather than an ordering: with both
> calls present in the wrong order, authorization evaluates against a `User`
> nothing has populated and challenges. Measured through a real
> `WebApplication` over three pipelines — correct order 200, **reversed 401**,
> neither call 200 — which is the only arrangement of those three that a reader
> would not predict.
>
> So the honest summary is that the framework protects you from forgetting a
> line and not from misplacing one. Write both, in this order, and let
> `AuthenticationMiddlewareTests` hold the claim: it drives all three pipelines
> and is the regression guard if a release ever stops auto-inserting.

The **gateway** has its own pipeline and is the only place rate limiting is
applied (§10.1); a service behind it does not call `UseRateLimiter`:

```csharp
// Gateway.Api/Program.cs
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddCommonWebDefaults();                                  // §13.2

// §10.1's body ceiling, and the only one in the platform. Kestrel's 30 MB is a
// web server's default rather than a choice; GatewayLimits argues the number.
// Enforced where the body is READ, which is inside the forwarder — so a request
// that fails authorization is refused without its size being considered.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = GatewayLimits.MaxRequestBodyBytes);

// §10.1's response compression. The providers and the compressible MIME types
// are the framework's defaults, deliberately — that list omits
// application/problem+json, which is what keeps §10.5's error bodies (the one
// place a client-supplied value is reflected back) out of the compressed set.
// EnableForHttps is the whole of ADR-020, and it is what makes compression
// happen at all: the block below rewrites Request.Scheme from the ingress's
// X-Forwarded-Proto, and this middleware decides at the first WRITE, so the
// scheme it reads is https even though the hop was plain.
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);

// YARP is registered here and configured from the "ReverseProxy" section
// shown in §10.2. Without this, MapReverseProxy() throws at startup and the
// entire routing configuration is inert.
builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddRateLimiter(/* §10.3 */);

// Every policy §10.2's routes name that Common.Web does not already register.
// "authenticated" comes from AddCommonWebDefaults; this one is the gateway's
// own, and it is a permission check rather than a role check for the reason
// §11.4 gives. A route naming a policy nobody registered fails CLOSED and
// loudly: the config load throws out of MapReverseProxy() below, naming the
// policy and the route, so the process does not start (§10.2).
builder.Services
    .AddAuthorizationBuilder()
    .AddPolicy(GatewayPermissions.InventoryAdmin, p => p.RequirePermission(GatewayPermissions.InventoryAdmin));

// Both of the following are conditional on the deployment shape, and each is
// REQUIRED once switched on. "Off" and "on but unconfigured" are different
// states: the first is a valid topology, the second is a silent defect.
bool behindProxy = builder.Configuration.GetValue<bool>("Ingress:Enabled");
bool corsEnabled = builder.Configuration.GetValue<bool>("Cors:Enabled");

if (behindProxy)
{
    // A load balancer or Ingress sits in front (§15.3), so
    // Connection.RemoteIpAddress is the proxy on every request. Without this
    // the rate limiter partitions all anonymous traffic into ONE bucket and
    // its per-client limit becomes a global cap — configured, running, and a
    // denial of service against legitimate users rather than a defence.
    //
    // Read HERE and not inside the callback: an options callback runs when the
    // options are first resolved, so a missing section read from inside one
    // throws on a request rather than at startup — the deferral this pair of
    // flags exists to avoid.
    string[] trusted = builder.Configuration.GetRequiredSection("Ingress:TrustedNetworks").Get<string[]>()!;

    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // Trust only the ingress. Left empty, ASP.NET Core trusts nothing
        // beyond loopback and silently keeps the proxy's address; opened to
        // all, any client can spoof its partition key and bypass the limit.
        //
        // KnownIPNetworks and System.Net.IPNetwork, both spelled deliberately:
        // KnownNetworks carries ASPDEPR005 at .NET 10 — an error under
        // ADR-019 — and the IPNetwork it held is the one
        // Microsoft.AspNetCore.HttpOverrides declares, which the using above
        // brings into scope in place of the framework type the new property
        // takes. This sample said both the other way and did not compile.
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
        foreach (string cidr in trusted)
            o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
    });
}

// Only when browsers call the gateway directly rather than through a CDN or
// same-origin edge (§10.2). Enabled but unset would yield WithOrigins([]),
// which rejects every browser request while starting cleanly — surfacing as a
// CORS error in a console rather than as the missing setting it is (§15.4).
// Hoisted out of the callback for the reason given above: the CORS options are
// built on the first request that needs them.
if (corsEnabled)
{
    string[] origins = builder.Configuration.GetRequiredSection("Cors:Origins").Get<string[]>() ?? [];

    // GetRequiredSection proves the section EXISTS and nothing more, and four
    // review rounds each found a value the previous check admitted:
    //
    //   ""                       binds from `Cors__Origins__0=`; WithOrigins
    //                            takes it and matches no browser. "Blank
    //                            counts as missing", §11.3's rule for
    //                            Identity:Authority
    //   "*"                      invalid beside AllowCredentials below, and
    //                            ASP.NET Core only says so when the policy is
    //                            BUILT — on a preflight, not at startup
    //   "https//spa.example"     one missing colon, compared literally
    //   "https://spa.example/"   every parsed property agrees it is fine;
    //                            the browser sends no trailing slash
    //   "https://spa.example:443" a browser omits the scheme's default port
    //
    // Seven rounds of that produced six clauses and a seventh value, so the
    // check stopped enumerating prohibitions. GetLeftPart(UriPartial.Authority)
    // IS the canonical origin — scheme, host, and a port only when it is not
    // the default — so demanding the configured text equal it accepts exactly
    // what a browser sends and rejects every variant at once.
    int[] malformed =
    [
        .. origins
            .Select((origin, index) => (origin, index))
            .Where(entry =>
                !Uri.TryCreate(entry.origin, UriKind.Absolute, out Uri? parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
                parsed.UserInfo.Length > 0 ||
                !string.Equals(entry.origin, parsed.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
            .Select(entry => entry.index)
    ];

    // Three guards, not one condition. They fail for different reasons and a
    // reader needs to be told which: an empty list has no index to report, and
    // the wildcard's whole diagnostic is the word AllowCredentials — collapsing
    // them into one `||` produces "unusable at index " with nothing after it,
    // and loses the only sentence that makes the wildcard case actionable.
    if (origins.Length == 0 || origins.Any(string.IsNullOrWhiteSpace))
        throw new InvalidOperationException("'Cors:Origins' is enabled but holds no usable origin.");

    if (origins.Any(o => o == "*"))
        throw new InvalidOperationException("'Cors:Origins' contains '*', which AllowCredentials below forbids.");

    // Indexes, never the values: credentials in the authority are one of the
    // rejected shapes, and an exception message reaches the logs — where
    // §13.4's redactor scrubs keyed attributes and cannot see a secret
    // interpolated into a string. The guard that rejects a password must not
    // be the thing that publishes one.
    if (malformed.Length > 0)
        throw new InvalidOperationException($"'Cors:Origins' is not an origin at index {string.Join(", ", malformed)}.");

    builder.Services
        .AddCors(o =>
            o.AddDefaultPolicy(p => p
                .WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()));
}

WebApplication app = builder.Build();

app.UseExceptionHandler();
app.UseCorrelationId();           // assigns the ID if the client sent none

// High enough to wrap every writer below it, because this middleware acts by
// replacing the response body feature. Nothing here is an ordering rule a test
// can catch — moving it below the auth pair changes no observable response,
// measured — but its ABSENCE is: AddResponseCompression succeeds and compresses
// nothing without it, the same quiet shape the limiter's registration has.
app.UseResponseCompression();     // §10.1, ADR-020

app.UseStatusCodePages();         // §10.5 — 401 and 403 as problem+json

// Above everything that reads the client address, and below the two that do
// not. Until this runs the address is the proxy's; skipped when the gateway IS
// the edge (Compose), where it is already the client and trusting a forwarded
// header would let any caller choose its own rate-limit bucket.
if (behindProxy)
    app.UseForwardedHeaders();

if (corsEnabled)
    app.UseCors();

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

> **Nothing tests this ordering, and the attempt to test it is the evidence.**
> PR-17 added a test proving two authenticated subjects hold independent
> buckets — the property the subject partition key exists for — and then ran it
> against a pipeline with `UseRateLimiter` moved above `UseAuthentication`. It
> passed, and so did every other test in that project. The limiter is still
> live under the reversal, because the anonymous window still rejects at its
> hundredth request; why the authenticated bucket does not collapse onto the
> shared fallback there is unexplained, and an unexplained pass is not a guard.
>
> So the row above is two claims of different standing. That the failure is
> **silent** is measured. That the partition **degrades to per-IP** is
> reasoned from the code and is not observed by anything. Keep the order, and
> do not believe a test is holding it — the same posture the callout below
> takes for `app.UseAuthentication()` itself.

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

## 4.3 What may be shared between services

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

## 4.4 Pinning the toolchain and packages

`global.json` pins the SDK to one exact patch, so every developer and every CI
agent compiles with the same compiler and analysers. Without it, a machine with
a newer SDK can produce different diagnostics — or different behaviour — from
the build that was reviewed. The trap below is why the pin says `disable`
rather than `latestPatch`: only one of the two makes that first sentence true.

```json
{
  "sdk": {
    "version": "10.0.302",
    "rollForward": "disable"
  }
}
```

> **Trap — `latestPatch` is not a pin, and the sentence above is only true
> without it.** `latestPatch` accepts any patch inside the feature band, so it
> resolves to whatever the machine happens to have: a developer on `10.0.305`
> compiles with those analysers while CI, which `setup-dotnet` gives exactly the
> version named here, compiles with these. Since ADR-019 makes analyser output a
> build gate, that divergence does not show up as a warning — it shows up as a
> build that is green on every machine and red in CI, reproducing nowhere. This
> repository has had one.
>
> `disable` is what closes it: the version named is the version used, or
> `dotnet` refuses to run at all rather than quietly choosing another. Feature
> bands were never the exposure — `latestPatch` already declined to cross them
> (`10.0.100` rejects `10.0.302`) — patches were, and they are the ones that
> ship analyser changes.
>
> The cost is the intended one and it is larger than it was: every machine needs
> this exact patch, not merely one in the band, so a bump is a deliberate edit
> here that everyone installs before they can build. That is the same trade as
> the exact package pins below, applied to the compiler that reads them.
>
> **One "machine" cannot install anything, and that makes the pin a two-file
> edit.** The build stage of each service image ([§15.2](15-cicd-deployment.md))
> runs whatever SDK its base tag carries, so those `FROM` lines name this exact
> patch too and a bump here is a bump there in the same change. They floated on
> `10.0-noble` once, on the argument that copying this file in would make any
> mismatch loud. It did: the tag moved a feature band, `disable` refused it, and
> every image stopped building on a restore that exited 155. Loud is the right
> behaviour for a drift nobody chose and the wrong one for the artefact that
> ships.

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
    <!-- The base package, referenced only by Common.Infrastructure: §9.5's
         InboxFilter writes through the service's DbContext because sharing
         the handler's transaction is the point of it, and common code may
         name the base type but never a provider. Every service's own
         Infrastructure takes the provider below instead. -->
    <PackageVersion Include="Microsoft.EntityFrameworkCore" Version="10.0.0" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.0" />
    <!-- Design-time only, referenced by each *.Migrator with PrivateAssets.
         `dotnet ef migrations add` (§7.4) needs it in the startup project and
         nothing at runtime does — the version is pinned to the line above
         because the tool and the runtime move together. -->
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.0" />
    <!-- Transitive, pinned deliberately, and the same shape as Microsoft.OpenApi
         below. The package above reaches it twice — through
         Microsoft.Build.Tasks.Core and through
         Microsoft.CodeAnalysis.Workspaces.MSBuild — and both floors resolve to
         9.0.0, which carries eight advisories. NU1903 turns that into a failed
         restore, so this pin is what makes the line above restorable at all.

         PrivateAssets on the Design reference means it never ships in an image.
         The restore fails anyway, and rightly: a design-time supply chain is
         still a supply chain, and this one runs on a developer's machine with
         their credentials. -->
    <PackageVersion Include="System.Security.Cryptography.Xml" Version="10.0.10" />
    <PackageVersion Include="Microsoft.Extensions.Caching.Hybrid" Version="10.0.0" />
    <!-- HybridCache's L2 (§8.2): AddStackExchangeRedisCache and
         RedisCacheOptions. The IDistributedCache implementation over
         StackExchange.Redis — a separate package from both, and the one that
         actually stores an entry in Redis. -->
    <PackageVersion Include="Microsoft.Extensions.Caching.StackExchangeRedis" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Http.Resilience" Version="10.0.0" />
    <!-- The container and logging contracts Common.Application compiles
         against (§6.2, §13.3). ASP.NET Core's shared framework carries both,
         but Application takes no FrameworkReference — §4.2 puts the web on the
         other side of the boundary, and the building block that defines
         IPipelineBehavior sits on this one. -->
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" />
    <!-- AddOptions<RedisCacheOptions>().Configure (§8.2) — options
         configuration called directly by Common.Infrastructure, so the
         assembly is referenced directly, on the register's honesty rule. -->
    <PackageVersion Include="Microsoft.Extensions.Options" Version="10.0.0" />
    <!-- The same argument one row down: AddCatalogInfrastructure names
         IConfiguration in its signature (§4.2) and *.Infrastructure is not a
         web project, so it pays for the contract as a package. -->
    <PackageVersion Include="Microsoft.Extensions.Configuration.Abstractions" Version="10.0.0" />
    <!-- The migrator's job host (§7.4). ASP.NET Core's shared framework carries
         the generic host, and a *.Migrator is a console job with no listener —
         so it is the one project shape here that pays for hosting as a
         package. -->
    <PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.0" />
    <!-- IHostEnvironment, for RedisKeys' key prefix (§8.3). Rides in the
         shared framework and in the Hosting meta-package above, but
         Common.Infrastructure is a library that takes neither — it pays for
         the contract as a package, like the abstractions rows above. -->
    <PackageVersion Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.0" />
    <PackageVersion Include="Dapper" Version="2.1.66" />
    <!-- SqlConnection itself, for §6.5's IDbConnectionFactory. EF's SqlServer
         provider already carries it transitively at exactly this version;
         the explicit pin exists because *.Infrastructure constructs the type
         by name, and referencing what is actually used keeps the register
         honest. -->
    <PackageVersion Include="Microsoft.Data.SqlClient" Version="6.1.1" />
    <!-- Exact major. v9 is commercially licensed — see ADR-003. The core
         package is a transitive of the transport one, pinned separately
         because two things reference it directly: the harness smoke, since
         the in-memory harness is core API and a test project that uses no
         transport must not claim one, and Common.Infrastructure since PR-14,
         for the IPublishEndpoint the outbox dispatcher publishes the Broker
         lane through (§9.4). Same version as the transport: they ship as one
         release. -->
    <PackageVersion Include="MassTransit" Version="8.5.3" />
    <PackageVersion Include="MassTransit.RabbitMQ" Version="8.5.3" />
    <PackageVersion Include="StackExchange.Redis" Version="2.9.11" />
    <PackageVersion Include="FluentValidation" Version="12.0.0" />
    <!-- AddValidatorsFromAssemblyContaining (§4.2's registration sample) lives
         in this separate package, not in FluentValidation itself. Same
         version, same licence, same publisher — pinned together. -->
    <PackageVersion Include="FluentValidation.DependencyInjectionExtensions" Version="12.0.0" />
    <PackageVersion Include="Scrutor" Version="6.1.0" />
    <PackageVersion Include="Yarp.ReverseProxy" Version="2.3.0" />
    <!-- The BFF's one synchronous hop (§9.7). Grpc.* majors move on their own
         schedule, independent of the .NET release — exactly the case for
         pinning rather than trusting the SDK to carry a compatible version. -->
    <PackageVersion Include="Grpc.Net.ClientFactory" Version="2.71.0" />
    <PackageVersion Include="Grpc.AspNetCore" Version="2.71.0" />
    <PackageVersion Include="Grpc.Tools" Version="2.71.0" />
    <PackageVersion Include="Google.Protobuf" Version="3.29.3" />
    <!-- §11.3's JWT bearer handler, referenced by Common.Web. Not carried by
         Microsoft.AspNetCore.App — the shared framework has the authentication
         abstractions and the cookie handler, and the JWT one has been a package
         since ASP.NET Core 3.0. Same version line as the runtime: it ships with
         the framework and moves with it. -->
    <PackageVersion Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="10.0.10" />
    <!-- PR-07's OpenAPI deliverable (Appendix C): the framework's own document
         generator — AddOpenApi/MapOpenApi, document only, no UI. -->
    <PackageVersion Include="Microsoft.AspNetCore.OpenApi" Version="10.0.10" />
    <!-- Transitive, pinned deliberately: every patch of the package above
         floors this at 2.0.0, which carries GHSA-v5pm-xwqc-g5wc, and NuGet
         resolves a floor to its lowest. NU1903 turns that into a failed
         restore, so the pin is what makes the line above buildable at all —
         CentralPackageTransitivePinningEnabled is why it works. -->
    <PackageVersion Include="Microsoft.OpenApi" Version="2.11.0" />
  </ItemGroup>
  <ItemGroup Label="Telemetry">
    <PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="1.17.0" />
    <PackageVersion Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.17.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.17.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.Http" Version="1.17.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.Runtime" Version="1.17.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.EntityFrameworkCore" Version="1.17.0-beta.1" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.StackExchangeRedis" Version="1.17.0-beta.1" />
    <!-- No AspNetCore.HealthChecks.Rabbitmq beside these two, and the absence
         is a decision (PR-13): its parameterless AddRabbitMQ resolves an
         IConnection nothing registers — MassTransit does not expose one — and
         the bus health check AddMassTransit registers itself answers the
         question better, endpoints included. -->
    <PackageVersion Include="AspNetCore.HealthChecks.SqlServer" Version="9.0.0" />
    <PackageVersion Include="AspNetCore.HealthChecks.Redis" Version="9.0.0" />
  </ItemGroup>
  <ItemGroup Label="Test">
    <!-- Test packages are pinned for exactly the same reason as runtime ones.
         xUnit v2 → v3 changed IAsyncLifetime from Task to ValueTask (§12.4):
         a major that drifts in silently breaks every fixture in the repo. -->
    <PackageVersion Include="xunit.v3" Version="3.1.0" />
    <!-- The fixture contract (IAsyncLifetime, TestContext) for
         Catalog.TestSupport, which is a Library — xunit.v3 itself refuses
         non-Exe output and names this package as the alternative. Pinned to
         the runner's version because they ship as one release. -->
    <PackageVersion Include="xunit.v3.extensibility.core" Version="3.1.0" />
    <!-- The VSTest adapter, and the reason `dotnet test` discovers anything.
         Microsoft.NET.Test.Sdk is the host; it finds tests through an adapter,
         and xunit.v3 does not carry one. Without this line the build succeeds,
         the run reports zero tests, and CI is green on a suite it never ran. -->
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.5" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />
    <!-- TestServer without a host project. Mvc.Testing carries this
         transitively, but WebApplicationFactory<T> needs an entry point and
         Common.Web has none — it is a library (§4.1). Referencing what is
         actually used keeps the register honest. -->
    <PackageVersion Include="Microsoft.AspNetCore.TestHost" Version="10.0.0" />
    <PackageVersion Include="Shouldly" Version="4.3.0" />
    <PackageVersion Include="NSubstitute" Version="5.3.0" />
    <PackageVersion Include="Testcontainers.MsSql" Version="4.6.0" />
    <PackageVersion Include="Testcontainers.Redis" Version="4.6.0" />
    <PackageVersion Include="Testcontainers.RabbitMq" Version="4.6.0" />
    <!-- Transitive of the three rows above, pinned deliberately, and the third
         instance of the shape Microsoft.OpenApi and System.Security.Cryptography.Xml
         already carry. Testcontainers 4.6.0 floors this at 2024.2.0 for its SSH
         port-forwarding path, and every version through 2025.1.0 carries
         GHSA-q939-rpr3-3284 — a malicious SCP server escaping the download
         directory through traversal sequences in a filename. NU1903 turns that
         into a failed restore, so the pin is what makes the three rows above
         buildable at all. Nothing here downloads over SCP; the pin is not a
         judgement that the advisory is reachable, only that a vulnerable
         package resolving into the graph is not a thing to carry. -->
    <PackageVersion Include="SSH.NET" Version="2026.0.0" />
    <PackageVersion Include="Respawn" Version="6.2.1" />
    <PackageVersion Include="WireMock.Net" Version="1.8.11" />
    <PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="9.9.0" />
    <!-- ServiceCollection itself. §6.2's registration test and §6.3's ordering
         test resolve from a real container, and the abstractions package the
         source references has none to build — it is contracts only. -->
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="10.0.0" />
    <!-- ConfigurationBuilder and AddInMemoryCollection. AddRedisConnections
         takes IConfiguration (§8.2), and its tests build a real one rather
         than substitute the contract — the same argument as the container
         row above, one abstraction over. -->
    <PackageVersion Include="Microsoft.Extensions.Configuration" Version="10.0.0" />
    <!-- The architecture gates of §4.2, which PR-07 turns from a review
         comment into a build failure. A major bump can change which rules
         exist, so it fails loudly rather than quietly stopping to enforce. -->
    <PackageVersion Include="NetArchTest.Rules" Version="1.3.2" />
    <!-- The in-memory reader the observability tests read back through:
         §13.4's redaction tests, which that chapter prints one of, the
         meter-coverage tests guarding §13.2's meter list in
         Common.Web.Tests, and the Redis client-span tests in
         Common.Infrastructure.Tests proving AddRedisConnections instruments
         its own connections. Test-only: nothing in src/ exports in
         memory. -->
    <PackageVersion Include="OpenTelemetry.Exporter.InMemory" Version="1.17.0" />
  </ItemGroup>
</Project>
```

**Every package means every package**, including the test ones — those are where
the version numbers here came from, and the list is the same set [Appendix B](appendix-b-licences.md)
registers. The two files answer different questions about the same dependencies:
Appendix B says whether a licence is acceptable, this file says which version CI
will actually resolve. A package in one and not the other is how a licence
boundary gets crossed by a restore, so PR-01 ships that check:
`.github/licence-gate/` reads this file and [Appendix B](appendix-b-licences.md)
as text, matches on the backticked package identities the register carries, and
fails on a pin nobody cleared.

Appendix B is the wider list, though, and three kinds of row in it will never
have a pin here. A check that does not know them reports false positives until
somebody stops reading its output:

- **Infrastructure products** — SQL Server, Redis, RabbitMQ, Keycloak — are
  licensable in their own right but are containers, not packages. The
  `StackExchange.Redis`, `Testcontainers.*` and `AspNetCore.HealthChecks.*` pins
  above are the *client libraries* that talk to them: a different artefact under
  a different licence. Match on package identity, never on the product a package
  is named after.
- **The Aspire packages** of [§14.2](14-local-development.md) are deliberately
  unpinned. Aspire is optional, nothing references it until the AppHost is
  adopted, and its API has moved fast enough that pinning a version this
  document cannot keep current would be worse than pinning none. Adopting
  Aspire means adding the pins here in the same change — the licence rows
  already exist, so the gap this file shows is the reminder.
- **Either/or rows** — `Shouldly` *or* `AwesomeAssertions` — pin only the chosen
  library. Clearing a licence for an alternative is not a commitment to restore
  it. Keep such rows rare and word them as alternatives, because a row that
  reads as two dependencies when it means one is how this check starts being
  ignored.

The versions are those current at the review date in the header. A blueprint
cannot keep them accurate, and neither does the gate below — it reads package
identities and never a `Version`. Currency and vulnerability scanning are a
separate obligation, and the tooling for them is not yet in this repository.

> **Trap — pinning floors instead of versions.** Writing `Version="8.*"`, or
> treating the file as a set of minimums to be "reviewed quarterly", means a
> routine `dotnet restore` can resolve forward across a major boundary. Where
> that boundary is also a **licence** boundary — MassTransit v8 → v9 is the live
> example (Appendix B) — the obligation is acquired by a restore rather than by a
> decision. Pin exact versions and upgrade deliberately.

What the gate does enforce is narrower, and the line matters. It fails the build
on a pin [Appendix B](appendix-b-licences.md) does not register, on a registered
licence outside `.github/licence-gate/allowed-licences.txt`, and on a registered
identity that is pinned nowhere. All three are questions about **identity and
licence**; none is a question about whether a version is current or safe.

Licence drift is only caught reliably by tooling — a convention will not survive
the twentieth dependency. The gate runs ahead of the build rather than after it
([§15.1](15-cicd-deployment.md)), which is what this file's central pinning
buys: every dependency the repository has is declared in one place, so nothing
needs restoring before the list can be read.

> **Trap — the sample above is a copy.** The gate reads
> `Directory.Packages.props`; the fenced block in this section is a second
> transcription of it, and nothing about a passing build proves the two agree.
> The gate closes that itself by comparing them — but the failure it reports is
> against this chapter, not the props file, because the file is what CI
> restores and the chapter is what a reader believes.

## 4.5 Adding a service

**Four** of §4.1's six services share the shape below — Catalog, Ordering,
Inventory and Payments — and writing the fourth by hand is how it ends up
subtly different from the first three. One command renders it instead:

```bash
python tools/new-service/new_service.py Yankee --port 5199
```

The name and the port are a probe rather than a real service, and that is
deliberate: this sample said `Ordering --port 5101` until PR-18 made both of
those taken, at which point the command a reader copies from the chapter
raised `ScaffoldError` — and the paragraph below, which says the run refuses a
port another service already publishes, made the chapter contradict its own
sample. A probe cannot quietly become a service later.

It writes §4.1's five service projects, its three test projects and its
`TestSupport` library — nine in all, and §4.1 is explicit that the last is not
a test project — with everything the service template has accumulated: the
`DbContext` and its conventions
([§7.2](07-persistence.md)), `EfUnitOfWork` ([§6.3](06-cqrs.md)), the
connection factory ([§6.5](06-cqrs.md)), the readiness checks
([§13.5](13-observability.md)) — SQL registered by the service, the bus's
`masstransit-bus` by MassTransit itself — the bus registration of
[§9](09-messaging.md), whose eager read means a scaffolded host refuses to
start without `ConnectionStrings:RabbitMq`, the migration job host
([§7.4](07-persistence.md)), the `InitialCreate` migration that creates the
schema and the `AddOutbox` one beside it — §9.4's table is wiring every
service has, and a service carrying the dispatcher without it would log a
failed claim twice a second from its first boot — the outbox itself with its
empty allow-list mapper, §9.5's inbox filter and retention purge, §11.3's JWT
validation, both images ([§15.2](15-cicd-deployment.md)) and
§4.2's architecture gates. The migrations it copies are `InitialCreate`,
`AddOutbox`, `AddInbox` and `AddOutboxRetentionIndex` — the messaging tables
ship with the dispatcher that reads them, because a service carrying the
dispatcher without its table logs a failed claim twice a second from its first
boot. It then edits five shared files: `Platform.slnx`, the Compose pair and
its `infra-only` exclusion, `.env.example`, and the ports table in
`deploy/compose/README.md` ([§14.1](14-local-development.md)). The new service
builds and its **fifty-six** tests pass before a line of it is written,
**thirty** of them against real SQL Server and RabbitMQ containers — counts
measured against a rendered service by PR-18, which found them reading
forty-one and sixteen three PRs after they stopped being true.

**There is no template directory, and that is the design.** The script reads
`src/Services/Catalog` at run time, so there is exactly one copy of the
wiring — the copy CI builds and `dotnet test` exercises — and an improvement to
the template reaches the next service the next time it runs. A tokenised copy
beside it would be a second `DbContext`, a second migrator host and a second
Dockerfile that nothing builds and nothing reconciles.

**It copies no domain.** Catalog's `Product`, its command, its query and its
endpoints are excluded by name; what a new service inherits is PR-07's state
with the later wiring on it, not PR-10's state with the nouns changed.
Renaming an aggregate would hand the next service a deletion job and a
vocabulary it did not choose. Three things therefore arrive with the first real
slice rather than with the scaffold — each with the part of it that needs them,
not as a set, and each noted at the line concerned in the generated code. The
first handler of either kind brings the application-test container wiring and
the test that §6.2's scan produced a registration; the first validator brings
the test for the validator scan; the first *query* brings `Dapper`, which a
command-only slice must not add. Both those scans fail silently when lost,
which is why the tests are named rather than left to be missed.

The `AssemblyMarker` runs the other way, and the distinction is worth keeping
straight. The scaffold **emits** it, because the §4.2 gates must name a type in
an assembly that has none; the first aggregate is when it is **deleted** and
the gates re-anchor on that aggregate. It is the one generated file written to
be removed, and its own doc comment says so.

> **The scaffold fails loudly or not at all.** Every piece of Catalog text it
> names must match exactly once, the whole render is built in memory and
> validated before a single file is created, and any file under
> `src/Services/Catalog` it cannot classify as template or slice stops the run.
> The price of having no second copy is that the first one moves; the price is
> paid by refusing, never by silently emitting a service that still names
> Catalog. Its own tests render this repository for the same reason — a fixture
> tree would test the script against a template that cannot drift.
>
> **That guarantee is about validation, not about the write.** A run the
> scaffold refuses writes nothing; a disk that fills up halfway through the
> write leaves a partial tree, and no transaction log is kept to undo it. The
> target is a git checkout — `git status` shows exactly what landed — and a
> second, untested rollback mechanism for something version control already
> does is not worth having.

`--port` is required and never derived. A port is an allocation recorded in
§14.1 and in `deploy/compose/README.md`; a script that guessed one would
quietly disagree with a printed chapter. The run refuses a port another service
already publishes.

Three things are outside it, and none is silently missing: the gateway route
([§10.2](10-api-gateway.md)) — the route belongs to the gateway's
configuration, not the service's tree — the Helm chart
([§15.3](15-cicd-deployment.md)), and a Worker host in place of an API, which
Shipping and Notifications take (§4.1) and which joins the script with the
first one built.

**The scaffold refuses `Shipping` and `Notifications` by name until it can.**
Documenting the gap left the script willing to render either as an API service,
which would have contradicted §4.1 quietly — Notifications has no Domain
project at all. A note is not a guard, and the two names come off that list
with the PR that adds the mode.

---

[← §3 Bounded contexts](03-bounded-contexts.md) · [Index](README.md) · [§5 Tactical DDD →](05-tactical-ddd.md)
