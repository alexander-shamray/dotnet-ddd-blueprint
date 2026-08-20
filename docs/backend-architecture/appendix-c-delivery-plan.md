# Appendix C — Delivery plan

An architecture document that does not say what to build first is a description,
not a plan. This appendix sequences the work into independently reviewable pull
requests. Every PR leaves `main` building and green.

It says nothing about how long. That lives outside the blueprint, in the
[delivery roadmap](../roadmap.md) — one estimate per PR below, the calendar
they imply, and the assumptions holding it up.

## C.1 Service build order

Two orderings matter and they are different. The **platform** is built in the PR
sequence below. The **services** are built in this order, and not in parallel:

1. **Catalog** — simple domain, and the only service that owes nothing to
   another. Establishes the CQRS structure, caching and the query patterns, and
   is the first thing through the deployment pipeline (PR-10).
2. **Ordering** — the core domain. Rich aggregate and outbox (PR-18), saga
   (PR-21). The split matters because C.2 sequences them apart: PR-18 is the
   scaffold, the database and §11.4's ownership 404, and the state machine's
   later transitions have no caller until the saga drives them.
3. **Inventory and Payments** — concurrency and third-party integration, once
   the surrounding patterns have settled.
4. **Shipping** — depends on everything upstream being stable.
5. **Notifications** — last, and not because it is hard. It has no domain
   logic, no public API and publishes nothing: its entire subscription list is
   seven events belonging to Ordering, Payments and Shipping ([§3.2](03-bounded-contexts.md)). Built
   before them it is a consumer with no producers, which can be deployed but
   not exercised.

> **A pure consumer cannot be the thing that proves the pipeline.** An earlier
> version of this list put Notifications first, on the reasoning that a service
> with no domain logic proves messaging, observability and deployment end to
> end while there is nothing else to debug. The first half is true and the
> conclusion does not follow: end to end needs both ends, and every event
> Notifications reads is emitted by a service that would not exist yet. C.2 was
> never written that way — PR-10 is Catalog and PR-18 is "second service" —
> so the sequence below is what the plan always did.

The first service through the pipeline finds every gap in deployment,
observability and testing. Fixing those once is far cheaper than fixing them
again for each of the seven deployables behind it — the five remaining services
plus the gateway and the BFF, which take the same pipeline ([§15.1](15-cicd-deployment.md)).

## C.2 Pull request sequence

Phase names map to the `phase` column. Dependencies are PR numbers.

### Foundation

| PR | Title | Depends | Delivers |
|---|---|---|---|
| **01** | `chore: solution structure, SDK pin, central package management, CI skeleton` | — | `global.json`, `Directory.Build.props`, `Directory.Packages.props` with **exact** versions, `.editorconfig`, solution, CI running `dotnet test`, **licence allow-list gate** |
| **02** | `feat(common): Result, Error, and domain primitives` | 01 | `Result`/`Result<T>`, `Error`, `Entity<TId>`, `AggregateRoot<TId>`, `IDomainEvent`, typed-ID pattern. **Unit tests ship in this PR** — the convention starts here |
| **03** | `feat(common): ProblemDetails, error catalogue, correlation middleware` | 02 | RFC 9457 mapping, the status-code table from [§10.5](10-api-gateway.md), `X-Correlation-Id` middleware, `ToHttpResult()` |
| **04** | `feat(common): CQRS dispatcher and pipeline behaviours` | 02 | The dispatcher from [§6.2](06-cqrs.md), logging and validation behaviours, tests asserting behaviour **ordering**. No transaction behaviour yet |
| **05** | `feat(common): OpenTelemetry and structured logging defaults` | 03 | `Common.Web`: OTLP export, resource attributes, health endpoint wiring, log redaction policy |
| **06** | `feat(dev): Docker Compose — SQL Server, Redis, RabbitMQ, Keycloak, OTel` | 01 | The Compose file from [§14.1](14-local-development.md) — infrastructure at this PR, each application block with the PR that builds its image — `.env.example`, the placeholder realm and collector config it mounts, documented ports, healthchecks, and the path-filtered CI smoke that proves them (`config -q`, `up --wait`, `down -v`) |

### Service template

