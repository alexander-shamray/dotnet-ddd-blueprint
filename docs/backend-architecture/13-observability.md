# 13. Observability

## 13.1 The three signals

| Signal | Question it answers | Tool |
|---|---|---|
| Metrics | Is something wrong right now? | Prometheus |
| Traces | Where in the system is it wrong? | Tempo / Jaeger |
| Logs | What exactly happened? | Loki / Seq |

They are used in that order during an incident. An alert fires on a metric, a
trace localises it to a service and a span, and logs filtered by that trace ID
explain it. Correlation between the three is what makes this work, which is why
section 10.4 exists.

## 13.2 OpenTelemetry

Configure once in `Common.Web` ([§4.1](04-solution-structure.md)), referenced by every service host.
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
    builder.Services
        .AddAuthorizationBuilder()
        .AddPolicy("authenticated", p => p.RequireAuthenticatedUser());

    builder.Services.AddCommonProblemDetails();                    // §10.5

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
    string serviceName = builder.Environment.ApplicationName;

    // OpenTelemetry becomes the ONLY logging provider, and that is a security
    // requirement rather than tidiness — see §13.4.
    builder.Logging.ClearProviders();

    builder.Logging.AddOpenTelemetry(logging =>
    {
        logging.IncludeFormattedMessage = true;
        logging.IncludeScopes = true;

        // §13.4's "never log a secret" rule, given a mechanism. Registered
        // here because this is the only logging pipeline the host has — a
        // redaction policy configured on a library nobody installed redacts
        // nothing, and reads in review as though it does.
        logging.AddProcessor(new SensitiveDataRedactor());
    });

    // A named local rather than an inline construction. The type has to be
    // spelled out where it is built, and spelling it inside AddAttributes' own
    // argument runs that line to 130 columns — past the 120 budget. Named here,
    // the declaration carries the type and `new` needs none.
    KeyValuePair<string, object> environment =
        new("deployment.environment", builder.Environment.EnvironmentName);

    builder.Services
        .AddOpenTelemetry()
        .ConfigureResource(r => r
            .AddService(serviceName, serviceVersion: BuildInfo.Version)
            .AddAttributes([environment]))
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
            .AddEntityFrameworkCoreInstrumentation()
            .AddRedisInstrumentation()
            .AddSource("MassTransit"))
        .UseOtlpExporter();

    return builder;
}
```

Two of the lines above still arrive later than the rest, and each is named here
so that a reader comparing this block against `Common.Web` does not read a gap
as a mistake. The rule is that an instrumentation lands with the package it
instruments — unlike a meter name, which is a string, each costs a package
reference, and a reference to a library nothing uses is a claim about the
dependency graph that is not yet true. `AddEntityFrameworkCoreInstrumentation`
therefore landed at **PR-08**, the PR that gave a service a `DbContext`;
`AddRedisInstrumentation` waits for **PR-12**. The authentication block in
`AddCommonWebDefaults` lands at **PR-16**, with the scheme that makes its
policy mean anything.

> **The EF Core call takes no options, and the one it used to take is a trap.**
> This block configured `SetDbStatementForText = true` until PR-08 compiled it:
> the property does not exist on the instrumentation package, which now emits
> the command text through the semantic-convention attributes by default. The
> option that *does* survive is `SetDbQueryParameters`, and it must stay off —
> its own documentation warns that it captures raw parameter **values**, which
> is every password, token and card number the application has ever bound,
> written onto a span that [§13.4](13-observability.md)'s redactor never
> inspects.

Filtering health checks out of traces is not cosmetic — at a ten-second probe
interval across a dozen pods they would otherwise dominate both trace volume and
storage cost.

`SetDbStatementForText` records SQL text on spans, which is invaluable for
debugging and a data-exposure risk if queries embed sensitive literals.
Parameterised queries — which everything here uses — record the parameterised
form, so this is safe as configured. Revisit it if anyone introduces string
concatenation into SQL.

## 13.3 Domain metrics

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
    private readonly Counter<long> _placed;
    private readonly Counter<long> _cancelled;
    private readonly Histogram<double> _value;
    private readonly Histogram<double> _fulfilmentSeconds;

    public OrderMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create("Ordering.Orders");

        _placed = meter.CreateCounter<long>(
            "orders.placed",
            unit: "{order}",
            description: "Orders successfully placed.");
        _cancelled = meter.CreateCounter<long>("orders.cancelled", unit: "{order}");
        _value = meter.CreateHistogram<double>("orders.value", unit: "EUR");
        _fulfilmentSeconds = meter.CreateHistogram<double>(
            "orders.fulfilment.duration",
            unit: "s",
            description: "Placed to confirmed.");
    }

    public void Placed(Money total)
    {
        _placed.Add(1, new KeyValuePair<string, object?>("currency", total.Currency));
        _value.Record((double)total.Amount, new KeyValuePair<string, object?>("currency", total.Currency));
    }

    public void Cancelled(string reason) =>
        _cancelled.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void Fulfilled(TimeSpan placedToConfirmed) =>
        _fulfilmentSeconds.Record(placedToConfirmed.TotalSeconds);
}
```

