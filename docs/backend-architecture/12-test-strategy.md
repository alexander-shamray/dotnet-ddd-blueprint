# 12. Test strategy and TDD

## 12.1 The pyramid

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
staging** ([§13.7](13-observability.md)), asserting the SLOs — not a test suite, and [§15.1](15-cicd-deployment.md) stages it as
what it is. Naming it accurately is the point: a load run that is honestly a
load run gets maintained; an "E2E suite" that is actually three fragile scripts
gets disabled after the second flake and stays green forever.

Every row above names a project **and has an example in this section**. Both
halves are the rule: a level with no home is a level nobody writes, and a level
whose home is empty is one nobody notices is missing.

## 12.2 The TDD cycle applied

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
        Order order = OrderBuilder.Shipped();

        Action act = () => order.Cancel(CancellationReason.CustomerRequest, DateTimeOffset.UtcNow);

        act
            .ShouldThrow<DomainException>()
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
    Order order = OrderBuilder.AwaitingPayment();
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

## 12.3 Domain tests — no mocks

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
            "EUR",
            Now);

        order.Total.ShouldBe(Money.Of(25.50m, "EUR"));
        order.Status.ShouldBe(OrderStatus.AwaitingStock);
    }

    [Fact]
    public void Placing_an_order_raises_OrderPlacedDomainEvent()
    {
        Order order = OrderBuilder.Placed();

        OrderPlacedDomainEvent placed = order.DomainEvents
            .OfType<OrderPlacedDomainEvent>().ShouldHaveSingleItem();
        placed.OrderId.ShouldBe(order.Id);
        placed.Total.ShouldBe(order.Total);
    }

    [Fact]
    public void An_order_must_have_at_least_one_line()
    {
        Action act = () => Order.Place(CustomerId.New(), AddressBuilder.Valid(), [], "EUR", Now);

        act.ShouldThrow<DomainException>();
    }

    [Fact]
    public void Adding_the_same_product_twice_merges_the_lines()
    {
        var product = ProductId.New();

        var order = Order.Place(
            CustomerId.New(),
            AddressBuilder.Valid(),
            [
                (product, 2, Money.Of(10m, "EUR")),
                (product, 3, Money.Of(10m, "EUR"))
            ],
            "EUR",
            Now);

        OrderLine line = order.Lines.ShouldHaveSingleItem();
        line.Quantity.ShouldBe(5);
    }

    [Fact]
    public void All_lines_must_share_the_order_currency()
    {
        Action act = () => Order.Place(
            CustomerId.New(),
            AddressBuilder.Valid(),
            [
                (ProductId.New(), 1, Money.Of(10m, "USD"))
            ],
            "EUR",
            Now);

        act
            .ShouldThrow<DomainException>()
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
    public static Order Placed(int lines = 1, string currency = "EUR", CustomerId? customer = null) =>
        Order.Place(
            customer ?? CustomerId.New(),
            AddressBuilder.Valid(),
            Enumerable
                .Range(0, lines)
                .Select(_ => (ProductId.New(), 1, Money.Of(10m, currency))),
            currency, DefaultNow);

    public static Order AwaitingPayment()
    {
        Order order = Placed();
        order.ConfirmStock(DefaultNow);
        return order;
    }

    public static Order Shipped()
    {
        Order order = AwaitingPayment();
        order.ConfirmPayment(PaymentReference.From("test-ref"), DefaultNow);
        order.MarkShipped(TrackingNumber.From("TRK1"), DefaultNow);
        return order;
    }
}
```

## 12.4 Application tests — real infrastructure

> **Decision — integration tests use real SQL Server and Redis, not in-memory
> substitutes.** See [ADR-010](appendix-a-adrs.md#adr-010--testcontainers-not-in-memory-providers).

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
        await Task.WhenAll(_sql.StartAsync(), _cache.StartAsync(), _coordination.StartAsync(), _rabbit.StartAsync());

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
                    services
                        .AddAuthentication()
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });

                    // Remove ONLY the outbox dispatcher, not every hosted
                    // service: MassTransit registers its bus as one, so
                    // RemoveAll<IHostedService>() would stop the broker from
                    // starting and silently disable every consumption test.
                    //
                    // The dispatcher polls every 500 ms; left running it drains
                    // outbox rows underneath assertions about them. Tests that
                    // want it call fixture.ProcessOutboxBatchAsync() explicitly.
                    ServiceDescriptor hosted = services.Single(
                        d => d.ServiceType == typeof(IHostedService) &&
                            d.ImplementationType == typeof(OutboxDispatcher));
                    services.Remove(hosted);

                    // Still resolvable directly, so tests can drive one pass.
                    services.AddSingleton<OutboxDispatcher>();
                }));

        // Tests deliberately collapse the two database identities of §7.1 —
        // the container's sa login holds both DML and DDL. Production keeps
        // them separate, and migrations run as a job, never from a host (ADR-007).
        using IServiceScope scope = Factory.Services.CreateScope();
        await scope.ServiceProvider
            .GetRequiredService<OrderingDbContext>()
            .Database.MigrateAsync();

        // Reset between tests by truncating, which is far faster than
        // recreating the schema or wrapping every test in a rolled-back
        // transaction (which would hide transaction-related bugs).
        _respawner = await Respawner.CreateAsync(
            _sql.GetConnectionString(),
            new RespawnerOptions { SchemasToInclude = ["ordering"] });
    }

    public Task ResetAsync() => _respawner.ResetAsync(_sql.GetConnectionString());

    /// <summary>Runs exactly one claim-and-deliver pass. No timers, no waiting.</summary>
    public Task<int> ProcessOutboxBatchAsync(CancellationToken ct = default) =>
        Factory.Services.GetRequiredService<OutboxDispatcher>().ProcessBatchAsync(ct);

    public IDbConnection CreateConnection() => new SqlConnection(_sql.GetConnectionString());

    /// <summary>
    /// Seeds the price projection (§6.6). Required before any PlaceOrder test:
    /// the handler reads prices locally, so an unseeded projection makes every
    /// order fail ProductsUnavailable rather than erroring visibly.
    /// </summary>
    public async Task SeedPriceAsync(Guid productId, decimal amount, string currency = "EUR")
    {
        using IDbConnection connection = CreateConnection();
        await connection.ExecuteAsync(
            """
            MERGE ordering.ProductPrices AS t
            USING (SELECT ProductId = @productId, Currency = @currency) AS s
                ON t.ProductId = s.ProductId
                AND t.Currency = s.Currency
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
        using IServiceScope scope = Factory.Services.CreateScope();
        OrderingDbContext db =
            scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        Order order = OrderBuilder.Placed(customer: new CustomerId(customerId));
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order.Id.Value;
    }

    public async Task<IReadOnlyList<OutboxMessage>> OutboxAsync()
    {
        using IServiceScope scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<OrderingDbContext>()
            .OutboxMessages.AsNoTracking().ToListAsync();
    }

    public async Task StageOutboxAsync(params OutboxMessage[] rows)
    {
        using IServiceScope scope = Factory.Services.CreateScope();
        OrderingDbContext db =
            scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
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
        using IDbConnection connection = CreateConnection();
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
        using IDbConnection connection = CreateConnection();
        await connection.ExecuteAsync(
            "UPDATE ordering.OutboxMessages SET LockedUntil = NULL WHERE ProcessedAt IS NULL;");
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Task.WhenAll(
            _sql.DisposeAsync().AsTask(),
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
> The wider point belongs in [Appendix B](appendix-b-licences.md), not here. That register pins exact
> versions because four dependencies changed licence in two years — but a pin is
> a claim about an API as well as a licence. Pinning a major you have not
> compiled against buys the licence guarantee and none of the correctness.

The test scheme itself. Tests state who they are in headers, so authorization
runs against a real principal rather than being switched off:

```csharp
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme         = "Test";
    public const string UserHeader     = "X-Test-User";
    public const string PermissionsHeader = "X-Test-Permissions";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // No header means anonymous, not "authenticated as nobody" — otherwise
        // every 401 test silently passes.
        if (!Request.Headers.TryGetValue(UserHeader, out StringValues userId))
            return Task.FromResult(AuthenticateResult.NoResult());

        List<Claim> claims = [new(ClaimTypes.NameIdentifier, userId.ToString())];

        // The same claim type §11.4's policies require. A test that grants
        // itself "orders:cancel" is exercising the policy, not bypassing it.
        if (Request.Headers.TryGetValue(PermissionsHeader, out StringValues granted))
            claims.AddRange(
                granted
                    .ToString()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => new Claim("permission", p)));

        ClaimsPrincipal principal = new(new ClaimsIdentity(claims, Scheme));
        return Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme)));
    }
}
```

> **Do not give the test principal every permission.** A fixture that hands out
> a blanket claim set makes the [§11.4](11-identity-authorization.md) policies untestable and, worse, makes them
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
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        IDispatcher dispatcher =
            scope.ServiceProvider.GetRequiredService<IDispatcher>();
        OrderingDbContext db =
            scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        Result<Guid> result = await dispatcher.SendAsync(
            new PlaceOrderCommand(
                CommandId:       Guid.CreateVersion7(),
                CustomerId:      Guid.CreateVersion7(),
                Items:           [ new PlaceOrderItem(SeedData.ProductId, 2) ],
                ShippingAddress: AddressBuilder.ValidDto(),
                Currency:        "EUR"));

        result.IsSuccess.ShouldBeTrue();

        Order order =
            await db.Orders.SingleAsync(o => o.Id == new OrderId(result.Value));
        order.Status.ShouldBe(OrderStatus.AwaitingStock);
        order.Lines.ShouldHaveSingleItem().Quantity.ShouldBe(2);

        // The outbox rows are the real assertion: they prove the reactions are
        // staged atomically with the state change, and that nothing has run yet.
        // Only meaningful because the fixture removed the dispatcher — otherwise
        // this races a background service that drains these rows twice a second.
        List<OutboxMessage> outbox = await db.OutboxMessages.ToListAsync();
        outbox.ShouldAllBe(m => m.ProcessedAt == null);

        // Broker lane carries the CONTRACT type (§9.3 allow-list)...
        outbox.ShouldContain(m => m.Lane == OutboxLane.Broker &&
            m.MessageType.Contains(nameof(V1.OrderPlaced)));

        // ...and the Local lane carries the DOMAIN type (§7.5). Distinct names
        // are what make this an assertion about which type is on which lane,
        // rather than merely that both lanes got a row.
        outbox.ShouldContain(m => m.Lane == OutboxLane.Local &&
            m.MessageType.Contains(nameof(OrderPlacedDomainEvent)));

        // The domain type must never reach the broker — that is the leak §9.3
        // exists to prevent, and it is only checkable because the names differ.
        outbox.ShouldNotContain(m => m.Lane == OutboxLane.Broker &&
            m.MessageType.Contains(nameof(OrderPlacedDomainEvent)));
    }

    [Fact]
    public async Task The_same_command_id_is_processed_once()
    {
        var commandId = Guid.CreateVersion7();
        PlaceOrderCommand command =
            CommandBuilder.PlaceOrder() with { CommandId = commandId };

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        IDispatcher dispatcher =
            scope.ServiceProvider.GetRequiredService<IDispatcher>();

        Result<Guid> first  = await dispatcher.SendAsync(command);
        Result<Guid> second = await dispatcher.SendAsync(command);

        second.Value.ShouldBe(first.Value);

        OrderingDbContext db =
            scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
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

        IReadOnlyList<OutboxMessage> rows = await fixture.OutboxAsync();

        rows.Count(r => r.ProcessedAt is not null).ShouldBe(2);

        OutboxMessage poison = rows.Single(r => r.ProcessedAt is null);
        poison.Attempts.ShouldBe(1);
        poison.LastError.ShouldNotBeNullOrEmpty();
        poison.LockedUntil.ShouldNotBeNull();     // backed off, not abandoned
    }

    [Fact]
    public async Task A_row_stops_being_claimed_at_the_attempt_cap()
    {
        OutboxMessage poison = Poison.Row(fixture.MessageTypes);
        await fixture.StageOutboxAsync(poison);
        await fixture.SetOutboxAttemptsAsync(poison.MessageId, 9);

        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(0);   // 9 → 10

        // Clear the backoff lease, so the second pass is blocked by the
        // attempt cap and nothing else. Without this the test would pass
        // even if the cap were removed entirely.
        await fixture.ExpireOutboxLeasesAsync();

        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(0);

        OutboxMessage row = (await fixture.OutboxAsync()).Single();
        row.Attempts.ShouldBe(10);                // not 11 — never re-claimed
        row.ProcessedAt.ShouldBeNull();           // visible to the §13.6 alert
    }

    [Fact]
    public async Task A_local_row_with_no_registered_handler_fails_loudly()
    {
        await fixture.StageOutboxAsync(LocalRowFor<UnhandledEvent>(fixture.MessageTypes));

        await fixture.ProcessOutboxBatchAsync();

        OutboxMessage row = (await fixture.OutboxAsync()).Single();
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
    public static V1.OrderPlaced OrderPlaced(Guid orderId, decimal total = 25.00m, string currency = "EUR") => new()
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
([Appendix D](appendix-d-type-inventory.md)). `Poison` stages a message whose registered handler always throws;
`LocalRowFor<T>` stages a `Local` row for an event type with no handler at all:

```csharp
// The map is the real one, resolved from the fixture's provider (§9.4). A test
// double here would let a test stage a type the running host cannot resolve,
// which is the one thing these builders exist to prove does not happen.
internal static class Poison
{
    public static OutboxMessage Row(MessageTypeMap types) =>
        OutboxMessage.Stage(new AlwaysThrows(), OutboxLane.Local, Guid.CreateVersion7(), TestClock.Now, types);
}