| PR | Title | Depends | Delivers |
|---|---|---|---|
| **07** | `feat(template): service skeleton and architecture test gate` | 02–06 | Compilable empty service across five projects ([§4.1](04-solution-structure.md)), Minimal API host, health endpoints, OpenAPI. **NetArchTest gate from this PR**: domain isolation, Application ↛ EF Core, endpoints ↛ Infrastructure, Application and Domain ↛ MassTransit (§4.2, [§9.3](09-messaging.md)) |
| **08** | `feat(template): EF Core, repositories, IUnitOfWork, migrator host` | 07, 06 | `DbContext` sealed in Infrastructure, `IUnitOfWork` port, `*.Migrator` project, **dual connection strings** ([§7.1](07-persistence.md)), Testcontainers smoke test |
| **09** | `feat(common): TransactionBehavior over IUnitOfWork` | 04, 08 | §6.3 behaviour. Tests proving `SaveChanges` is called once on success and never on failure, that a handler which writes through `ExecuteRawAsync` and then returns `Result.Failure` leaves no row, and that queries never open a transaction |
| **10** | `feat(catalog): first vertical slice — command, query, cursor pagination` | 07–09 | One aggregate, one command, one cursor-paginated query, the service's Dockerfile and Compose block, and the `docker-compose.infra-only.yml` override ([§14.1](14-local-development.md)) — the first containerised service, and the template PR-11's scaffold copies. **The write endpoint is deliberately unauthenticated and this is stated in the README** — closed by PR-16. The listing is not a gap and was never one: [§10.2](10-api-gateway.md)'s `catalog-public` route is GET-only and carries no policy, so a product listing is public at the edge and stays public here |
| **11** | `feat(tooling): new-service scaffold script` | 07, 10 | Copies and renames the template ([§4.5](04-solution-structure.md)): ports, database name, solution entries, Compose block. **The template is Catalog itself, read at run time** — one copy of the wiring, not two that drift — and the slice is excluded, so a new service inherits the wiring and none of the domain. Dogfooded by PR-18 |

### Data, cache, messaging

| PR | Title | Depends | Delivers |
|---|---|---|---|
| **12** | `feat(common): Redis helpers — HybridCache, key namespaces, distributed locks` | 06, 08 | Key-naming helper, **mandatory TTL enforced in code**, `{service}:cache\|lock\|idem\|denylist:` namespaces, the eviction-policy isolation from [§8.1](08-caching-redis.md), Testcontainers Redis tests |
| **13** | `feat(template): MassTransit RabbitMQ registration and harness smoke` | 08, 06 | Bus connects, publish/consume proven with the in-memory harness. **Split from the outbox deliberately** to keep the review readable |
| **14** | `feat(template): transactional outbox and allow-list event mapper` | 09, 13, 10 | Outbox table and dispatcher (§9.4), `IIntegrationEventMapper` allow-list, `IIntegrationEventPublisher` with the §9.3 contract. **`MessageTypeMap` and `OutboxJson` land here, not later**: both halves of what the `MessageType` and `Payload` columns mean, and a column whose format is decided after rows exist in it is a migration nobody wants. **`Common.Contracts` is created here too, with `IIntegrationEvent` and the one contract the allow-list maps to** — `Stage` reads that interface and `MessageTypeMap` selects on it, so neither compiles without it, and a mapper with an empty registry could not carry the test below. Integration tests proving aggregate row and outbox row commit in **one** transaction, that `Stage` copies the envelope's `MessageId`, and that every stageable domain event round-trips through the registered `OutboxJson` ([§12.4](12-test-strategy.md)) |
| **15** | `feat(messaging): Contracts, inbox consumers, inbox + outbox retention purge` | 14, 12 | The **remaining** versioned records in the `Common.Contracts` PR-14 created, inbox filter (§9.5), the `IntegrationEventConsumer<T>` adapter (§9.4), one purge hosted service covering **both** tables. **`Platform.IntegrationTests` starts here** with the §12.6 contract suite — no domain reference, versioned namespace, round-trip — because the rules arrive with the assembly they constrain |

### Edge and security

