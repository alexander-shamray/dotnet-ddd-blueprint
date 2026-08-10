# PR-13 — MassTransit RabbitMQ registration and harness smoke

Design spec, frozen at write time. Appendix C names the PR
(`feat(template): MassTransit RabbitMQ registration and harness smoke`,
depends 08 and 06) and its deliverables: the bus connects, publish/consume is
proven with the in-memory harness, and the outbox is deliberately absent —
"split from the outbox deliberately to keep the review readable". §9 is the
chapter this PR starts implementing; §9.4's outbox and consumers are PR-14's
and PR-15's halves of it.

## What this PR is

The smallest true statement about messaging: a service host that owns a bus
connection, proves it can publish and consume through MassTransit's pipeline,
and reports broker readiness — with nothing riding on the bus yet. Catalog is
the template (§4.5), so the registration lands in Catalog and the scaffold
inherits it; PR-14's outbox is what will put the first real message through
it. The same landing shape as PR-04's dispatcher and PR-12's Redis helpers:
mechanism first, consumer with its own PR.

## Where it lives — `Catalog.Infrastructure/Messaging`

**Decision.** A new folder-scoped registration class, mirroring
`Common.Infrastructure/Redis/DependencyInjection.cs` exactly:
`src/Services/Catalog/Catalog.Infrastructure/Messaging/DependencyInjection.cs`,
namespace `Catalog.Infrastructure.Messaging`, static class
`DependencyInjection`, one method —
`AddMassTransitMessaging(this IServiceCollection, IConfiguration)`. Called
from `AddCatalogInfrastructure`, which is the shape §4.2's
`AddOrderingInfrastructure` sample already shows
(`services.AddMassTransitMessaging(configuration)`).

**Why.** Per-service, not `Common.Infrastructure`, because the helper is where
each service's consumers, sagas and receive endpoints will be configured —
§9.6 registers Ordering's saga *inside* `AddMassTransitMessaging`, so the
helper cannot be shared without a callback surface nothing needs yet. It also
keeps MassTransit out of `Common.Infrastructure` until PR-14's outbox, the
first common code that names a MassTransit type (`IPublishEndpoint` in
`OutboxDispatcher`) — the register-honesty argument applied to package
references.

**Consequences.** `Catalog.Infrastructure.Messaging` is the namespace §9.4
already uses for the per-service messaging types
(`Ordering.Infrastructure.Messaging`), so consumers land beside their
registration in PR-15 without a move. §4.2's gates are untouched: Domain and
Application still reference no MassTransit, and the existing
`Application ↛ MassTransit` architecture test keeps proving it.

## The registration

```csharp
public static IServiceCollection AddMassTransitMessaging(
    this IServiceCollection services,
    IConfiguration configuration)
{
    string connectionString = configuration.GetConnectionString("RabbitMq") ??
        throw new InvalidOperationException(
            "ConnectionStrings:RabbitMq is not configured. The bus cannot start without it (§9, §13.5).");

    services.AddMassTransit(x =>
    {
        // MassTransit 8.5 reports anonymous usage data to a vendor endpoint
        // after the bus starts, enabled by default. §13.2 owns this
        // platform's telemetry, and none of it leaves the cluster silently.
        x.DisableUsageTelemetry();

        x.UsingRabbitMq((context, cfg) =>
        {
            cfg.Host(new Uri(connectionString));

            // Nothing to configure yet — no consumers exist until PR-15.
            // The call stays because it is the line every later consumer
            // rides in on, and its absence is the silent kind of wrong.
            cfg.ConfigureEndpoints(context);
        });
    });

    return services;
}
```

Decisions inside it, each argued:

- **`ConnectionStrings:RabbitMq` is the key** — §14.1's Compose file and
  §12.4's fixture both already spell it, and §15.4's secret table lists it.
  The value is an AMQP URI (`amqp://guest:guest@rabbitmq:5672` locally).
- **Read eagerly, throw naming the key.** PR-08's precedent (`AddSqlServer`
  throws on null): a host with no broker configured must not start. Read
  inside `UsingRabbitMq`'s callback it would surface at bus start — after the
  host is up, past `ValidateOnBuild`, in a background service's log.
- **`DisableUsageTelemetry()`** — verified at the pin: `UsageTelemetryOptions.
  Enabled` defaults to `true` in 8.5.3 and reports after bus start. An
  outbound report to a vendor endpoint from every service host is a decision,
  and this blueprint decides no.
- **No endpoint-name formatter, no retry policy, no consumers.** Retry is
  configured per receive endpoint (§9.8) and there are none; a formatter
  choice with no endpoint to name is configuration nothing reads.