internal static class Healthy
{
    public static OutboxMessage Row(MessageTypeMap types) =>
        OutboxMessage.Stage(new NoOpEvent(), OutboxLane.Local, Guid.CreateVersion7(), TestClock.Now, types);
}

internal static OutboxMessage LocalRowFor<TEvent>(MessageTypeMap types)
    where TEvent : new() =>
    OutboxMessage.Stage(new TEvent(), OutboxLane.Local, Guid.CreateVersion7(), TestClock.Now, types);
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
services.AddSingleton(
    new MessageTypeSource(typeof(V1.OrderPlaced).Assembly, typeof(Order).Assembly)
        .Add(typeof(AlwaysThrows).Assembly));
```

Two assertions belong beside these. The first is the cheapest guard on the
single-identity rule of [§9.1](09-messaging.md), and it takes no fixture at all — the thing that
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
    V1.OrderPlaced placed = Contracts.OrderPlaced(SeedData.OrderId);

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
    foreach (Type type in fixture.MessageTypes.StageableDomainEvents)
    {
        object sample = DomainEventSamples.Create(type);
        string json = JsonSerializer.Serialize(sample, type, OutboxJson.Options);

        JsonSerializer
            .Deserialize(json, type, OutboxJson.Options)
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
> `Ordering.TestSupport` ([§4.1](04-solution-structure.md)), a library rather than a test project.
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

### API contract tests

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
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/v1/orders/{Guid.CreateVersion7()}/cancel",
            new CancelOrderRequest(CancelReasons.CustomerRequest));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Rejects_a_caller_holding_the_wrong_permission()
    {
        // Authenticated, but with orders:read where the endpoint wants
        // orders:cancel — the case a fixture that grants everything hides.
        HttpResponseMessage response = await SendAsAsync(Guid.CreateVersion7(), "orders:read", Guid.CreateVersion7());

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Hides_another_customers_order_behind_a_404()
    {
        var owner = Guid.CreateVersion7();
        Guid orderId = await fixture.SeedOrderAsync(customerId: owner);

        HttpResponseMessage response = await SendAsAsync(
            Guid.CreateVersion7(),   // a different customer
            "orders:cancel",
            orderId);

        // 404, not 403 — §11.4. A 403 here would confirm the order exists,
        // which is the whole point of the check, and it is invisible to a
        // handler test that asserts on Result.Failure alone.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Rejects_a_reason_outside_the_wire_vocabulary()
    {
        HttpRequestMessage request = new(HttpMethod.Post, $"/v1/orders/{Guid.CreateVersion7()}/cancel")
        {
            // The enum's member name, not the wire code — accepted by
            // Enum.TryParse and rejected here, which is the difference.
            Content = JsonContent.Create(new CancelOrderRequest("CustomerRequest"))
        };
        request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        request.Headers.Add(TestAuthHandler.PermissionsHeader, "orders:cancel");

        HttpResponseMessage response = await _client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private Task<HttpResponseMessage> SendAsAsync(Guid userId, string permissions, Guid orderId)
    {
        HttpRequestMessage request = new(HttpMethod.Post, $"/v1/orders/{orderId}/cancel")
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

## 12.5 Testing the saga

Saga logic is where cross-service bugs live, and MassTransit's in-memory test
harness makes it testable without any infrastructure at all.

```csharp
[Fact]
public async Task Payment_declined_releases_stock_before_cancelling()
{
    await using ServiceProvider provider = new ServiceCollection()
        .AddMassTransitTestHarness(cfg => cfg
            .AddSagaStateMachine<OrderFulfilmentSaga, OrderFulfilmentState>()
            .InMemoryRepository())
        .BuildServiceProvider(true);

    ITestHarness harness = provider.GetRequiredService<ITestHarness>();
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
    ITestHarness harness = await StartHarnessAsync();
    var orderId = Guid.CreateVersion7();

    // Every member of V1.OrderPlaced is `required`, so there is no partial
    // construction to elide — a builder keeps that from filling the test.
    await harness.Bus.Publish(Contracts.OrderPlaced(orderId));

    (await harness.Sent.Any<ReserveStock>()).ShouldBeTrue();
    (await harness.Published.Any<ReserveStock>()).ShouldBeFalse();
}
```

## 12.6 Contract tests

The saga tests above prove one service's coordination. The only thing left that
is genuinely *between* services is the contract assembly, and its rules are all
stated elsewhere as things reviewers should notice: §9.6's "a contract may not
name a domain type", §9.2's versioned namespace, `required` members. Each is
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
        typeof(OrderPlaced).Assembly
            .GetTypes()
            .Where(t => t.IsPublic &&
                t is { IsInterface: false, IsAbstract: false } &&
                t.Namespace?.StartsWith("Common.Contracts.") == true)
            .ToArray();

    [Fact]
    public void No_contract_names_a_domain_type()
    {
        // §9.6's rule, and the one that silently drags Ordering.Domain into
        // every consuming service. Checked at the assembly level because a
        // contract cannot reference a domain type without the reference.
        typeof(OrderPlaced).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ShouldNotContain(name => name.EndsWith(".Domain"));
    }

    [Fact]
    public void Every_contract_lives_in_a_versioned_namespace()
    {
        // Common.Contracts.<Service>.V<n> — §9.2. A contract that lands one
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
        foreach (Type type in Contracts)
        {
            object instance = ContractSamples.Create(type);
            string json = JsonSerializer.Serialize(instance, type);

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

## 12.7 Test doubles

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

## 12.8 Conventions

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

## 12.9 What not to test

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

[← §11 Identity](11-identity-authorization.md) · [Index](README.md) · [§13 Observability →](13-observability.md)