| PR | Title | Depends | Delivers |
|---|---|---|---|
| **16** | `feat(security): JWT bearer with mandatory per-service re-validation` | 03, 10 | Keycloak realm import, JWT validation in `Common.Web`, permission policies, test auth handler, and §11.4's `ICurrentUser` — common rather than per-service, the chapter amended to match. Closes PR-10's deliberately unauthenticated write path; the listing stays anonymous, because [§10.2](10-api-gateway.md)'s `catalog-public` route is GET-only and carries no policy. **Security tests: forged header without a token → 401, and a caller holding the wrong permission → 403.** The **404 half moves to PR-18**: hiding user B's resource from user A needs a resource with an owner, and Catalog has none — every product is public to every caller by design. Ordering's `CancelOrderHandler` is the first aggregate the check applies to |
| **17** | `feat(gateway): YARP routing, JWT, rate limiting, CORS` | 06, 16, 10 | The gateway from §10: [§10.2](10-api-gateway.md)'s whole route file — ahead of three of the four services it points at, because the tests below say nothing over one route — the rate-limit policies of §10.3 through `IProblemDetailsService`, the gateway's own `inventory:admin` authorization policy, the two conditional blocks of [§4.2](04-solution-structure.md), and correlation ID assignment. **Two config tests, both on `ReverseProxy:Routes`: every `AuthorizationPolicy` and `RateLimiterPolicy` named resolves, and every route's match minus its `PathRemovePrefix` is a prefix of the group its service maps (§10.2), which the in-process API tests cannot see.** Both were corrected by building it — an unresolvable policy stops the host rather than dropping the route silently, and Catalog's own route is why the second is a prefix rather than an equality. The dual-version pair stays an example in §10.2; what ships is the invariant that keeps its strips matched |
| **18** | `feat(ordering): second service from the scaffold` | 11, 08, 16 | Proves the scaffold. Own database, own migrator, and the Compose pair that makes §10.2's `ordering` route stop answering 502 — **the route itself is not a deliverable here**, because PR-17 shipped the whole route file ahead of three of its destinations and this is the first service to arrive behind one. A service PR that re-decides the route file is the mistake §10.2's dual-version trap describes. **Carries PR-16's deferred security test: user A cancelling user B's order → 404**, which is §11.4's ownership check and needs the first resource in the platform that has an owner — `CancelOrderHandler` is where the rule finally has something to apply to |
| **27** | `feat(gateway): response compression and request size limits` | 17 | The two entries of [§10.1](10-api-gateway.md)'s "It does" list PR-17 did not deliver, and the last outstanding piece of the gateway. **Neither was deferred for effort** — together they are four lines — but each needed a decision no chapter had taken. The body-size limit is **one mebibyte** in `GatewayLimits`, two orders of magnitude above the largest body this platform can construct; the HTTPS question is settled by [ADR-020](appendix-a-adrs.md#adr-020--the-edge-compresses-over-tls-and-says-so), which sets `EnableForHttps = true` because that flag is what makes compression happen at all: TLS terminates upstream, but §4.2's forwarded-headers block rewrites `Request.Scheme` from the ingress's header and the middleware decides at the first write, so at the default a gateway behind an HTTPS ingress compresses nothing. **Four things were found by building it, and one of them by a review after that.** The 413 needs no exception handler, unlike §10.5's 400 and 409 rows: Kestrel's `BadHttpRequestException` carries the status and `ExceptionHandlerMiddleware` reads it off. The limit cannot be tested over `TestServer`, which implements none of the body-size features, so this is the first suite in the solution to drive `WebApplicationFactory.UseKestrel`. The compression middleware has **no** ordering rule a test can catch — measured, by moving it — where its absence is caught immediately. The ADR's first argument was inverted: it read the *hop's* scheme rather than the one the middleware reads, which made a load-bearing flag look decorative. And the edge was violating RFC 9111 §5.2.2.6 — ASP.NET Core's compression middleware ignores `Cache-Control: no-transform`, which an intermediary may not — so the PR carries `NoTransformResponseCompressionProvider`, the one piece of machinery here that is conformance rather than choice. Numbered last and depending on PR-17 alone, so it may land at any point after it |

### Integration and operations