An instrument with no call site is a metric that reads zero forever, which is
indistinguishable from a system doing no work. All three call sites are in
`OrderSummaryProjection` ([§6.6](06-cqrs.md)), and that is a rule rather than a coincidence:

> **A business metric is recorded on the committed path, never inside the
> write transaction.** A handler runs inside one, so a counter it increments
> counts orders that roll back — and counts them once per attempt when EF's
> retrying execution strategy replays the delegate (§6.3). The projection runs
> after the commit, driven by the `Local` outbox lane, which is the earliest
> point at which "an order was placed" is true.
>
> **And exactly once, which rows-affected does not give you.** The `Local` lane
> is at-least-once *and* unordered ([§9.4](09-messaging.md)), so "did this write change anything"
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
FulfilmentFact? fulfilment =
    await connection.QuerySingleOrDefaultAsync<FulfilmentFact>(
        """
        UPDATE ordering.OrderSummaries
        SET FulfilmentCounted = 1
        OUTPUT inserted.PlacedAt, inserted.ConfirmedAt
        WHERE OrderId = @OrderId
            AND PlacedAt IS NOT NULL
            AND ConfirmedAt IS NOT NULL
            AND FulfilmentCounted = 0;
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

### The two types this section defines, and where the rest come from

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
        Meter meter = factory.Create("Commerce.Requests");
        _duration = meter.CreateHistogram<double>(
            "request.duration",
            unit: "s",
            description: "Dispatcher entry to result.");
    }

    public void Recorded(string request, string outcome, TimeSpan elapsed) =>
        _duration.Record(
            elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("request", request),
            new KeyValuePair<string, object?>("outcome", outcome));
}
```

```csharp
// Common.Infrastructure — registered by AddOrderingInfrastructure, because
// all three call sites are Infrastructure types (§9.4).
public sealed class MessagingMetrics
{
    private readonly Histogram<double> _deliveryLag;
    private readonly Histogram<double> _projectionLag;
    private readonly Counter<long> _rejected;

    public MessagingMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create("Commerce.Messaging");

        _deliveryLag = meter.CreateHistogram<double>(
            "messaging.delivery.lag",
            unit: "s",
            description: "OccurredAt to consumer start.");
        _projectionLag = meter.CreateHistogram<double>(
            "projection.lag",
            unit: "s",
            description: "Event raised to projection applied.");
        _rejected = meter.CreateCounter<long>(
            "command.domain_rejected",
            description: "Message-borne commands the domain refused (§9.8).");
    }

    public void Delivered(string message, TimeSpan lag) =>
        _deliveryLag.Record(lag.TotalSeconds, new KeyValuePair<string, object?>("message", message));

    public void Projected(string message, TimeSpan lag) =>
        _projectionLag.Record(lag.TotalSeconds, new KeyValuePair<string, object?>("message", message));

    public void Rejected(string message, string error) =>
        _rejected.Add(
            1,
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
`IDomainEvent` does carry `OccurredAt` ([§5.5](05-tactical-ddd.md)), so a constraint would compile
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

> **These get a `MetricsInitialiser` entry too (§13.6), and the tempting reason
> not to is the instrument kind.** An observable gauge is pull-based — the
> collector asks, and if nothing ever constructed the class there is nothing to
> ask — which makes forcing the outbox gauges obviously necessary. A histogram
> is pushed from a live call site, so anything recording it has already resolved
> the class, and it looks safe to leave out. It is not: the call site has to be
> *reached*, and a consumer is constructed when a message arrives, so on a quiet
> service these instruments still do not exist. §13.6 states the test that
> actually decides membership — can this service run for an hour without
> constructing it — and all four types fail it.

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
    // Compiled once per closed behaviour rather than parsed per request. CA1848
    // is met rather than waived here: this behaviour is outermost on every
    // dispatched request, which is exactly the hot path the rule is about.
    private static readonly Action<ILogger, string, double, Exception?> Completed =
        LoggerMessage.Define<string, double>(
            LogLevel.Information,
            new EventId(1, nameof(Completed)),
            "{RequestType} completed in {ElapsedMs} ms");

    private static readonly Action<ILogger, string, Exception?> Threw =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(2, nameof(Threw)),
            "{RequestType} threw");

    public async Task<TResult> HandleAsync(TRequest request, NextDelegate<TResult> next, CancellationToken ct)
    {
        string name = typeof(TRequest).Name;
        long start = clock.GetTimestamp();

        // A scope, not a log property: everything written inside the handler
        // inherits it, including EF Core's and MassTransit's own logging.
        using IDisposable? scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["RequestType"] = name
        });

        try
        {
            TResult result = await next();

            // Read once and used twice. Two calls to GetElapsedTime would put
            // a different number in the log line and the histogram, and the
            // one a reader trusts is whichever they looked at first.
            TimeSpan elapsed = clock.GetElapsedTime(start);

            Completed(logger, name, elapsed.TotalMilliseconds, null);
            metrics.Recorded(name, "ok", elapsed);

            return result;
        }
        catch (Exception ex)
        {
            Threw(logger, name, ex);
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

## 13.4 Structured logging

```csharp
// Good — structured, queryable, no PII.
logger.LogInformation(
    "Order {OrderId} placed by customer {CustomerId} for {Amount} {Currency}",
    order.Id,
    order.CustomerId,
    order.Total.Amount,
    order.Total.Currency);

// Bad — string interpolation destroys the structure; the fields cannot be
// queried and every message is a distinct string.
logger.LogInformation($"Order {order.Id} placed for {order.Total}");
```

Both halves call `LogInformation` directly, and that is deliberate — the pair
is about message templates, and a `LoggerMessage.Define` field either side of
it would bury the one difference the reader is meant to see. Every logging call
site the solution actually builds takes the compiled form instead, because
CA1848 is enforced (ADR-019) and the classes that log are the ones that run per
request or per message: §13.3's `LoggingBehavior`, and `OutboxDispatcher` and
`CommandConsumer` in [§9.4](09-messaging.md). Fragments here teach the
template; those three show the shape.

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
    // The key ILogger puts the message template under. Its presence is what
    // makes Body a template rather than a rendered line — see OnEnd.
    private const string OriginalFormat = "{OriginalFormat}";

    // Substring match, not equality: the field that leaks is never named
    // exactly "password" — it is "NewPassword", "card_number", "id_token".
    private static readonly string[] Sensitive =
        ["password", "secret", "token", "authorization", "cardnumber", "card_number", "ssn", "nationalid"];

    public override void OnEnd(LogRecord record)
    {
        if (record.Attributes is null)
            return;

        List<KeyValuePair<string, object?>>? scrubbed = null;
        List<string>? secrets = null;
        bool hasTemplate = false;

        for (int i = 0; i < record.Attributes.Count; i++)
        {
            KeyValuePair<string, object?> attribute = record.Attributes[i];

            if (attribute.Key == OriginalFormat)
                hasTemplate = true;

            if (!IsSensitive(attribute.Key))
                continue;

            // Copy only when something actually matches — the common case is
            // no match, and this runs on every log record on every request.
            scrubbed ??= [.. record.Attributes];

            // Keep what was removed. The exception check below needs the
            // values, not the keys — see the rule it enforces.
            if (attribute.Value?.ToString() is { Length: > 0 } secret)
                (secrets ??= []).Add(secret);

            scrubbed[i] = new KeyValuePair<string, object?>(attribute.Key, "[redacted]");
        }

        if (scrubbed is null)
            return;

        record.Attributes = scrubbed;

        // Attributes alone are not enough. IncludeFormattedMessage is set
        // above, and with it the exporter sends FormattedMessage as the
        // record's body — the template with every argument substituted.
        // Redacting Password while "Login for ada with hunter2" ships beside
        // it protects nothing and reads in review as though it does.
        //
        // Body is only that template when the state carried {OriginalFormat}.
        // Without it OpenTelemetry fills Body with the formatter's own output
        // — the rendered line, secret and all — so falling back to Body there
        // would re-export what the scrub just removed.
        record.FormattedMessage = hasTemplate && record.Body is not null
            ? record.Body
            : "[redacted]";

        // The rule this enforces: never export a value the processor has just
        // decided is sensitive. OTLP serialises Exception separately from both
        // Attributes and FormattedMessage — as exception.message and
        // exception.stacktrace — so scrubbing those two and leaving the
        // exception alone ships the secret through a third channel.
        //
        // ToString() rather than Message, because it covers inner exceptions
        // and the stack trace too. Dropped rather than rewritten: Exception
        // .Message is read-only, and reconstructing the type is not something
        // to attempt on a logging path. The record keeps its level, template
        // and every non-sensitive attribute, so the error is still visible —
        // what is lost is the trace, on the records that demonstrably carry a
        // live secret in it.
        //
        // Deliberately narrower than "drop the exception whenever anything was
        // redacted": that would destroy stack traces on every record that
        // merely has a Password attribute beside an unrelated failure, which
        // is most of them.
        if (record.Exception is not null && secrets is not null && Reveals(record.Exception, secrets))
            record.Exception = null;
    }

    private static bool Reveals(Exception exception, List<string> secrets)
    {
        string text = exception.ToString();

        foreach (string secret in secrets)
        {
            if (text.Contains(secret, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    // A foreach rather than Sensitive.Any(s => key.Contains(s, ...)): the
    // lambda would capture `key`, so the closure allocates once per attribute
    // inspected — including on the no-match path the copy above is written to
    // keep allocation-free. This runs on every attribute of every log record.
    private static bool IsSensitive(string key)
    {
        foreach (string term in Sensitive)
        {
            if (key.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
```

> **Scrubbing the attributes alone would protect nothing.**
> `IncludeFormattedMessage` is on (§13.2), and with it the exported body is the
> *rendered* string — so `Login for ada with hunter2` would sit in the one
> field a log backend indexes and searches, next to a `Password` attribute
> reading `[redacted]`. The fallback is `Body`, the un-substituted template:
> still readable, and the values that were not sensitive are still on the
> record as attributes.

A record with nothing sensitive on it keeps its formatted message untouched,
and that is asserted as its own test rather than left implied. Without it a
processor that rewrote unconditionally would pass every redaction test in the
suite while quietly emptying every log line on the platform.

Four limits worth stating rather than discovering.

Redaction is **by key**, so `logger.LogInformation("Token is {Value}", token)`
is caught only if the placeholder is named sensitively — the argument for
naming it `{Token}` and never interpolating. Interpolation is now doubly
unsafe: `$"Token is {token}"` produces no attribute to match *and* puts the
secret in the template, so the fallback carries it too.

It cannot help with a **whole object logged as one attribute**; that is what
the "never log full request bodies" half of the rule is for.

And it does not read **scopes**. `IncludeScopes` is on, but the processor
inspects `Attributes` only, so a sensitive key in a `BeginScope` dictionary is
exported unredacted. Nothing leaks today, because the platform opens exactly
two scopes and neither can carry one: `LoggingBehavior`'s `RequestType`
(§13.3), which is a type name, and `UseCorrelationId`'s `CorrelationId`
([§10.4](10-api-gateway.md)), which is a trace ID or a GUID. A third one
carrying a secret would leak it silently, and no test here would notice.
Widening the processor to walk `ScopeProvider` is a design change with its own
cost, not a fix to fold into this one.

And an **exception can still carry a secret the attributes never named**.
Where a redacted value reappears in the exception text the exception is
dropped, under the rule above — never export a value the processor has just
decided is sensitive. Where the secret was only ever in the exception, there
is nothing to match it against and it survives. That is the interpolation case again: text an author
wrote by hand, which no key-based mechanism can inspect. `throw new
InvalidOperationException($"bad token {token}")` is the same mistake as
`$"Token is {token}"` and is caught by neither.

**The processor governs one pipeline, which is why §13.2 leaves only one.** A
`BaseProcessor<LogRecord>` sees records inside OpenTelemetry and nowhere else.
Any other `ILoggerProvider` on the host formats the original state itself and
never passes through this code — so `AddObservability` calls
`ClearProviders()` before adding OpenTelemetry, making it the sole provider.

That is not tidiness. `WebApplication.CreateBuilder` installs Console, Debug and
EventSource before a host reaches `AddCommonWebDefaults` (§4.2), and container
stdout is collected in most clusters, so a `{Password}` scrubbed on the OTLP
path shipped in clear text on the console one. The redaction looked complete and
covered a single destination — the same shape as the `FormattedMessage` gap
above, one layer further out.

Two things follow, both worth stating. The guarantee covers providers registered
**before** `AddObservability`, which is every default and the only case §4.2
produces; a service that adds a provider afterwards has opted out and owns the
consequence. And the visible cost is local: `dotnet run` no longer prints to the
terminal, because nothing is left that writes there. §13.1 routes logs to Loki
or Seq through OTLP regardless, and [§14.1](14-local-development.md) runs a
collector, so the loss is the raw terminal stream rather than the logs
themselves — add a console exporter to the OpenTelemetry pipeline if a
developer wants it back.

Assert it, because a redactor that silently stops matching is worse than none.
The test lives in `Common.Web.Tests` — a `Common.Web` behaviour tested once
rather than once per host, in the suite that already owns this project's
behaviour ([§12.1](12-test-strategy.md)). Every host calls `AddObservability`, so a copy in a
service's own suite would re-assert the same processor over the same pipeline
and only add a place to forget — and a building block asserted in Ordering's
suite is one that moves house if Ordering ever does.

Assert it through `ILogger`, not through OpenTelemetry's logger provider
directly. The Logs Bridge API (`Sdk.CreateLoggerProviderBuilder`) is shipped
behind an experimental diagnostic and is not how any host here produces a log
record; a test that used it would be green while the path in production drifted
away underneath it:

```csharp
// CA1848 is enforced repo-wide (ADR-019) and does not exempt test projects, so
// the template goes through LoggerMessage.Define exactly as production logging
// does. The point survives intact: the attribute keys still come from a message
// template, read through ILogger.
private static readonly Action<ILogger, string, string, Exception?> Login =
    LoggerMessage.Define<string, string>(
        LogLevel.Information,
        new EventId(1, nameof(Login)),
        "Login for {User} with {Password}");

private static LogRecord EmitRecord(Action<ILogger> write)
{
    List<LogRecord> exported = [];

    // Built exactly as AddObservability builds it (§13.2) — ILoggingBuilder,
    // the same extension, and IncludeFormattedMessage set the same way, so the
    // test covers the seam the host uses. A block rather than a using
    // declaration: the factory has to be disposed before the exported list is
    // read, and a declaration would defer that to the end of the method.
    using (ILoggerFactory factory = LoggerFactory.Create(b =>
        b.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.AddProcessor(new SensitiveDataRedactor());
            o.AddInMemoryExporter(exported);
        })))
    {
        write(factory.CreateLogger("test"));
    }

    return exported.Single();
}

[Fact]
public void Sensitive_attributes_are_redacted()
{
    IReadOnlyList<KeyValuePair<string, object?>> attributes =
        EmitRecord(logger => Login(logger, "ada", "hunter2", null)).Attributes!;

    attributes.Single(a => a.Key == "Password").Value.ShouldBe("[redacted]");

    // The other half of the assertion, and the one that catches a deny-list
    // grown careless: everything not on it survives intact.
    attributes.Single(a => a.Key == "User").Value.ShouldBe("ada");
}

[Fact]
public void A_redacted_record_does_not_export_the_rendered_secret()
{
    // The assertion above is cosmetic without this one: the exported body is
    // the rendered string, and it is what a log backend indexes.
    LogRecord record = EmitRecord(logger => Login(logger, "ada", "hunter2", null));

    record.FormattedMessage.ShouldBe("Login for {User} with {Password}");
}
```

Going through `ILogger` also means the test exercises message templates, which
is where the attribute keys come from — so the `{Token}` naming advice above is
verified by this test rather than merely stated near it.

## 13.5 Health checks

Three distinct endpoints, because Kubernetes asks three distinct questions.

Registration and exposure live in different places, for one reason: the checks
need connection strings and the endpoints do not.

**The checks** are registered by the service's own Infrastructure — the block
shown in `AddOrderingInfrastructure` (§4.2), which has the configuration:

```csharp
services
    .AddHealthChecks()
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
    app
        .MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
        .AllowAnonymous();

    app
        .MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") })
        .AllowAnonymous();

    app
        .MapHealthChecks("/health/startup", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") })
        .AllowAnonymous();

    return app;
}
```

A host that registers no readiness checks therefore reports ready immediately.
That is correct for exactly two hosts here — the **gateway** and the **BFF**,
which own no database (§4.2) — and correct for neither of the six services.
Every service owns a schema, including the two with no public API: Shipping and
Notifications both ship a migrator and both register a SQL check (§4.1, [§3.2](03-bounded-contexts.md)).

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

## 13.6 What to alert on

Alert on symptoms users experience, not on causes. Each alert should be
actionable — if the response is "acknowledge and ignore", delete it.

| Alert | Condition | Why | Runbook |
|---|---|---|---|
| Error rate | 5xx > 1% over 5 min | Users are seeing failures | `error-rate.md` |
| Latency | p99 > 1 s over 10 min | Users are waiting | `latency.md` |
| Error queue depth | > 0 | A business process has stopped | `error-queue.md` |
| Saga age | any saga unfinalised > 1 h, **excluding one awaiting despatch** | Orders are stuck. Every other wait state in §9.6 times out in 5, 10 or 15 minutes, so an hour is a sane margin above all of them — but the despatch wait is **three days** by design, three orders of magnitude further out, and an unqualified hour would page on the healthy path for most of a saga's real lifetime. A despatch that genuinely expires escalates to the row below, not to this one | `stuck-saga.md` |
| Orders awaiting review | any row in `ordering.OrderReviews` older than 1 h | A saga hit a wait it could not compensate and escalated (§9.6). It has already finalised, so the saga-age alert above will *not* catch this | `order-review.md` |
| Migration job failed | Helm `pre-upgrade` hook non-zero, or a release stuck pending | The deploy stopped before any pod rolled ([§7.4](07-persistence.md)); the previous version is still serving, which is why nothing else fires | `migration-failure.md` |
| Cache hit ratio collapse | `rate(cache_hits) / rate(cache_hits + cache_misses)` < 50% over 10 min, from `Microsoft.Extensions.Caching.Hybrid` | Redis lost its working set; every miss becomes a database read, and the databases are sized for a warm cache (ADR-006) | `redis-cold.md` |
| Business volume | `orders.placed` per hour drops > 50% vs the same hour last week | The most valuable alert here — it catches failures no technical metric detects. §6.6's worked case: `ordering.ProductPrices` has no row for a product, every order containing it fails validation, and the result is a 400 the customer sees, no exception, no 5xx and no lag. Week-over-week rather than a fixed floor, because a volume alert without a seasonality model is the first pager people mute | `business-volume.md` |

### Outbox alerts are per lane

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
        Meter meter = factory.Create(MeterName);

        meter.CreateObservableGauge(
            "outbox.oldest.age",
            () => new[]
            {
                new Measurement<double>(stats.OldestAgeSeconds(OutboxLane.Broker), Tag(OutboxLane.Broker)),
                new Measurement<double>(stats.OldestAgeSeconds(OutboxLane.Local), Tag(OutboxLane.Local))
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
                new Measurement<int>(stats.PendingCount(OutboxLane.Local), Tag(OutboxLane.Local))
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
                new Measurement<int>(stats.AbandonedCount(OutboxLane.Local), Tag(OutboxLane.Local))
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
            using IServiceScope scope = scopes.CreateScope();
            using IDbConnection connection =
                scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().Create();

            return connection.ExecuteScalar<double?>(
                """
                SELECT DATEDIFF(second, MIN(OccurredAt), SYSDATETIMEOFFSET())
                FROM ordering.OutboxMessages
                WHERE ProcessedAt IS NULL
                    AND Lane = @lane;
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
    OutboxMetrics _,
    OrderMetrics __,
    MessagingMetrics ___,
    RequestMetrics ____) : IHostedService
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
    IEnumerable<Type> registered = BuildServices()
        .Select(d => d.ServiceType)
        .Where(t => t.Name.EndsWith("Metrics"))
        .Distinct();

    HashSet<Type> forced = typeof(MetricsInitialiser)
        .GetConstructors()
        .Single()
        .GetParameters()
        .Select(p => p.ParameterType)
        .ToHashSet();

    // Both directions. Unforced-and-unexplained is the drift this exists for;
    // forced-but-unregistered is a host that will not start.
    registered
        .Where(t => !forced.Contains(t) && !NotForced.ContainsKey(t))
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

## 13.7 Starting SLOs

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
([§15.1](15-cicd-deployment.md)) — the load run in CD, which asserts the targets in this table and is the
first real gate after the dev deploy. Not a "smoke test": §15.1 declines to have
one and §12.1 gives the reason, which is that a stage named for what it actually
does gets maintained. This is also not a capacity test — it catches the
regression where a query loses its index and goes from 40 ms to 4 s, which no
unit test will find.

## 13.8 Ownership

| Artefact | Owner |
|---|---|
| Golden-signal dashboards (RED, saturation) | Platform |
| Business metric dashboards | The service team |
| Gateway 5xx, infrastructure alerts, **broker-lane** outbox stalls (usually a shared-broker fault) | Platform |
| Own p95, **local-lane** outbox stalls and projection lag, abandoned rows, consumer failures | The service team |

Dashboards are **code**, checked into `deploy/observability/` as Grafana JSON or
equivalent. A dashboard clicked together in a UI is lost with the instance and
cannot be reviewed.

## 13.9 Runbooks

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

[← §12 Test strategy](12-test-strategy.md) · [Index](README.md) · [§14 Local development →](14-local-development.md)
