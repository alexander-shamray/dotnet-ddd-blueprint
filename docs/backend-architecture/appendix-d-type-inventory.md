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
| `ICurrentUser` | [§11.4](11-identity-authorization.md) | The caller, for resource-level checks, and the **only** source of a subject identifier on a command or query (§11.4's subject rule). `IsAuthenticated` is false on the consumer path |
| `CommandOrigin` | §11.4 | `User` \| `System`, saying which path a command arrived on. `User` is the zero value, so an origin nobody set fails closed. Written as a literal at each entry point — never bound from a request or a message |
| `CancelOrderCommand`, `CancelOrderRequest`, `CancelOrderHandler` | §11.4 | The slice both entry paths converge on — HTTP and `CommandConsumer` (§9.4). The command carries a `CommandOrigin`; the request does not |
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
| `IntegrationEventConsumer<T>` | §9.4 | `IConsumer<T>` for **events** → `IIntegrationEventHandler` |
| `CommandConsumer<TMessage,TCommand>` | §9.4 | `IConsumer<T>` for **commands** → the application dispatcher |
| `InboxFilter<T>` | §9.5 | `IFilter<ConsumeContext<T>>` |
| `ProjectedPriceReader` | §6.4 | `IProductPriceReader` over `ordering.ProductPrices` |
| `HttpContextCurrentUser` | §11.4 | `ICurrentUser` over `IHttpContextAccessor`; reads `ClaimTypes.NameIdentifier` and `permission` claims |
| `SensitiveDataRedactor` | §13.4 | `BaseProcessor<LogRecord>`; enforces the never-log list on the pipeline §13.2 builds |
| `OutboxMetrics` | §13.6 | Observable gauges on the `Ordering.Outbox` meter; singleton, eagerly constructed |
| `RequestMetrics` | §13.3 | `request.duration` on `Commerce.Requests`; injected by `LoggingBehavior` and still forced by `MetricsInitialiser` (§13.6) — a health probe never enters the pipeline |
| `MessagingMetrics` | §13.3 | Three instruments on `Commerce.Messaging`: `messaging.delivery.lag` and `projection.lag` histograms, and the `command.domain_rejected` counter `Rejected` writes (§9.8). Injected by `IntegrationEventConsumer<T>` and `CommandConsumer<,>`; resolved from the provider by the static `ProjectionInvoker`. **The class grows in instalments** — `Projected` lands with the outbox, the other two with the consumers that record them, on `PluggableInterfaces.All`'s terms |
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
| `AddCommonProblemDetails` | §10.5 | `Common.Web`; the RFC 9457 customisation `AddCommonWebDefaults` composes — and the registration of `ValidationExceptionHandler`, so no host takes one half without the other |
| `ValidationExceptionHandler` | §10.5 | `Common.Web`; the `IExceptionHandler` executing the table's 400 row — `ValidationBehavior`'s thrown `ValidationException` into field-keyed `errors`, registered by `AddCommonProblemDetails` |
| `ToHttpResult` | §10.5 | `Common.Web`; extension on `Result` and `Result<T>` — 204 or the value, and `ErrorType` to a status otherwise |
| `OrderFulfilmentSaga`, `Endpoints` | §9.6 | Saga and its command destinations |
| `ServiceFixture` | §12.4 | Testcontainers `IAsyncLifetime` fixture; owns the `WebApplicationFactory`. One per service, in that service's `*.TestSupport` (§4.1) — Catalog's is the first built, and §12.4's worked example is Ordering's |
| `TestAuthHandler` | §12.4 | `AuthenticationHandler<AuthenticationSchemeOptions>`; issues the principal a test names in headers. Also `Ordering.TestSupport` |
| `TestCurrentUser`, `Authenticated`, `Anonymous` | §12.4 | The scoped `ICurrentUser` double and its two factories — one reporting a given subject, one with `IsAuthenticated` false and a throwing `Id`, which is the consumer path's shape. Registered for every test as an authenticated default, so a test silent about the caller still has one. `TestAuthHandler` is the boundary equivalent; these are for the states `RequireAuthorization` stops a request from reaching |
| `ServiceFixture.DispatchAsync` | §12.4 | Opens a scope, points its `TestCurrentUser` at a named principal and dispatches. The seam the subject tests use to reach a handler with no caller — which HTTP cannot produce against an endpoint group carrying `RequireAuthorization` |

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
| `Contracts`, `ContractSamples` | Test builders for `required`-member contract messages — one sample per contract type, and the reason the §12.6 suite cannot silently skip a new one |
| `AlwaysThrows`, `NoOpEvent`, `UnhandledEvent` | Test event types with throwing / no-op / no handler |
| `IntegrationCollection` (xUnit) | The `[CollectionDefinition]` sharing one `ServiceFixture` across test classes — declared once **per test assembly** (§12.4). Named for what it groups, and deliberately not `ServiceCollection`: that name is taken by `Microsoft.Extensions.DependencyInjection`, and the local type would win in every test file that also builds a provider |
| `ICommandMessageMapper<TMessage,TCommand>` implementations | One per command contract — e.g. `CancelOrderMapper`, which parses `CancelOrder.Reason` back to `CancellationReason` and stamps `CommandOrigin.System` (§9.4). The stamp lives here rather than on the wire contract, so a peer service cannot claim it |
| `ConfirmOrderCommand`, `MarkOrderShippedCommand`, `FlagOrderForReviewCommand` | The other three message-borne slices, same shape as `CancelOrderCommand` (§9.4, §9.6) |
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
| `StockReserved`, `StockReservationFailed`, `StockReleased`, `PaymentAuthorised`, `PaymentDeclined`, `ShipmentDispatched` | The saga's integration events ([§3.2](03-bounded-contexts.md), §9.6, `Common.Contracts`), each carrying the §9.1 envelope plus `OrderId` and what its own step decided. `PriceChanged` is not here — §9.1 declares it |

## D.6 Framework types

Not listed individually: ASP.NET Core, EF Core, MassTransit, StackExchange.Redis,
Dapper, Polly, OpenTelemetry, xUnit, Shouldly, NSubstitute, Testcontainers,
Respawn, Scrutor, FluentValidation, NetArchTest and Aspire types are assumed —
including the ones the samples name outright, such as `TestResult` (§4.2) and
`IResourceBuilder<ProjectResource>` (§14.2). Licences in [Appendix B](appendix-b-licences.md).

One of those names collides: xunit.v3 declares its own `Xunit.TestResult`, so
a test file holding §4.2's gates aliases the one it means —
`using TestResult = NetArchTest.Rules.TestResult;` — the `Types` shadowing
note in D.5, met from the other direction.

---

[← Appendix C](appendix-c-delivery-plan.md) · [Index](README.md)
