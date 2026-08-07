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
│   ├── Common.Web.Tests/               convention the services use (§12.1).
│   │                                   Common.Web is a library with no entry
│   │                                   point, so its suite drives a TestServer
│   │                                   rather than a WebApplicationFactory
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
| `*.Api` | Application, Infrastructure (**composition root only**) | another service's projects |

`*.Domain` having no third-party dependencies is what makes domain tests
instant and mock-free. It is worth defending. Enforce it with an architecture
test rather than a code review convention:

```csharp
[Fact]
public void Domain_references_only_common_domain_and_the_framework()
{
    // The table's rule is an allow-list — "Common.Domain and nothing else" —
    // so the gate is one too: a blacklist can only ban the libraries someone
    // thought to name, and Common.Application, another service's Domain or a
    // new package all slip past it. System.Text.Json is the one framework
    // assembly the table bans by name: a domain type must not serialise
    // itself.
    IEnumerable<string> referenced = typeof(Order).Assembly
        .GetReferencedAssemblies()
        .Select(a => a.Name!);

    referenced.ShouldAllBe(name =>
        name == "Common.Domain" ||
        (name.StartsWith("System.") && !name.StartsWith("System.Text.Json")));
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
    foreach (Assembly assembly in new[] { typeof(PlaceOrderHandler).Assembly, typeof(Order).Assembly })
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
    services.AddSingleton<MessageTypeSource>(_ =>
        new MessageTypeSource(typeof(V1.OrderPlaced).Assembly, typeof(Order).Assembly));

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
    services
        .AddHealthChecks()
        .AddSqlServer(configuration.GetConnectionString("Ordering")!, name: "sql", tags: ["ready"])
        .AddRedis(configuration.GetConnectionString("RedisCache")!, name: "redis-cache", tags: ["ready"])
        .AddRedis(configuration.GetConnectionString("RedisCoordination")!, name: "redis-coordination", tags: ["ready"])
        .AddRabbitMQ(name: "rabbitmq", tags: ["ready"])
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
builder.Services
    .AddAuthorizationBuilder()
    .AddPolicy("orders:read", p => p.RequireClaim("permission", "orders:read"))
    .AddPolicy("orders:write", p => p.RequireClaim("permission", "orders:write"))
    .AddPolicy("orders:cancel", p => p.RequireClaim("permission", "orders:cancel"));

WebApplication app = builder.Build();

// Middleware order is behaviour, not formatting. Each line below depends on
// the ones above it, and getting it wrong fails silently rather than loudly.
app.UseExceptionHandler();        // §10.5 — outermost, catching middleware faults
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
| `UseCorrelationId` before everything that logs, `UseExceptionHandler` alone above it | Early log lines and traces have no correlation ID, so the one request you need to follow is the one you cannot. The handler is the deliberate exception — it has to be outermost to catch faults in the middleware below it, and it reaches the ID through `Request.Headers` rather than the log scope ([§10.4](10-api-gateway.md)) |
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
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddCommonWebDefaults();                                  // §13.2

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
// §11.4 gives. A route naming a policy nobody registered does not fail closed:
// YARP rejects the route at config load and drops it, so that path 404s while
// every other route keeps serving.
builder.Services
    .AddAuthorizationBuilder()
    .AddPolicy("inventory:admin", p => p.RequireClaim("permission", "inventory:admin"));

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
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // Trust only the ingress. Left empty, ASP.NET Core trusts nothing
        // beyond loopback and silently keeps the proxy's address; opened to
        // all, any client can spoof its partition key and bypass the limit.
        o.KnownNetworks.Clear();
        o.KnownProxies.Clear();
        foreach (string cidr in builder.Configuration.GetRequiredSection("Ingress:TrustedNetworks").Get<string[]>()!)
            o.KnownNetworks.Add(IPNetwork.Parse(cidr));
    });
}

// Only when browsers call the gateway directly rather than through a CDN or
// same-origin edge (§10.2). Enabled but unset would yield WithOrigins([]),
// which rejects every browser request while starting cleanly — surfacing as a
// CORS error in a console rather than as the missing setting it is (§15.4).
if (corsEnabled)
{
    builder.Services
        .AddCors(o =>
            o.AddDefaultPolicy(p => p
                .WithOrigins(builder.Configuration.GetRequiredSection("Cors:Origins").Get<string[]>()!)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()));
}

WebApplication app = builder.Build();

// First when present: everything below reads the client address, and until
// this runs it is the proxy's. Skipped when the gateway IS the edge (Compose),
// where RemoteIpAddress is already the client and trusting a forwarded header
// would let any caller choose its own rate-limit bucket.
if (behindProxy)
    app.UseForwardedHeaders();

app.UseExceptionHandler();
app.UseCorrelationId();           // assigns the ID if the client sent none
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
    <!-- The container and logging contracts Common.Application compiles
         against (§6.2, §13.3). ASP.NET Core's shared framework carries both,
         but Application takes no FrameworkReference — §4.2 puts the web on the
         other side of the boundary, and the building block that defines
         IPipelineBehavior sits on this one. -->
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" />
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
    <PackageVersion Include="AspNetCore.HealthChecks.SqlServer" Version="9.0.0" />
    <PackageVersion Include="AspNetCore.HealthChecks.Redis" Version="9.0.0" />
    <PackageVersion Include="AspNetCore.HealthChecks.Rabbitmq" Version="9.0.0" />
  </ItemGroup>
  <ItemGroup Label="Test">
    <!-- Test packages are pinned for exactly the same reason as runtime ones.
         xUnit v2 → v3 changed IAsyncLifetime from Task to ValueTask (§12.4):
         a major that drifts in silently breaks every fixture in the repo. -->
    <PackageVersion Include="xunit.v3" Version="3.1.0" />
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
    <PackageVersion Include="Respawn" Version="6.2.1" />
    <PackageVersion Include="WireMock.Net" Version="1.8.11" />
    <PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="9.9.0" />
    <!-- ServiceCollection itself. §6.2's registration test and §6.3's ordering
         test resolve from a real container, and the abstractions package the
         source references has none to build — it is contracts only. -->
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="10.0.0" />
    <!-- The architecture gates of §4.2, which PR-07 turns from a review
         comment into a build failure. A major bump can change which rules
         exist, so it fails loudly rather than quietly stopping to enforce. -->
    <PackageVersion Include="NetArchTest.Rules" Version="1.3.2" />
    <!-- The in-memory reader the observability tests read back through:
         §13.4's redaction tests, which that chapter prints one of, and the
         meter-coverage tests guarding §13.2's meter list, which live in
         Common.Web.Tests only. Test-only: nothing in src/ exports in
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

---

[← §3 Bounded contexts](03-bounded-contexts.md) · [Index](README.md) · [§5 Tactical DDD →](05-tactical-ddd.md)