| PR | Title | Depends | Delivers |
|---|---|---|---|
| **19** | `feat(bff): the BFF host, its gRPC client and the one permitted sync hop` | 05, 12, 17, 18 | `Web.Bff` (§4.1), `AddStandardResilienceHandler` defaults, the timeout hierarchy asserted at startup, one deliberate **BFF → Catalog** pricing call demonstrating ADR-017. **The only host that gets an `Identity:Client`** ([§11.5](11-identity-authorization.md)) — the Keycloak client and secret arrive here and nowhere else. Catalog gains the server half: `pricing.proto`, a `GetPrices` slice and a second, **HTTP/2-only Kestrel endpoint on 8081**, because a cleartext port cannot serve HTTP/1.1 and h2c at once |
| **20** | `feat(ordering): consume Catalog events into a local projection` | 15, 18, 17 | The full async path, and the producer `ordering.ProductPrices` has been waiting for since PR-18 shipped its reader. `ProductPriceProjection` with **idempotent `MERGE` and the out-of-order guard** from §6.6, all three of Catalog's events because §3.2 gives Ordering all three, and the `ordering-catalog-events` receive endpoint of [§9.8](09-messaging.md) — retry, inbox filter, in-memory outbox, in that order. **`OrderSummaries` is not here**: §6.6's other projection is fed mostly by Ordering's own domain events on the local lane and needs §13.3's `OrderMetrics` and the escalated history query with it, none of which this PR's title names. **Three things were found by building it.** §9.8's printed `e.UseInMemoryOutbox()` carries `CS0618` at the pinned version, so ADR-019 had made the chapter's line unbuildable since it was written. §6.6's `MERGE` wanted `WITH (HOLDLOCK)`, which no test catches — measured, at eight-way and sixty-four-way concurrency — so it is a reasoned claim and labelled as one. And `ConfigureEndpoints(context)`, left in both services by PR-13, turned out to manufacture a queue with neither the inbox filter nor the retry policy for any consumer whose explicit binding is missing; it is gone from Catalog's registration too, because that file is the scaffold's template. **Two things are named as owed rather than built.** Broker-fed read-model staleness has no SLO — `messaging.delivery.lag` is recorded before the handler runs, so it cannot cover it, and closing the gap needs a §13.3 instrument. And this projection has **no rebuild path**: Ordering holds no source of truth for prices, so §6.6's day-one rebuild script has to be Catalog republishing — carrying each product's *original* `OccurredAt`, because a fresh one re-lists everything ever discontinued |
| **21** | `feat(ordering): order fulfilment saga` | 20, 14 | The state machine from §9.6, compensation paths, **a timeout on every wait state**, harness tests including the payment-declined compensation ordering. Also the four command handlers the saga sends to and §9.4's `ordering-commands` endpoint, because a saga whose commands nothing accepts is a saga sending into silence — and §9.3's allow-list, empty since PR-18, since the saga starts on Ordering's own `OrderPlaced`. **Four things were found by building it.** No chapter had ever named a **message scheduler**, and §9.6's four `Schedule` declarations do not work without one — [ADR-021](appendix-a-adrs.md#adr-021--saga-timeouts-are-scheduled-by-the-broker) settles it on the broker's delayed exchange, which makes §14.1's RabbitMQ the one infrastructure service that is built rather than pulled. §5.4's `Order.ConfirmStock` had **no caller and no way to acquire one**: the saga sends four commands and none of them advances the order out of `AwaitingStock`, so `ConfirmOrder` met an aggregate that refused it — closed by a fourth receive endpoint reading `StockReserved`, which §3.2 already grants, rather than by a fifth command it does not. §9.6's escalation insert was a read-then-write with no range lock, which is §6.6's `MERGE` finding one table over. And `ShippingAddressV1` silently dropped an address line, invisible until this PR became its first producer |
| **22** | `test: expand architecture rules and document the test strategy` | 07, 10, 14 | The rest of [§4.2](04-solution-structure.md)'s table as gates — the cross-service clause three of its five rows carry had no test at all, and the Migrator row had none of any kind — plus `docs/testing.md`, the `Category=Integration` split and the domain-layer coverage report. **Four things were decided by building it.** §4.2's composition-root rule granted host-level `*ServiceCollectionExtensions` an exemption the gate never implemented and no host ever wanted, so the prose narrows to the code rather than the gate widening to a hole nothing occupies. The cross-service rule cannot be a list of service names, because §4.5's scaffold renames the template's own name inside whatever it renders — so the gate asks a **measured** question instead: every package this platform pins is strong-named and none of its own projects is, `Dapper` alone excepted, which is a residual named rather than closed. The category goes on the `[CollectionDefinition]` and not on each test class, so joining the container collection *is* carrying the category and no reflection gate is owed — xUnit v3's propagation was measured, 10 and 71 out of 81 with no third state. And the coverage collector is the one `Microsoft.NET.Test.Sdk` already carries, so the figure cost no package and [Appendix B](appendix-b-licences.md) no entry |
| **23** | `feat(deploy): Helm charts, migration hooks, probes` | 17, 19, 20, 08 | Chart per service, umbrella chart, migration job as a `pre-install,pre-upgrade` hook, the probe and resource shape from §15.3 — plus `deploy/helm/smoke.sh`, which renders all five charts and asserts what comes out, and the second path-filtered workflow of §15.1. **Five things were found by building it.** Helm's `fullname` convention would have broken the platform's routing: §10.2's route file and §9.7's pricing hop hold their destination hosts as literals *on the stated grounds that the host is the Kubernetes Service name*, so a release-derived name is a 502 the moment the umbrella installs the same workload — the charts take a required `workload.name` instead, and the selector carries nothing release-scoped, because a selector is workload identity rather than release bookkeeping and a Deployment never lets that field change. §15.4's two Redis rows were marked required against a solution where no host calls `AddRedisConnections`, which is not over-supply but a `secretKeyRef` to a Secret nobody created and therefore a pod that never starts; they are now conditional on the consumer existing. `terminationGracePeriodSeconds` had a rule and no number, and the number is not free: `HostOptions.ShutdownTimeout` defaults to 30 s — measured, not read — and so does Kubernetes' grace period, so the default is a `SIGKILL` at the instant the drain would have finished. The pod template's rollout checksum hashed the ConfigMap the library renders and therefore missed the gateway's own, so editing `cors.origins` rewrote a mounted object and left the pod byte-identical — a config-only deploy that reports success and rolls nothing; it now hashes the whole of `.Values`. And the gate caught itself: "the gateway renders no migration Job" passed against a gateway that *declared* a migrator image, because that chart carries no migration template at all — the assertion is now about the two halves agreeing, and it was the **one** deliberate defect, of every one run through that gate, that failed to turn the run red |
| **24** | `docs(ops): runbooks, secrets, dashboards-as-code, the SLO run` | 15, 20, 21 | The twelve runbooks from [§13.9](13-observability.md) — one per alert, checked both ways — per-lane outbox alerts (§13.6), `docs/secrets.md`, Grafana JSON in `deploy/observability/`, and the k6 **SLO run** against staging (§13.7, §15.1) — named for what it asserts, because §15.1 deliberately has no smoke stage. **Five things were found by building it.** §13.6's twelve conditions became files and **four of them read an instrument nothing publishes** — the saga age, the review queue, `orders.placed`, and the cache ratio, whose meter §13.2 *does* register while `Microsoft.Extensions.Caching.Hybrid` 10.0.0 publishes no meter at all — it reports EventCounters through `HybridCacheEventSource`, so wiring Redis would not light it either, and the row is owed an instrument **and** a consumer. **That one was first diagnosed as owed only a consumer**, because nothing calls `AddRedisConnections`; a gate was written on that premise, observed red and removed once the package was read, which is the more useful half of the finding — a visible absence was taken for the cause. So those rules ship unloaded in `awaiting-signal.yaml` and the gate asserts their metrics are published by **nothing**, which is what makes the list self-clearing rather than a list of alerts nobody ever turned on. §13.6's `MetricsInitialiser` did not compile: unread primary-constructor parameters are CS9113 three times over and the discard-looking `_` names do not escape it, while `ct` is CA1725 against `IHostedService` — and CA1707, the rule a reader expects to fire on those names, does not. Its `OutboxStats` reached for an `IServiceScopeFactory` to avoid holding a `DbContext`, but §6.5's `IDbConnectionFactory` is a singleton holding a string, so the scope guarded against a dependency shape the code does not have. §13.6's saga alert excludes "one awaiting despatch" and there is no such state — `OrderFulfilmentSaga` arms the three-day timeout on the transition **into `Confirmed`**, so a selector spelled `AwaitingDespatch` would match no series, exclude nothing, and page on every confirmed order an hour after payment. And the gate's own instrument reader missed every gauge on its first run, because `CreateObservableGauge` infers its type argument and a pattern requiring `<…>` finds the histograms and the counters and silently skips the three types this PR added |
| **25** | `ci: integration categories, canary deploy, quality gates` | 20, 22, 17 | Path-filtered per-service builds, containerised integration tests in CI, canary with automated rollback on error rate or p99 — plus [§15.1](15-cicd-deployment.md)'s filter-inventory check, its per-service image build, and the fourth path-filtered `deploy/**` workflow. **Six things were found by building it.** §15.5 specified a canary and no chapter had chosen a **mechanism**, which [ADR-022](appendix-a-adrs.md#adr-022--the-canary-is-a-second-release-weighted-by-replicas) now records: an ingress-controller weight is disqualified by topology rather than taste, because this platform has one Ingress and everything behind it is dialled by Service name ([§10.2](10-api-gateway.md), [§9.7](09-messaging.md)) — so an edge weight cannot canary Catalog or Ordering at all. The two tracks then needed telling apart, and **`service.version` cannot do it**: `BuildInfo` strips the source-revision suffix on purpose and [§4.4](04-solution-structure.md) pins no assembly version, so every host in the platform reports `1.0.0` — a registered, exported, constant attribute, which is §13.6's own trap in a new place. `deployment.track` through `OTEL_RESOURCE_ATTRIBUTES` replaced it and **no production C# changed**, because the SDK's resource builder already honours the variable — established by a test against an exported resource rather than by reading the overload. §15.5's **5% is not expressible** at §15.3's `replicaCount: 3`, where one canary pod already serves 25%: it needs 19 stable replicas, and `autoscaling.maxReplicas` is 20, so the smallest configuration in which the first rung is real is exactly the largest the chart allows. The weight arithmetic was then **wrong in floating point at the one input the ladder starts from** — `19 * 0.05 / 0.95` is `1.0000000000000002`, so `ceil` bought two pods and served 9.5% under a label reading 5% — found by the test asserting that the function naming the required replica count and the function checking it agree, and fixed by doing the whole calculation in integers. `--logger trx`, which the stage gate counts from, **moves the coverage collector's output**: each stage then leaves the run's merged attachment *and* one partial per test project, so the single-file reporter had to become a merging one — and the union is worth having, at 257 lines against the unit stage's 253 and the integration stage's 192. And the Deployment's selector had to gain the track label, which is **immutable**: free today because nothing has installed these charts, and a downtime window if taken later |

### Optional

| PR | Title | Depends | Delivers |
|---|---|---|---|
| **26** | `chore(optional): consumer-driven contract tests` | 25 | Pact, only if a consumer relationship becomes contentious. Not required for completeness |

## C.3 Dependency graph

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
    P19 --> P23
    P20 --> P23
    P08 --> P23
    P21 --> P24[24 Runbooks + SLO]
    P15 --> P24
    P20 --> P24
    P20 --> P25[25 CI + canary]
    P22 --> P25
    P17 --> P25
    P17 --> P27[27 Gateway compression + size limits]
    P25 --> P26[26 Optional: Pact]
```

The graph carries every edge in the tables above and no others. It is a
transcription, so it can drift silently — a missing edge suggests two PRs are
independent when the table says one blocks the other, which is the direction
that costs a wasted branch rather than a wrong build.

## C.4 Sequencing rules worth preserving

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

PR-16 closed it, and closing it settled a question the note could not: **only
the write path was a gap.** The listing is anonymous permanently, because
[§10.2](10-api-gateway.md)'s `catalog-public` route matches GET alone and
carries no authorization policy. A scheduled closure is worth having partly for
this — the PR that pays the debt is the one that reads the whole design and
finds out how much of it was ever owed.

**PR-11 is dogfooded by PR-18.** The scaffold script is proven by the next real
service, not by intent. If the script cannot produce Ordering, it is not
finished.

And one rule about the whole sequence: **from PR-02 onward, production code
lands with its tests in the same pull request.** Not "tests to follow" — the
follow-up PR is the one that gets deprioritised, and a test written after the
code has never been observed failing.

---

[← Appendix B](appendix-b-licences.md) · [Index](README.md) · [Appendix D →](appendix-d-type-inventory.md)