- **`MassTransitHostOptions` stays at its defaults** — `WaitUntilStarted` is
  `false` at this pin, so the host starts while the bus connects in the
  background and readiness carries the wait. Blocking startup on the broker
  would convert a RabbitMQ outage into a pod that cannot boot, which is
  §13.5's restart-storm argument one dependency over.

## Readiness — MassTransit's own check, and §13.5 is amended

**Decision.** The broker readiness signal is the health check MassTransit
itself registers: verified in the 8.5.3 source, `AddMassTransit` contributes
an `IConfigureOptions<HealthCheckServiceOptions>` that adds a `BusHealthCheck`
named **`masstransit-bus`** with tags **`ready`** and **`masstransit`** — so
`MapCommonHealthEndpoints`' `ready` predicate picks it up with no registration
line in `AddCatalogInfrastructure` at all. The
`.AddRabbitMQ(name: "rabbitmq", tags: ["ready"])` line in §4.2's and §13.5's
samples is removed, the `AspNetCore.HealthChecks.Rabbitmq` pin is removed from
`Directory.Packages.props`, and its identity comes off Appendix B's
health-check row.

**Why.** The sample line is a latent defect found by this implementation. The
Xabaril package's parameterless `AddRabbitMQ()` resolves an
`RabbitMQ.Client.IConnection` from the container — and nothing registers one:
MassTransit does not expose its connection as an `IConnection`, so the check
as written throws on every probe and the pod is never ready. Making it work
means constructing and holding a **second** AMQP connection whose only job is
answering a question the bus's own check answers better — `BusHealthCheck`
reports the actual bus, endpoints included, not just socket reachability.

**Consequences.** §13.5 keeps its rule ("a host with a connection string has a
readiness check") and gains the mechanism: the bus check rides in with
`AddMassTransit`, which is why the registration block shows no RabbitMQ line —
stated in both amended samples, so the absence reads as a decision rather than
an omission. The SqlServer and Redis rows of the Appendix B health-check entry
survive; only the Rabbitmq identity leaves.

## Compose — catalog-api joins the broker

`deploy/compose/docker-compose.yml`, catalog-api block only:

- `ConnectionStrings__RabbitMq: "amqp://guest:guest@rabbitmq:5672"` — a plain
  value, exactly as §14.1's ordering-api sample spells it. No `${…}` variable:
  guest/guest is RabbitMQ's documented local default (§14.1's endpoint table),
  not a secret with a production shape, and `.env.example`'s contract is that
  every *variable* has a default — a variable nothing would override is a knob
  that exists to be documented.
- `depends_on` gains `rabbitmq: { condition: service_healthy }`.
- The env comment is rewritten: the bus key is here because PR-13's code reads
  it; Redis keys still wait for their first reader, the outbox for PR-14, JWT
  for PR-16.

The migrator block is untouched — a job host has no bus. `.env.example`,
`docker-compose.infra-only.yml` and the compose README are untouched: no new
variable, rabbitmq was already infrastructure, and the ports table already
lists the management UI.

## Tests

### `MessagingRegistrationTests` — Catalog.Api.Tests, no Docker

