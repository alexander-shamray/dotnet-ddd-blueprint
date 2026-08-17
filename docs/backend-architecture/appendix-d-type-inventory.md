# Appendix D — Type inventory

Code samples in this document are excerpts, not compilable units. This appendix
is the index of the types they reference, so a reader can tell at a glance
whether a name is defined somewhere in the document, deliberately elided, or a
framework type.

It exists because of a specific failure mode: a sample that calls
`ProjectionInvoker.InvokeAsync(...)` reads as complete, and nothing catches that
no such method was ever defined. Grepping this table against the samples is a
repeatable check; noticing by eye is not.

## D.1 Application ports — defined here

| Type | Section | Purpose |
|---|---|---|
| `ICommand<T>`, `IQuery<T>` | [§6.2](06-cqrs.md) | Request marker interfaces |
| `ICommandHandler<,>`, `IQueryHandler<,>` | §6.2 | Request handlers |
| `IPipelineBehavior<,>`, `NextDelegate<T>` | §6.2 | Cross-cutting pipeline |
| `IDispatcher` | §6.2 | Entry point for commands and queries |
| `IUnitOfWork` | §6.3 | Transaction boundary; EF sealed in Infrastructure |
| `Error`, `ErrorType` | [§10.5](10-api-gateway.md) | `Code`, `Description`, `Type`. `Code` is a metric dimension ([§9.8](09-messaging.md)), so it is a closed set by construction |
| `OrderErrors` | §10.5 | Ordering's catalogue — the only place an `Error` is constructed |
| `IIdempotencyStore`, `IdempotencyEntry` | [§8.5](08-caching-redis.md) | Idempotency-key claim, Redis-backed |
| `IIdempotentCommand` | §8.5 | Opts a command into `IdempotencyBehavior`; carries `CommandId` |
| `IDomainEventCollector` | [§7.5](07-persistence.md) | Reads the change tracker without exposing it |
| `IDomainEventDispatcher` | §7.5 | Stages outbox rows; runs no handlers |
| `IProjectionRegistry` | §7.5 | Which event types have projection handlers |
| `IProductPriceReader` | §6.4 | Prices from the **local** projection — never a remote call |
| `ICurrentUser` | [§11.4](11-identity-authorization.md) | The caller, for resource-level checks, and the **only** source of a subject identifier on a principal-bearing command or query — every HTTP one (§11.4's subject rule). `Common.Application`, not per-service: nothing in its three members names one. `IsAuthenticated` is false on the consumer path, where there is no principal to bind from and §9.6's `AuthorisePayment` still carries a `CustomerId` field |
| `CommandOrigin` | §11.4 | `User` \| `System`, saying which path a command arrived on. `User` is the zero value, so an origin nobody set fails closed. Written as a literal at each entry point — never bound from a request or a message |
| `CancelOrderCommand`, `CancelOrderHandler` | §11.4 | The slice both entry paths converge on — HTTP and `CommandConsumer` (§9.4). The command carries a `CommandOrigin`; neither wire shape does. `CancelOrderRequest` is **not** here: it is the HTTP path's wire type and lives in `Ordering.Api.Endpoints` beside the endpoint binding it, the message path's being `CancelOrder` in `Common.Contracts` |
| `CancellationReasons` | §11.4 | Wire code → `CancellationReason`; the single parse both paths call |
| `OrderMetrics` | [§13.3](13-observability.md) | Instruments on `Ordering.Orders`. **Application**, not `Common.*`: it takes `Money`. Recorded only from §6.6's projection, on the committed path |
| `ICommandMessageMapper<,>` | §6.2 | Wire contract → application command |
| `IIntegrationEvent` | §9.1 | The three envelope fields every contract carries; the constraint that lets the consumer read `OccurredAt` |
| `ServiceIdentityOptions` | [§15.4](15-cicd-deployment.md) | Bound to `Identity:Client`, `ValidateOnStart`-checked. Registered by the BFF alone (§9.7) |
| `ServiceOptions` | §15.4 | Static constants only — not bound, not validated, not deployable |
| `PluggableInterfaces`, `AddPluggableFrom` | §6.2 | The one list of scanned interfaces, and the per-assembly scan that reads it |
| `AddDispatcher` | §6.2 | Registers `IDispatcher`. It exists because `Dispatcher` is internal to `Common.Application`, so no service can write the `AddScoped` line itself |
| `AddDomainEventDispatcher` | §7.5 | Its twin, for the same reason: `DomainEventDispatcher`, `ProjectionRegistry` and `ProjectionRegistryCache` are all internal. Registers the cache as a **singleton** and the other two scoped |
| `IIntegrationEventMapper` | §9.3 | Domain → integration allow-list |
| `IIntegrationEventPublisher` | §9.3 | Stages outbox rows on the current transaction |
| `IProjectionHandler<T>` | §9.4 | Reacts to own events, local lane, after commit |
| `IIntegrationEventHandler<T>` | §9.4 | Reacts to another service's events |
| `OutboxLane` | §9.3 | `Broker` \| `Local` |

## D.2 Domain model — defined in §5

The sample domain. Everything from `Order` down lives in `Ordering.Domain` and
references nothing outside it ([§4.2](04-solution-structure.md)). The first two
rows are the exception and the reason that rule reads "`Common.Domain` and
nothing else": they are shared *mechanism*, they live in `Common.Domain`
([§4.1](04-solution-structure.md)), and they are the one reference a Domain
project is permitted to carry.

| Type | Section | Kind |
|---|---|---|
| `Entity<TId>`, `AggregateRoot<TId>` | [§5.5](05-tactical-ddd.md) | Base types in `Common.Domain`; `Entity` carries identity equality, `AggregateRoot` adds `DomainEvents` and `Version` |
| `IDomainEvent`, `IHasDomainEvents`, `IAggregateRoot` | §5.5 | Domain event contracts and the two non-generic markers, also `Common.Domain` |
| `Order`, `OrderLine` | §5.4 | The aggregate and its child entity |
| `OrderId`, `CustomerId`, `ProductId` | §5.2 | Strongly typed identifiers. `CustomerId` has no `New()`: Ordering never mints a customer, and a factory would be a way to invent a subject (§11.4's rule) |
| `OrderLineId` | §5.2 | A line's own identity, distinct from `OrderId` — `Entity<TId>` compares the type as well as the identifier precisely because a shared key type would otherwise make a line equal to the order holding it (§5.5) |
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

## D.3 Outbox types — two, deliberately

The distinction that finding this appendix's first defect depended on:

| Type | Layer | Columns | Written by | Read by |
|---|---|---|---|---|
| `OutboxMessage` | EF entity, §9.4 | All eleven | `IIntegrationEventPublisher` staging; test fixtures | `db.OutboxMessages`, alerts, purge |
| `OutboxClaim` | Dapper record, §9.4 | The eight the `OUTPUT` clause returns | — | `OutboxDispatcher.ProcessBatchAsync` |
| `MessageTypeMap` | Singleton, §9.4 | Neither — it maps `MessageType` values to types | Built at startup from `MessageTypeSource` | `Stage` on the way in, `DeliverAsync` on the way out, [§12.4](12-test-strategy.md)'s round-trip |
| `MessageTypeSource` | Singleton, §9.4 | The assembly list behind the map, and the persisted-name overrides beside it — `Alias` resolves a name inward and `WriteAs` keeps writing one outward, which is what §9.4's three-release rename needs in both directions | §4.2's registration; §12.4's fixture `Add`s the test assembly | `MessageTypeMap`'s factory |
| `OutboxJson` | Singleton, §9.4 | Neither — the one `JsonSerializerOptions` both ends of the lane use, converters included | Built at startup from the registered `JsonConverter`s | `Stage` and `DeliverAsync`, and §12.4's round-trip test |
| `OutboxTable` | Singleton, §9.4 | Neither — the schema the dispatcher's three statements are composed against | §4.2's registration, one per service | `OutboxDispatcher`'s constructor |
| `InboxTable` | Singleton, §9.5 | Neither — the outbox's sibling, same schema and same database | §4.2's registration, from the **same** schema literal as the pair above | `RetentionPurgeService`'s constructor. Not the filter, which writes through EF and composes no SQL |
| `SqlSchema` | Internal static, §9.4 | Neither — the identifier check and the bracket-quoting the two tables above share | — | Both table types' constructors |
| `RetentionPolicy` | Singleton, §9.4 and §9.5 | Neither — the two windows, the batch size, the interval and the per-pass batch ceiling | §4.2's registration | `RetentionPurgeService`. Registered rather than `const` because §9.5 tells the reader to check the inbox window against their broker |
| `InboxMessage` | EF entity, §9.5 | `(MessageId, Endpoint)` composite key, `HandledAt` | `InboxFilter<T>` | duplicate suppression, retention purge |

`OutboxClaim` has no `ProcessedAt`, `LastError` or `LockedUntil` — a claimed row
is unprocessed by definition, and the dispatcher writes those columns rather
than reading them. Using one type for both produces properties that are
structurally always null on one path.

## D.4 Infrastructure implementations — defined here

| Type | Section | Implements |
|---|---|---|
| `Dispatcher` | §6.2 | `IDispatcher` |
| `EfUnitOfWork` | §6.3 | `IUnitOfWork` |
| `EfDomainEventCollector` | §7.5 | `IDomainEventCollector` |
| `DomainEventDispatcher`, `ProjectionRegistry` | §7.5 | Application ports |
| `ProjectionRegistryCache` | §7.5 | The registry's memo. A **singleton, not a `static` field**: keyed to the container, because a process holding two hosts would otherwise share whichever answer was computed first |
| `RedisIdempotencyStore` | §8.5 | `IIdempotencyStore` |
| `OrderingIntegrationEventMapper` | §9.3 | `IIntegrationEventMapper` |
| `OutboxDispatcher` | §9.4 | `BackgroundService`; `ProcessBatchAsync` is public for tests |
| `ProjectionInvoker` | §9.4 | Cached-delegate handler resolution |
| `MessageTypeMapValidator` | §9.4 | `IHostedService` that resolves `MessageTypeMap` once at startup. It exists because the map is registered through a factory and a factory is lazy — without it the duplicate-name throw lands on a background thread in a ready host, not at boot. Registered before `OutboxDispatcher`, since hosted services start in order |
| `MoneyJsonConverter` | §9.4 | `JsonConverter<Money>` for the Local lane. Per-service, beside the `ComplexProperty` mapping that persists the same value object as columns — a value object with a private constructor deserialises to its default in silence, and the domain may not carry a `[JsonConstructor]` (§4.2's gate) |
| `IntegrationEventConsumer<T>` | §9.4 | `IConsumer<T>` for **events** → `IIntegrationEventHandler`. `Common.Infrastructure.Messaging`, not per-service: nothing in it is, and the binding is what each service owns |
| `CommandConsumer<TMessage,TCommand>` | §9.4 | `IConsumer<T>` for **commands** → the application dispatcher. Same home and the same argument |
| `UnavailableResultException` | §9.8 | Thrown by `CommandConsumer` when a handler returns `ErrorType.Unavailable`, so the endpoint's retry policy sees a fault instead of an ack. `Common.Infrastructure.Messaging`, beside its one producer. The mirror image of `ContractMappingException`: that one exists to be *excluded* from retry, this one to reach it |
| `InboxFilter<T>` | §9.5 | `IFilter<ConsumeContext<T>>` over `DbContext` and `TimeProvider` — common, reaching the entity through `Set<InboxMessage>()`. Each service registers `AddScoped<DbContext>(sp => sp.GetRequiredService<XDbContext>())`, and the **delegate** is what keeps the inbox row on the handler's context. The row is added **after** `next.Send`, not before: a tracked entity does not survive the `ChangeTracker.Clear()` every command's unit of work opens with (§9.5's trap) |
| `RetentionPurgeService` | §9.4, §9.5 | `BackgroundService` running both purges on one slow schedule; `PurgeAsync` is public for tests. Logs and swallows a failed pass, because a throw out of `ExecuteAsync` stops the host |
| `ProjectedPriceReader` | §6.4 | `IProductPriceReader` over `ordering.ProductPrices` |
| `HttpContextCurrentUser` | §11.4 | `ICurrentUser` over `IHttpContextAccessor`; reads `ClaimTypes.NameIdentifier` and `PermissionClaim.Type`. `Common.Web`, not per-service — it cannot live in `Common.Infrastructure`, which takes no `FrameworkReference`, and `AddCommonWebDefaults` registers it beside `AddHttpContextAccessor()` |
| `PermissionClaim` | §11.4 | The claim type a permission travels in. Four things must agree on it and only three are code — the policies, `HttpContextCurrentUser` and `TestAuthHandler`; the realm's protocol mapper is configuration and is asserted against this constant instead (§11.5) |
| `AuthorizationPolicyExtensions` | §11.4 | `RequirePermission` over `RequireClaim(PermissionClaim.Type, …)`, so no host spells the claim type and an endpoint policy cannot drift from the resource check behind it |
| `AuthenticationExtensions` | [§11.3](11-identity-authorization.md) | `AddJwtAuthentication`, composed by `AddCommonWebDefaults`. Holds `Audience` — a constant, because §11.5 gives the platform one audience and §15.4 disqualifies a value that never varies from being configuration — and `AuthorityKey`, read eagerly with a throw that names it |
| `<Service>Permissions` | §11.4 | Each service's permission vocabulary as constants, at its composition root. The strings are the contract with the realm's claim mapper; the name is written twice — policy and endpoint — so the compiler should be what compares them. `CatalogPermissions.Write` is the first, and its only entry: the listing is anonymous (§10.2), so there is no `catalog:read` |
| `GatewayPermissions` | §11.4, [§10.2](10-api-gateway.md) | The same shape one host over, and the only permission a *route* names rather than an endpoint: `InventoryAdmin`. It lives in `Gateway.Api` because the route that requires it is the gateway's, not `Inventory.Api`'s, and the second site the string is written at is `appsettings.json` — which no compiler reads, so `RouteConfigurationTests` does |
| `RetryAfterHeader` | [§10.3](10-api-gateway.md) | `Seconds(TimeSpan)` — the rejection handler's `Retry-After`, rounded **up** to whole seconds and clamped at zero. A type for one expression because the rule is otherwise unreachable: the gateway's window is a minute long, so every rejection an HTTP test can produce carries tens of seconds and rounds identically whether the code truncates or ceilings. `Gateway.Api`, beside the pipeline that calls it |
| `NoTransformResponseCompressionProvider` | [ADR-020](appendix-a-adrs.md#adr-020--the-edge-compresses-over-tls-and-says-so) | `Gateway.Api`; a `ResponseCompressionProvider` subclass adding one case in front of `ShouldCompressResponse` — neither side of the exchange may carry `Cache-Control: no-transform`. RFC 9111 §5.2.2.6 requires that of an intermediary for the response form and ASP.NET Core does not implement it, so that half is conformance rather than policy; §5.2.1.6 makes the request form an ask rather than an obligation, and it is honoured anyway — which is why the provider also appends `Cache-Control` to `Vary` on every decision, the representation now depending on a request header that no cache would otherwise key on. Registered with `Replace`, because `AddResponseCompression` uses `TryAddSingleton` and ordering would otherwise decide it silently |
| `GatewayLimits` | [§10.1](10-api-gateway.md), [ADR-020](appendix-a-adrs.md#adr-020--the-edge-compresses-over-tls-and-says-so) | `MaxRequestBodyBytes` — one mebibyte, and the platform's only body ceiling. A constant rather than configuration because it does not vary between environments (§15.4), and a named one rather than a literal in `Program.cs` because the tests either side of the boundary spend it — which is what lets them assert the boundary's semantics rather than a value, so the number can move without touching them |
| `GatewayRateLimiterPolicies` | [§10.3](10-api-gateway.md) | `Anonymous`, `Authenticated` and the `All` list beside them. Not the authorization policy of the same name — §10.2 keeps two registries apart under one word. The list exists because the limiter's policy map is internal to the framework, so nothing can ask a built host what it registered: YARP refuses to load a route naming a policy that is absent, and `All` is what catches the other direction, a policy registered and named by nothing |
| `SensitiveDataRedactor` | §13.4 | `BaseProcessor<LogRecord>`; enforces the never-log list on the pipeline §13.2 builds |
| `OutboxMetrics` | §13.6 | Observable gauges on the `Ordering.Outbox` meter; singleton, eagerly constructed |
| `RequestMetrics` | §13.3 | `request.duration` on `Commerce.Requests`; injected by `LoggingBehavior` and still forced by `MetricsInitialiser` (§13.6) — a health probe never enters the pipeline |
| `MessagingMetrics` | §13.3 | Three instruments on `Commerce.Messaging`: `messaging.delivery.lag` and `projection.lag` histograms, and the `command.domain_rejected` counter `Rejected` writes (§9.8). Injected by `IntegrationEventConsumer<T>` and `CommandConsumer<,>`; resolved from the provider by the static `ProjectionInvoker`. **The class grew in instalments** — `Projected` landed with the outbox, the other two with the consumers that record them, on `PluggableInterfaces.All`'s terms — and is complete |
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
| `UseCorrelationId`, `MapCommonHealthEndpoints`, `AddObservability`, `AddCommonWebDefaults` | §10.4, §13.5, §13.2 | `Common.Web` host extensions |
| `AddCommonProblemDetails` | §10.5 | `Common.Web`; the RFC 9457 customisation `AddCommonWebDefaults` composes — and the registration of both exception handlers below, so no host takes one half without the other |
| `ValidationExceptionHandler` | §10.5 | `Common.Web`; the `IExceptionHandler` executing the table's 400 row — `ValidationBehavior`'s thrown `ValidationException` into field-keyed `errors`, registered by `AddCommonProblemDetails` |
| `ConcurrencyExceptionHandler` | §10.5 | `Common.Web`; the `IExceptionHandler` executing the table's 409 row — `DbUpdateConcurrencyException` into a problem response naming neither entity nor row version. Matches the derived type, not `DbUpdateException`, which also covers a constraint violation and is not a race |
| `ToHttpResult` | §10.5 | `Common.Web`; extension on `Result` and `Result<T>` — 204 or the value, and `ErrorType` to a status otherwise |
| `OrderFulfilmentSaga`, `Endpoints` | §9.6 | Saga and its command destinations |
| `ServiceFixture` | §12.4 | Testcontainers `IAsyncLifetime` fixture; owns the `WebApplicationFactory`. One per service, in that service's `*.TestSupport` (§4.1) — Catalog's is the first built, and §12.4's worked example is Ordering's |
| `TestAuthHandler` | §12.4 | `AuthenticationHandler<AuthenticationSchemeOptions>`; issues the principal a test names in headers, with `PermissionClaim.Type` on each granted permission so a test exercises the policy rather than bypassing it. Its constant is `SchemeName`, not `Scheme` — the base class already declares one, and CS0108 is an error under ADR-019. Per **suite owner**, not per service: each service keeps its copy in `*.TestSupport`, and the gateway's lives in `Gateway.Api.Tests` itself, which has one suite and therefore no TestSupport library ([§4.1](04-solution-structure.md)). The copies are deliberate — §4.3 permits one assembly to cross a boundary and it holds integration events — and what they share is `PermissionClaim.Type`, read from `Common.Web` |
| `StubDestination`, `StubbedGatewayFactory` | §12.4 | The gateway suite's pair: a real Kestrel server on an ephemeral loopback port answering 204 — or, asked through a query string, a compressible body, optionally under a declared `Content-Encoding` of `gzip` or `identity` (the double-compression guard, which refuses both alike) or under `Cache-Control: no-transform` (ADR-020's actual opt-out) — and recording the path it was given, with the factory pointing every cluster at it. A listener rather than an address that refuses — a refused connection measured about two seconds a request, so §10.3's hundred-request window replenished before a test could exhaust it — and the recorded path is the only observation of the prefix strip anywhere in the solution |
| `TestCurrentUser`, `Principals` | §12.4 | The scoped `ICurrentUser` double and its three values — `Default`, `Authenticated(subject, permissions)` and `Anonymous`. It **delegates** rather than replacing: unset inside a request it resolves through `HttpContext` exactly as production does, so `TestAuthHandler` keeps driving the endpoint tests; unset below HTTP it is `Default`; set, it is whatever the test said. A flat double would break the ownership-404 test by passing for the wrong reason |
| `ServiceFixture.DispatchAsync` | §12.4 | Opens a scope, points its `TestCurrentUser` at a named principal and dispatches; one overload each for `ICommand<T>` and `IQuery<T>`. The seam the subject tests use to reach a handler with no caller — which HTTP cannot produce against an endpoint group carrying `RequireAuthorization` |

## D.5 Referenced but deliberately not shown

These are named in samples and left undefined on purpose. They are ordinary,
and writing them out would add length without insight.

**The rule: any project-specific name a sample references — type *or* member —
that is not defined in D.1–D.4 must appear here.** If it is in neither, it is a
defect rather than an omission. Framework and BCL names (D.6) are out of scope;
so are members of types the document does define, since those are visible at
their declaration.

| Name | What it is |
|---|---|
| `IDbConnectionFactory`, `SqlConnectionFactory` | Creates a `SqlConnection` for Dapper reads — closed; Dapper opens it, the caller disposes it |
| `OrderingDbContext` | The service `DbContext`; configuration in §7.2 |
| `OutboxPublisher` | `IIntegrationEventPublisher` writing `OutboxMessage` rows; resolves `MessageTypeMap` and `OutboxJson` and hands both to `Stage`. Mints one correlation id per scope, lazily — a scope is one command, so rows staged together correlate, and an integration event overrides it from its envelope (§9.1) |
| `Result`, `Result<T>` | Non-generic `Result` is the void case — there is no `Unit` — and `Result<T>` derives from it, which is what lets `TransactionBehavior` test any command's outcome with one pattern (§6.3). `IsSuccess`/`IsFailure`, `Error`, and the `Success`/`Failure` factories |
| `CursorPage<T>`, `Cursor` | The pagination envelope and the opaque cursor codec (§6.5) |
| `AddRedisConnections`, `AddMassTransitMessaging` | Infrastructure registration helpers. The first is `Common.Infrastructure`'s one entry point, shown abbreviated in §8.2 — connections, cache stack, `RedisKeys`, the lock factory and the Redis tracing instrumentation in one call. The second is **per-service** (`{Service}.Infrastructure.Messaging`, the `Redis/DependencyInjection` shape one tree over): reads `ConnectionStrings:RabbitMq` eagerly and throws naming the key, disables MassTransit's usage telemetry, configures the RabbitMQ bus — and registers no health check, because `AddMassTransit` contributes `masstransit-bus` itself (§13.5). Per-service because it is where each service's consumers, sagas and receive endpoints are configured (§9.6, §9.8) |
| `RedisConnections` | Keyed-service names for the cache and coordination connections (§8.1), spelled like the configuration keys they resolve from |
| `RedisKeys` | §8.3's naming authority: `Lock`, `Idempotency` and `Denylist` return the full `ApplicationName`-prefixed key, and `CacheInstanceName` is the cache half's prefix. Deliberately no `Cache(string)` — the double-prefix hazard §8.3 names |
| `IDistributedLockFactory`, `IDistributedLock` | §8.1's lock and its held handle: `TryAcquireAsync(name, duration, ct)` returns null under contention, the TTL is mandatory, and disposal is the token-checked release |
| `BuildInfo` | Assembly version stamped onto OTel resource attributes (§13.2) |
| `OrderBuilder`, `AddressBuilder`, `CommandBuilder`, `SeedData` | Test data builders (§12.3) |
| `OutboxRows` | Outbox test builders (§12.4) — `Poison`, `Healthy`, `Unhandled` and `Verbose`, one class rather than one per case. Each takes the **fixture**, not the map alone: a staged row needs both halves of the host's agreement about the format, and one written without the service's converters round-trips to a defaulted value object. `Raised` is a fixed instant, because `OccurredAt` is what §13.7's lag is measured from |
| `Contracts`, `ContractSamples` | Test builders for `required`-member contract messages — one sample per contract type, and the reason the §12.6 suite cannot silently skip a new one. `Sampled` exposes the registry's keys, so the suite can also fail a sample whose contract has gone |
| `RecordedMeasurements`, `TestMeterFactory` | A `MeterListener` scoped to one meter, and the ten-line `IMeterFactory` behind a metrics class under test. What §9.8 and §13.3 constrain is the instrument's **name and tags**, which a substituted metrics type could not assert |
| `RouteConfiguration.ReadAll`, `RouteConfigurationTests.ReadRoutes` | One route of `ReverseProxy:Routes` and the two readers §12.4's samples call. **`ReadAll` is the type's static reader; `ReadRoutes` is the suite's private wrapper and not an alias for it** — it adds the non-empty assertion every other test in the class rests on, since each of them is a `foreach` and a `foreach` over nothing passes. Both are read through the host's own `IConfiguration` rather than off disk: that is the exact text YARP binds, where a second parser would assert against itself, and the file legally carries comments that only the configuration provider's `JsonCommentHandling.Skip` is guaranteed to agree with. `RouteConfiguration` carries the two derived values the assertions need — the forwarded path prefix, and the namespace the match sits under |
| `AlwaysThrows`, `NoOpEvent`, `UnhandledEvent` | Test event types with throwing / no-op / no handler |
| `IntegrationCollection` (xUnit) | The `[CollectionDefinition]` sharing one `ServiceFixture` across test classes — declared once **per test assembly** (§12.4). Named for what it groups, and deliberately not `ServiceCollection`: that name is taken by `Microsoft.Extensions.DependencyInjection`, and the local type would win in every test file that also builds a provider |
| `ICommandMessageMapper<TMessage,TCommand>` implementations | One per command contract — e.g. `CancelOrderMapper`, which parses `CancelOrder.Reason` back to `CancellationReason` and stamps `CommandOrigin.System` (§9.4). The stamp lives here rather than on the wire contract, so nothing a peer *sends* can claim it — what earns it is arrival on the queue, which is only as strong as broker authorisation (§9.4's callout) |
| `ConfirmOrderCommand`, `MarkOrderShippedCommand`, `FlagOrderForReviewCommand` | The other three message-borne slices — the same `CommandConsumer` and `ICommandMessageMapper` shape as `CancelOrderCommand` (§9.4, §9.6), and deliberately **not** its `CommandOrigin`. These arrive by message only, so there is no second path for an origin to tell apart; a dual-path command is what needs one (§11.4) |
| `OrderRepository` | `IOrderRepository` over EF; also the Infrastructure assembly marker for the §6.2 scan |
| `ContractMappingException` | Thrown by an `ICommandMessageMapper` on a value it cannot map; ignored by the retry policy so it reaches the error queue immediately (§9.4, §11.4) |
| `StartHarnessAsync` | Test helper returning a started MassTransit harness, and where a saga suite states both harness bounds — the inactivity timeout its negative assertions wait on, and the `TestTimeout` that would otherwise cap them (§12.5) |
| `Realm`, `Catalog`, `Audiences()` | Fixture handles on the Keycloak and Catalog containers, and a `aud`-claim reader over the decoded token — the realm-configuration assertions in §11.5 |
| `MessageTypes` | Fixture handle on the real `MessageTypeMap` (§12.4). **Not `Types`**: that name belongs to `NetArchTest.Rules.Types`, which §4.2's architecture tests call as `Types.InAssembly(...)`, and a fixture member would shadow it in any file holding both — the `ServiceCollection` collision again, two rows above |
| `Types`, `Json` | A `MessageTypeMap` over the contract and test assemblies and an `OutboxJson` with no converters, built once as private statics in the unit tests (§12.4). Not the fixture's — the `Stage` test takes no fixture, which is the point of it |
| `OutboxJson` (fixture member) | Fixture handle on the registered `OutboxJson` (§12.4), so a staged row and a delivered one agree about converters as well as settings |
| `DomainEventSamples` | One sample per stageable domain event, so a new event without one fails §12.4's round-trip rather than being skipped — `ContractSamples`' counterpart for the Local lane |
| `ConcurrentRequestException` | Thrown when an idempotency key is claimed but unfinished (§8.5) |
| `InProgressMarker` | The sentinel `TryClaimAsync` writes while a command is in flight; `GetAsync` reads it back as `InProgress` (§8.5) |
| `InvariantViolationException` | Thrown when a command modifies more than one aggregate root (§6.3, principle 3) |
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
| `PlaceOrderCommand`, `PlaceOrderItem`, `PlaceOrderValidator`, `PlaceOrderHandler` | The worked command slice (§6.4). Carries **no** `CustomerId` — the handler takes the subject from `ICurrentUser` (§11.4's subject rule) |
| `AddressDto`, its `ToDomain()` | The shipping address as `PlaceOrderCommand` carries it, and the map to the `Address` value object (§6.4). Built by `AddressBuilder.ValidDto()` in §12.4. An application DTO, unrelated to `ShippingAddressV1` below — a wire contract and a command payload version on different schedules (§4.3) |
| `GetOrderSummariesQuery`, `GetOrderSummariesHandler`, `OrderSummaryDto`, `SummaryProduct` | The worked query slice — shown at level 1 (§6.5) and rewritten in place at level 2 (§6.6). Carries `Cursor` and `Limit` only: the `CustomerId` in the `WHERE` clause is bound from `ICurrentUser` at both levels (§11.4's subject rule). The projection feeding it is `OrderSummaryProjection`, defined in D.4 |
| `GetProductDetailQuery`, `GetProductDetailHandler`, `ProductDetailDto`, `ProductSql` | Catalog's cached read slice — the `HybridCache` worked example (§8.2), and the one sample here belonging to a service other than Ordering |
| `ShippingAddressV1` | The address `V1.OrderConfirmed` carries (§9.1) — primitives only, and versioned with the contract that owns it, exactly as `PlacedLine` and `ConfirmedLine` are |
| `ProductPublished` | Catalog contract, envelope (§9.1) plus `ProductId`, `Name`, `ThumbnailUrl`, `Currency`, `Amount` — the last two so a projection has a price the moment the product exists, without waiting for a `PriceChanged` that may never come |
| `ProductDiscontinued` | Catalog contract, envelope plus `ProductId`. No reason code: §6.6 flips `IsAvailable` either way |
| `StockReserved`, `StockReleased` | Two of the saga's integration events ([§3.2](03-bounded-contexts.md), §9.6, `Common.Contracts`), each carrying the §9.1 envelope plus `OrderId` and nothing else — the step's whole outcome is that it happened. `PriceChanged` is not here at all; §9.1 declares it |
| `StockReservationFailed` | Envelope, `OrderId`, and `UnavailableProductIds` — the fact the step decided. It is on the contract rather than left to a support query because the saga finalises on it, and `SetCompletedWhenFinalized` has deleted the instance by the time anyone asks which lines failed. Ids, not a message: a sentence is something every consumer has to parse |
| `StockLevelChanged` | Inventory's fourth event and Catalog's only subscription (§3.2). Envelope, `ProductId`, `QuantityAvailable` — a **level**, not a delta, which is what makes a consumer idempotent by construction rather than by care (§6.6) |
| `PaymentAuthorised`, `PaymentRefunded` | Envelope, `OrderId`, `Reference`, `Amount` and `Currency`. The reference is an opaque provider token and stays a string everywhere; the currency travels with the amount for §9.6's reason |
| `PaymentDeclined` | Envelope, `OrderId`, `Reason` — the **provider's** reason, deliberately not a closed vocabulary. `CancelReasons` is this platform's and is enumerated; a PSP's set is not ours to pin, so this is carried for a human and never branched on or used as a metric dimension (§9.8) |
| `ShipmentDispatched`, `ShipmentDelivered` | Envelope, `OrderId`, `TrackingNumber`. Despatch is what the saga finalises on; delivery has only Notifications for a consumer, because it is not something a saga can coordinate or compensate |
| `OrderCancelled` | Envelope, `OrderId`, `CustomerId`, `Reason` — the same `CancelReasons` code the saga sent on `CancelOrder`, so a cancellation is reported with the code that caused it |

## D.6 Framework types

Not listed individually: ASP.NET Core, EF Core, MassTransit,
StackExchange.Redis, Dapper, Polly, OpenTelemetry, YARP, xUnit, Shouldly,
NSubstitute, Testcontainers, Respawn, Scrutor, FluentValidation, NetArchTest
and Aspire types are assumed — including the ones the samples name outright,
such as `TestResult` (§4.2), `IProxyStateLookup` ([§12.4](12-test-strategy.md))
and `IResourceBuilder<ProjectResource>` (§14.2). Licences in
[Appendix B](appendix-b-licences.md).

One of those names collides: xunit.v3 declares its own `Xunit.TestResult`, so
a test file holding §4.2's gates aliases the one it means —
`using TestResult = NetArchTest.Rules.TestResult;` — the `Types` shadowing
note in D.5, met from the other direction.

---

[← Appendix C](appendix-c-delivery-plan.md) · [Index](README.md)