The harness smoke Appendix C names. `AddMassTransitTestHarness` is verified at
the pin to **replace an existing `AddMassTransit` registration** with the
in-memory transport ("If MassTransit has already been configured, the existing
bus configuration will be replaced"), which is what lets the test drive the
*production* helper rather than a parallel registration:

- **Publish/consume through the real registration**: build a
  `ServiceCollection`, call `AddMassTransitMessaging` with an in-memory
  configuration carrying a fake AMQP URI, then `AddMassTransitTestHarness`
  registering one test-local consumer; start the harness, publish the
  test-local record, assert consumed and published. This proves the
  registration composes, the pipeline delivers, and nothing in the helper
  depends on a live broker.
- **Registration surface**: after `AddMassTransitMessaging` alone, the
  collection contains the bus (`IBus`) and its hosted service — asserted on
  descriptors without building a provider, the PR-12 shape.
- **Missing key throws at registration**, naming `ConnectionStrings:RabbitMq`.

Home: `Catalog.Api.Tests` because the helper is Infrastructure wiring and that
suite already owns the composition-level assertions (`HostSmokeTests` asserts
Infrastructure's health registrations today); `Catalog.Application.Tests`
would put a MassTransit reference beside the suite whose architecture test
proves Application has none, which is legal and needlessly confusing.

### `HostSmokeTests` — updated

- The class fixture becomes `UnreachableInfrastructureFactory` (renamed from
  `UnreachableSqlFactory`): unreachable SQL **and** an unreachable AMQP URI
  (`amqp://guest:guest@catalog-rabbit.invalid:5672`), both `.invalid` so
  failure is NXDOMAIN, not a timeout. `WaitUntilStarted=false` is what makes
  this safe — the host starts, the bus retries in the background, and the
  class stays fast.
- `Ready_probe_reports_the_sql_check` becomes
  `Ready_probe_reports_the_sql_and_bus_checks`: two registrations, `sql` and
  `masstransit-bus`, both tagged `ready` — the bus row asserted against the
  name and tags the 8.5.3 source guarantees, so a MassTransit major that
  changes them fails here rather than in a cluster.
- `Ready_probe_returns_503` and the liveness test hold unchanged: liveness
  must not consult the bus, and the live probe passing against an unreachable
  broker is now asserting that for two dependencies.

### `ServiceFixture` / `CatalogApiFactory` — the RabbitMQ container joins

§12.4's fixture shape already shows it: a `RabbitMqContainer`
(`rabbitmq:4-management-alpine`, the image §14.1 runs) started alongside SQL
with `Task.WhenAll`, and the factory handed both connection strings.
`CatalogApiFactory` gains a second constructor parameter and a second
`UseSetting` — one type, both suites, same §12.4 argument as PR-10.

`DatabaseSmokeTests.Ready_probe_returns_200_against_a_real_database` becomes a
poll: the bus connects in the background (`WaitUntilStarted=false`), so
readiness legitimately answers 503 for the first moments and flips to 200 when
the bus is up. Polling until 200 within a deadline asserts exactly the
behaviour Kubernetes relies on — and is the "bus connects" deliverable proven
against a real broker, not inferred from the harness.

The Redis containers of §12.4's full shape still wait for the PR whose code
reads those keys — the fixture comment is updated to say only RabbitMQ joined.

## The scaffold — reconciled in the same change

`tools/new-service` classifies every file under the Catalog roots and must
learn: the new `Messaging/DependencyInjection.cs` (template — substituted
namespace, nothing Catalog-specific), the new `MessagingRegistrationTests.cs`
(template — no domain types), the csproj's `MassTransit.RabbitMQ` package
reference, the changed anchors in `DependencyInjection.cs`, `ServiceFixture`,
`CatalogApiFactory`, `HostSmokeTests` and `DatabaseSmokeTests`, and the
catalog-api compose block's two new lines. A scaffolded service therefore
inherits the bus registration, the harness smoke and the broker-ready fixture
— PR-18 dogfoods all of it. `py -3.12 -m unittest` green is the acceptance
gate, extended tests included.

## What this PR does not do

- **No consumers, no receive endpoints, no retry policy** — §9.8 configures
  retry per endpoint, PR-15 brings the endpoints.
- **No outbox, no `IIntegrationEventPublisher`** — PR-14, deliberately split.
- **No contracts** — `Common.Contracts` is PR-15's project; a message type
  invented early is a project invented early. The harness smoke's message is
  a test-local record, and stays one.
- **No `MessagingMetrics`** — §13.3's instruments are injected by consumers,
  which do not exist.
- **No Testcontainers RabbitMQ *assertions* beyond readiness** — observing
  what reached a real broker needs consumers; §12.4's fixture note already
  says the fixture deliberately has no harness.

## Packages

All already pinned and registered: `MassTransit.RabbitMQ` 8.5.3 (Appendix B
row "MassTransit 8.x", pinned ahead of use since PR-01),
`Testcontainers.RabbitMq` 4.6.0. Removed: `AspNetCore.HealthChecks.Rabbitmq`
pin and its Appendix B identity, per the readiness decision. No new pins.

## Blueprint reconciliation, same PR

- **§4.2**: `AddOrderingInfrastructure`'s health block loses `.AddRabbitMQ`,
  gains the one-line statement that the bus check rides in with
  `AddMassTransit`.
- **§13.5**: same edit in the printed block, plus the prose stating the
  mechanism and why a transport-level check was rejected.
- **Appendix B**: the health-check row's identity list and argument lose
  Rabbitmq.
- **Appendix D**: D.5's `AddMassTransitMessaging` half of the shared row gains
  its description now that the helper exists — per-service home, eager key
  read, telemetry decision.
- **`Directory.Packages.props`**: `AspNetCore.HealthChecks.Rabbitmq` removed.
- **CLAUDE.md**: phase section — PR-13 landed with its binding decisions,
  PR-14 next; Catalog.Infrastructure tree line gains Messaging; test counts
  refreshed from the actual runs.
- **`docs/roadmap.md`**: no edit expected; verified at the end.

## Risks

- `AddMassTransitTestHarness`'s replace-existing-bus path and the
  `masstransit-bus` name/tags are read from the v8.5.3 source, not docs;
  both are asserted by tests, so a surprise fails the suite, not the field.
- The readiness poll's deadline is generous (30 s) because a cold RabbitMQ
  container plus bus handshake is measured in seconds; if it flakes in CI the
  finding is a fixture-ordering bug, not a threshold to raise blindly.
