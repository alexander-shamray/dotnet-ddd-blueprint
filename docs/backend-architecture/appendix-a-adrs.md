# Appendix A — Architecture decision records

Short-form ADRs. Each records what was decided, why, and what it costs. The
value is in the "consequences" column — that is what a future reader needs when
the decision looks wrong.

## ADR-001 — Database per service

**Decision.** Each service owns a SQL Server database. No shared tables, no
cross-database queries.
**Why.** A shared database couples deployment, schema evolution and scaling.
Any change to a shared table requires coordinating every service that reads it,
which reintroduces the constraint microservices exist to remove.
**Consequences.** No cross-service joins or foreign keys. Some data is
duplicated. Reporting needs a separate approach — read replicas or a warehouse
fed by events.

## ADR-002 — Async messaging as the default

**Decision.** Services integrate through events on RabbitMQ. Synchronous calls
are the exception and require an explicit justification.
**Why.** Chained synchronous calls multiply latency and failure probability; a
service that is temporarily down should queue work, not fail requests.
**Consequences.** Eventual consistency everywhere. Debugging requires
distributed tracing. UIs must handle "in progress" states.

## ADR-003 — MassTransit v8, pinned

**Decision.** Use MassTransit 8.x (Apache 2.0) and pin the major version.
**Why.** MassTransit v9 moved to a commercial licence in 2026 at $400–1,200 per
month. v8 remains Apache 2.0 and maintained into 2026, and its abstraction over
RabbitMQ keeps the broker replaceable.
**Consequences.** A migration decision is required when v8 maintenance ends.
Options: pay for v9, move to Wolverine, adopt a community fork, or use
`RabbitMQ.Client` directly. What preserves all four is that **no Application or
Domain code touches a MassTransit type**: publication goes through
`IIntegrationEventPublisher` ([§9.3](09-messaging.md)) and the only MassTransit surface is the
outbox dispatcher, the consumer classes and the bus configuration — all in
Infrastructure. Note that `IPublishEndpoint` and `IBus` are MassTransit types
and so are *not* the abstraction; using them as the seam would mean abstracting
MassTransit behind MassTransit.
**Review by.** Q4 2026.

## ADR-004 — No mediator library

**Decision.** Implement the command/query dispatcher and pipeline in
`Common.Application` — roughly 80 lines.
**Why.** MediatR moved to a commercial licence. The functionality used here is
small, and owning it removes a dependency, a licence obligation, and a layer of
reflection that obscures stack traces.
**Consequences.** A small amount of infrastructure code to maintain and test.
New developers cannot rely on MediatR familiarity, so the dispatcher needs to
stay simple and documented.

## ADR-005 — EF Core for writes, Dapper for reads

**Decision.** Aggregates persist through EF Core; queries use Dapper.
**Why.** EF Core's change tracking and rich mapping suit aggregate persistence.
For reads it adds overhead and invites accidental N+1 and over-fetching. Dapper
gives exact control over the SQL for the shapes the API returns.
**Consequences.** Two data access technologies. SQL in query handlers must be
maintained by hand when the schema changes — integration tests catch this.

## ADR-006 — Redis for cache and coordination, never as a store of record

**Decision.** Redis holds cached read models, idempotency keys, distributed
locks and the token denylist. Nothing that must survive its loss. These are
split across **two instances with different eviction policies**: a cache
instance under `allkeys-lru`, and a coordination instance under `noeviction`
for locks, idempotency keys and the denylist. Shared rate-limit counters belong
on the coordination instance when they are built; the gateway's v1 limiter is
in-process and per-replica, and [§10.3](10-api-gateway.md) states what that costs.
**Why.** Redis is fast and its durability guarantees are weaker than SQL
Server's. Treating it as authoritative for anything means accepting data loss.
The split exists because eviction policy is a property of the whole keyspace:
an `allkeys-lru` instance under memory pressure will evict a held lock or a
revoked-token entry with no error and no log line ([§8.1](08-caching-redis.md)).
**Consequences.** Two Redis instances to run, two connection strings, and a
keyed-service registration so choosing the wrong one is a visible decision.
Every cached value must be reconstructible from SQL Server. A cold cache causes
a load spike on the databases, which capacity planning must allow for; a lost
coordination instance is more serious and is why it runs with persistence
enabled.

> **The token denylist named twice above is withdrawn by
> [ADR-033](#adr-033--revocation-is-bounded-by-the-token-lifetime-and-no-denylist-exists),
> and nothing here has been edited.** An ADR is superseded and never rewritten:
> the decision this record actually took — Redis is cache and coordination and
> never a store of record, split across two instances by eviction policy — is
> untouched and still binding, and so is every argument for it. What moved is
> one item in a list of what the coordination instance *holds*.
>
> **The Why paragraph is the part worth reading with that in mind.** It
> justifies `noeviction` partly by "a revoked-token entry" being evicted
> silently, and there has never been such an entry to evict: nothing has ever
> written the `{service}:denylist:` keyspace, and §11.3's `AddJwtBearer` reads
> no revocation list. The conclusion survives its example — a held lock and an
> idempotency claim are both real and both must not be evicted — but the
> example was doing more work than it could carry, which is how a keyspace with
> no reader kept reading as a control for four PRs.

## ADR-007 — Migrations as a pre-deploy job

**Decision.** Never call `Database.Migrate()` at application startup.
**Why.** Multiple replicas race; rolling deploys run old code against a new
schema; and the runtime identity would need DDL permissions.
**Consequences.** Every migration must be backward compatible with the running
version. Destructive changes become multi-release sequences.

## ADR-008 — YARP as the gateway

**Decision.** YARP, self-hosted, with routing/auth/rate-limiting only.
**Why.** MIT, actively maintained by Microsoft, configurable in code as well as
JSON, and it runs in the same stack the team already knows. Ocelot is
comparatively quiet; managed gateways add cloud coupling and cost.
**Consequences.** The gateway is a service to operate and scale. Its config must
stay disciplined — aggregation belongs in a BFF, not here.

## ADR-009 — Keycloak, not a hand-built identity service

**Decision.** Keycloak (Apache 2.0) as the OIDC provider.
**Why.** Authentication is a solved problem with a long tail of security detail
and no business differentiation. Building it creates liability and no value.
**Consequences.** Keycloak is another component to run, upgrade and back up.
Realm configuration must be source-controlled and imported, not clicked through
the admin UI.

## ADR-010 — Testcontainers, not in-memory providers

**Decision.** Integration tests run against real SQL Server, Redis and RabbitMQ
in containers.
**Why.** The EF Core in-memory provider does not enforce foreign keys, does not
implement `rowversion` concurrency, and translates LINQ differently. Tests green
against it still fail in production.
**Consequences.** Tests need a Docker daemon and take seconds rather than
milliseconds. Mitigated by sharing containers per collection and resetting with
Respawn.

## ADR-011 — Compose baseline, Aspire optional

**Decision.** Docker Compose is the documented local development environment.
Aspire is offered as an optional accelerator.
**Why.** Compose is universal, stable and language-agnostic, which suits a
reference architecture. Aspire gives a materially better inner loop and free
distributed tracing, but adds a fast-moving dependency and a visible tooling
opinion.
**Consequences.** Two local-dev paths to document and keep working. Mitigated by
Aspire's ability to emit a Compose file from the same model, and by the low exit
cost (section 14.2).

## ADR-012 — Contracts versioned by namespace

**Decision.** Integration events live in `Common.Contracts.<Service>.V<n>`.
Breaking changes create a new version; both are published during a deprecation
window.
**Why.** It is the only mechanism that lets producers and consumers deploy
independently, which is the entire point of the architecture.
**Consequences.** Contract changes require deliberate planning. Consumer
adoption must be tracked with telemetry before a version is retired.

## ADR-013 — Dapr not adopted

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

## ADR-014 — Wolverine not adopted, but kept as the exit

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

## ADR-015 — Minimal APIs, not MVC controllers

**Decision.** Endpoints are Minimal API groups.
**Why.** The endpoint layer translates HTTP to a command or query and does
nothing else. Controllers bring a base class, action filters and binding
conventions to do that, and the filter pipeline duplicates the dispatcher
pipeline.
**Consequences.** Endpoint classes must be organised deliberately — a single
`Program.cs` of two hundred `MapPost` calls is worse than controllers were. One
static class of extension methods per aggregate, registered from the composition
root.

## ADR-016 — Cursor pagination by default

**Decision.** Collection endpoints use opaque keyset cursors. `page`/`pageSize`
is not the default.
**Why.** `OFFSET n ROWS` costs proportionally to `n`, and results shift under
concurrent inserts, so a user paging through a live list sees duplicates and
skips.
**Consequences.** No "jump to page 47" and no cheap total count. Where a UI
genuinely needs either — an admin table over a bounded set — offset pagination
is an explicit, documented exception.

## ADR-017 — One synchronous hop

**Decision.** At most one synchronous downstream service call per inbound
request. Synchronous calls inside message consumers require a written exception.
**Why.** Availability multiplies and latency accumulates down a chain. Four
services at 99.9% give 99.6% — 43 minutes of monthly downtime becomes nearly
three hours with no service having missed its own target.
**Consequences.** Cross-context data must arrive by event and be projected
locally, which means designing for staleness in the UI. That is the intended
trade.

## ADR-018 — Reactions happen after commit

**Decision.** Nothing subscribes to a domain event inside the write
transaction. The dispatcher stages outbox rows and performs no other I/O;
projections, cache invalidation and integration publishing all run afterwards,
driven by the outbox ([§7.5](07-persistence.md)).
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

## ADR-019 — Warnings are errors, and the .editorconfig is a build input

**Decision.** `Directory.Build.props` sets `TreatWarningsAsErrors`,
`EnforceCodeStyleInBuild` and `AnalysisLevel latest-Recommended` from PR-01, and
takes no StyleCop package. Three code-style rules are configured at `warning`
and are therefore enforced — IDE0055 formatting, IDE0065 `using` placement,
IDE0161 file-scoped namespaces. Everything else in `.editorconfig` stays a
suggestion.
**Why.** [§4.1](04-solution-structure.md) commits to shared MSBuild settings
without saying what goes in them, and the answer does not get cheaper by
waiting: adopted at PR-01 the policy costs half a day against an empty
repository, adopted at PR-20 it costs a sweep across twenty pull requests
written without it. `TreatWarningsAsErrors` is what makes the other two settings
mean anything — a violation that only prints is a violation best hidden by the
longest build log, which is always the pull request that introduced the most of
them. StyleCop is declined because it restates rules `.editorconfig` already
carries and contradicts several of them outright, and a house style policed by
two tools that disagree is policed by neither.
**Consequences.** A compiler or analyser warning stops the build, so an SDK bump
can turn a clean tree red. That is the `global.json` pin (§4.4) earning its
place rather than an argument against the policy. Suppressions are not available
inline — `#pragma` is forbidden, so a warranted one goes in
`Directory.Build.props` with a comment saying why. Style rules whose exceptions
Roslyn cannot express stay at `suggestion` and remain a review matter: the four
cases that keep `var` are the live example, and a rule whose carve-out lives in
prose must not fail a build that cannot read the prose.

## ADR-020 — The edge compresses over TLS, and says so

**Decision.** `Gateway.Api` registers `AddResponseCompression` with
`EnableForHttps = true` and calls `UseResponseCompression` in its pipeline —
both halves are named because the registration on its own compresses nothing.
It takes the framework's default providers — Brotli and Gzip at
`CompressionLevel.Fastest` — and its default compressible type list, which does
**not** include `application/problem+json`. It replaces the framework's
`IResponseCompressionProvider` with one that refuses any response carrying
`Cache-Control: no-transform`, which RFC 9111 requires of an intermediary and
ASP.NET Core does not implement. No other host in the platform compresses
anything.
**Why.** The framework ships `EnableForHttps = false` because compressing a
response that mixes attacker-influenced input with a secret leaks the secret's
length, which is BREACH. **Not CRIME**, which attacked compression in the TLS
layer rather than of an HTTP response body, and naming both would conflate two
layers in the one paragraph deciding what this edge compresses.
**Here that flag is what makes compression
happen at all**, and the first version of this ADR argued the exact opposite.
It reasoned that TLS terminates at the load balancer or Ingress
([§10.1](10-api-gateway.md)) and plain HTTP is forwarded inside the cluster, so
the gateway is served `http` and the flag never fires. Every clause of that is
true except the conclusion. §4.2's forwarded-headers block enables
`XForwardedProto`, `UseForwardedHeaders` rewrites `Request.Scheme` from the
ingress's header, and the compression middleware takes its decision at the
first **write** — below the whole pipeline — so the scheme it reads is the
rewritten one. Left at its default, a gateway behind an HTTPS ingress
compresses **nothing**, and no response says why.
`ForwardedSchemeCompressionTests` is the measurement; it goes red against the
property removed.

So the flag cannot be argued from the scheme in either direction: the
*response* reaches the browser over TLS whatever the inner hop was, and the
inner hop is not what the middleware reads anyway. It has to be argued from
content, and the content is what makes it safe: the bodies crossing this edge
are proxied API JSON, and the platform puts no secret in one. Tokens are
issued by Keycloak and reach the gateway on an `Authorization` header rather
than in a body ([§11.2](11-identity-authorization.md)), no response sets a
session cookie, and no endpoint returns an anti-forgery token.
The one body that *does* reflect a client-supplied value back — §10.5's
problem+json, carrying the `X-Correlation-Id` the caller may have chosen
(§10.4) — is the one the default type list omits, so the input half and the
compression never meet.
**Consequences.** The gateway now spends CPU per response — which is precisely
the resource §15.3 deliberately leaves *unlimited*, because CPU is compressible
and a cap on it surfaces as unexplained p99 spikes long before the pod is short
of capacity. Memory is the one §15.3 bounds, and what to size it against is
**concurrent compressed responses** — each holds a compressor and its
buffers for the life of the response. Explicitly *not* §10.1's body ceiling:
that bounds a request, and nothing about it constrains how large a proxied
response is or how many are in flight. So an edge latency
regression is investigated as CPU spent here and never as a leak, and a
compression provider is the first thing to look at. The omission of
`application/problem+json` is a framework default this platform relies on and
does not state, so `CompressedResponseTests` pins it from the wire in both
directions — adding the type to
`CompressibleContentTypes` would be re-taking this decision, and the test is
what makes that visible. **The rule is inherited rather than re-decided by
every host behind the edge**: PR-19's BFF is the first that could hold a
session, and its responses pass through this middleware. A BFF response
carrying a secret says so with **`Cache-Control: no-transform`**, and
`Gateway.Api` honours it through a `ResponseCompressionProvider` of its own.

**That is a conformance fix, not a preference.** RFC 9111 §5.2.2.6 says the
directive "indicates that an intermediary (regardless of whether it implements
a cache) MUST NOT transform the content", and applying a content coding is such
a transformation (RFC 9110 §7.7). A YARP gateway is an intermediary. ASP.NET
Core's middleware does not implement the rule — measured, before the provider
existed: a body sent under the directive came back gzipped with the directive
intact — so the edge was violating it on every such response.

**The request form is honoured too, and it is a weaker thing.** §5.2.1.6 says
only that "the client is asking for intermediaries to avoid transforming the
content" — an ask, where the response form is an obligation. The provider
refuses either, because a caller who says so explicitly should be believed and
the check is one header read. The asymmetry is written down rather than
flattened into "the RFC requires it", which would be false of half of it.

**Reading the request header costs a `Vary` entry, and forgetting it would hand
the policy back to any cache in front.** The representation now depends on
`Cache-Control`, so the provider advertises it as a cache-selection dimension
on every decision — including the compressed ones, because a response
compressed *because* no directive arrived varies on the header exactly as a
refused one does. The price is cache efficiency, since callers send assorted
`Cache-Control` values; the alternative is a shared cache serving a stored
gzipped variant to the one caller who asked for none.

**A destination's `Vary: *` is left alone, and the framework's own entry is
not.** The wildcard covers every dimension, so adding a field name beside it
narrows nothing; the provider checks before appending, which is the idiom the
middleware already uses for `Accept-Encoding`. What the middleware does *not*
do is check for the wildcard — it appends `Accept-Encoding` regardless,
after the provider has answered and through no seam the provider can reach, so
a destination's `*` reaches the client as `*, Accept-Encoding`. Measured and
recorded rather than asserted as correct: it is the framework's behaviour, not
this platform's decision, and the test says which is which.

`Content-Encoding: identity` also stops the middleware, and is **not** the
contract offered here. It works only as a side effect of the
double-compression guard — a refusal reached by looking like an already-encoded
response — and it puts a content coding on the wire for no reason of the
client's. `no-transform` is what travels: the ingress, the CDN and every cache
on the path read it, where a content coding speaks only to whatever reads the
response next. `CompressedResponseTests` covers both, and the `no-transform`
one is red against the provider's registration removed.

**This too was written the wrong way round first**, and the correction is worth
keeping because the wrong version looked like a mitigation. It told the BFF to
*encode* the response itself. That protects nothing: gzip opens the same length
side channel wherever it is applied, so a BFF-compressed secret leaks exactly
as a gateway-compressed one does, and the pass-through test proves only that
the gateway declines to encode a second time. The header check is the same
mechanism either way; what changed is which value a downstream must send to be
safe. And a service that one day needs to
accept an upload meets §10.1's body ceiling first, which is a number in
`GatewayLimits` rather than a per-route setting: raising it is a platform
decision made once, in the open.

## ADR-021 — Saga timeouts are scheduled by the broker

**Decision.** [§9.6](09-messaging.md)'s saga schedules are delivered by
MassTransit's **delayed message scheduler** —
`AddDelayedMessageScheduler()` in the registration and
`UseDelayedMessageScheduler()` on the transport, both halves named because
either alone leaves `.Schedule(…)` throwing. On RabbitMQ that scheduler is the
`rabbitmq_delayed_message_exchange` plugin, so `deploy/compose/rabbitmq`
**builds** the broker image rather than pulling a stock one — the single
infrastructure service in [§14.1](14-local-development.md) that is built. No
Quartz, no Hangfire, and no scheduler process of this platform's own.

**Why.** No chapter specified one, and the omission was invisible until a state
machine with four waits was compiled: `Initially` arms `StockTimeout`, so the
very first `OrderPlaced` reaches for a scheduler the container does not hold.
Three options were live and one is disqualified outright.

**An in-memory scheduler is not a candidate**, and §9.6 rules it out in its own
words rather than on taste: "A saga waiting forever for a message that will
never arrive is an order stuck in limbo and a support ticket." A scheduler that
lives in the process loses every armed timeout on the next deployment, which
manufactures exactly that order — and does it silently, because the saga row
survives and looks healthy.

That leaves two durable answers, and they differ in **where the pending timeout
lives**. Quartz with an ADO job store would put it in Ordering's own database,
which is the argument §9.6 already makes for the saga instance one table over —
one database to back up, one migration history, one connection pool. It is the
better answer at scale and it is not the one taken here, for reasons that are
about this platform rather than about Quartz: it is three packages, roughly two
hundred lines of vendor DDL this repository would then own inside its own
migration, eleven `dbo`-prefixed tables cutting across the `ordering.` schema
every other table in this service sits in ([§9.4](09-messaging.md), §9.6), and a
second set of receive endpoints — because this
platform deliberately does not call `ConfigureEndpoints`, so the scheduler's own
consumers would each need declaring by hand ([§9.8](09-messaging.md)).

The broker's delayed exchange needs none of that: no package, no schema, two
registration lines, and durability that is already this architecture's model —
§9.8's own failure table says messages queue **in the broker** while a consumer
is down and that the outbox holds them while the broker is. A pending timeout is
a message in flight, and this platform's answer for a message in flight is the
broker.

**It also makes the test and the production registration the same two lines**,
which decided it. The in-memory transport implements the delay itself, so
§12.5's harness runs `AddDelayedMessageScheduler` and
`UseDelayedMessageScheduler` verbatim — the transport differs and the
registration under test does not. A Quartz production path tested over an
in-memory Quartz is a different mechanism wearing the same test.

**Consequences.** The broker image is no longer a stock tag, and that is the
whole of the cost *to deploy* — the cost to **run** is the uncancellable
timeout below, which is larger and is where this decision gets superseded.
The image is pinned to a **minor** (`rabbitmq:4.1-management-alpine`)
because the plugin is built against a broker line and `rabbitmq-plugins enable`
refuses one it does not match — a floating `4` would enable cleanly today and
fail the image build on whatever Tuesday 4.2 becomes latest. The plugin is
`ADD`ed by URL and then checked in the following `RUN` — `sha256sum -c` against
the pinned digest, then `chmod 644` — so a substituted asset fails the build
rather than reaching a broker, and a plugin the broker cannot read fails it too:
`ADD` from a URL lands 0600 and root-owned, `enable --offline` never opens the
archive, and the image therefore **builds cleanly and dies at start** with an
Erlang `eacces`. That was measured, not reasoned.

**Both of those belong on `ADD` and cannot go there**, which is the constraint
worth recording rather than the syntax. `ADD --checksum=` and `--chmod=` are
**BuildKit-only**, and this image is built by two builders: Compose uses
BuildKit, and §12.4's Testcontainers fixture uses the classic `/build` API,
which refuses them outright — *the --chmod option requires BuildKit*. A
Dockerfile only one of the two can build is a fixture that silently falls back
to a stock broker, which is the failure this whole decision exists to prevent.
The digest is therefore verified one layer later, after the file is written
rather than before; nothing reaches a running broker either way. **Do not
"tidy" these back onto the `ADD`** — that re-arms the fallback, and it is the
kind of edit that looks like a simplification.

A broker without the plugin is the failure mode worth naming, and it was
measured rather than reasoned — three earlier drafts of this paragraph each
described it differently and all three were wrong. What actually happens:

| | |
|---|---|
| Bus start | **Clean.** The connection is made, the endpoints declare, readiness reports ready |
| First `.Schedule(…)` | `exchange.declare` fails with `precondition_failed: unknown exchange type 'x-delayed-message'` |
| After that | MassTransit **retries the topology indefinitely**, so the scheduling call never returns and the saga's transition never completes. The broker logs a channel error every few seconds; the service logs nothing and stays healthy |

So the message is neither delivered nor rejected, and the order waits for a
timeout that cannot arrive — §9.6's stuck order, produced by infrastructure
rather than by a missing transition. **Nothing on the service side ever says
so**, which is why §14.1's healthcheck asserts the plugin is enabled as well as
that the broker is running: a stock broker is *healthy* and wrong, and the only
evidence is in a log belonging to something else.

**This scheduler cannot cancel, and that is the cost this ADR most understated.**
MassTransit 8.5.3's `DelayedScheduleMessageProvider.CancelScheduledSend` returns
`Task.CompletedTask` on both overloads — checked against the tagged source — so
once the broker holds a delayed message nothing recalls it. Every `Unschedule`
in §9.6's machine is a no-op here, and **every order keeps all of its timeouts
until they fire**: five minutes, fifteen minutes and ten minutes on the
ordinary path, and three days on every order that ships. The token-id columns
are written and never read back.

Correctness survives it, and by construction rather than by luck — a timeout
arriving in a state that does not handle it is ignored, and one arriving after
the saga finalised is discarded. Both were measured; the first has a test
(§12.5). What does not survive is the volume argument: the plugin keeps its
delayed messages in Mnesia, per node and unreplicated, its own guidance warns
against large numbers of long delays, and this decision guarantees one
undeliverable delayed message **per wait the order enters** rather than the few
a cancelling scheduler would leave — four on an order that ships.

**That number is not fixed by this decision and has already moved once.** It
was three until §9.6 gained `AwaitingConfirmation`
([#126](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/126)),
which is worth recording here because the *volume* is this ADR's stated
supersession trigger: a state added to the machine raises the standing Mnesia
population by one message per order, and nothing in the state machine's own
review would surface that. **A cost that grows with a decision taken somewhere
else is one to state as a rule rather than as a total**, which is why the rule
is written above and the four is an illustration of it.

**That is the trigger to supersede**, and the replacement is the Quartz option
above rather than a new one — Quartz cancels, so the `Unschedule` calls and the
token columns start working the day it lands, with no change to the state
machine, its schedules or the tests. Which is the same property that let the
choice be made on cost.

## ADR-022 — The canary is a second release, weighted by replicas

**Decision.** [§15.5](15-cicd-deployment.md)'s canary is a **second Helm release
of the same chart** — `<workload>-canary`, `canary.enabled=true` — whose pods
carry the same `app.kubernetes.io/name` as the stable release's and are
therefore selected by the same Service. Traffic splits because kube-proxy
spreads connections across a Service's endpoints, so the share the new version
serves is `canary / (stable + canary)`. No service mesh, no Argo Rollouts, no
Flagger, and no ingress-controller canary annotation.

Two things follow and both are load-bearing. **The weight is quantised**, so
§15.5's ladder is a set of ceilings rather than of targets to land on:
`deploy/canary/canary.py` computes the largest canary that stays within the
requested weight and **refuses** where even one pod overshoots, naming the
stable replica count that would satisfy it. And **the stable release is never
modified** — not its image, not its replica count beyond the one scale-up the
first rung needs — so a rollback is `helm uninstall` of the canary and costs
the canary's own pods and nothing else.

The two tracks are told apart in the telemetry by a `deployment.track` resource
attribute, supplied through `OTEL_RESOURCE_ATTRIBUTES` from the chart.

> **A resource attribute is not a metric label, and the collector is what
> bridges them.** Under the standard OTLP-to-Prometheus mapping only
> `service.name`, `service.namespace` and `service.instance.id` become labels
> on each series; everything else lands in `target_info`. So a query filtering
> `deployment_track="canary"` matches nothing unless the collector copies the
> attribute onto the datapoint — and matching nothing is read here as an absent
> series, which rolls back. **Every rung, every time, on a canary behaving
> perfectly.** [§14.1](14-local-development.md)'s collector does it with a
> `transform` processor over one attribute; **the deployed collector must do
> the same**, and that is a requirement on an environment this repository does
> not contain rather than something its gates can check.

**Why.** §15.5 specifies the behaviour and no chapter had chosen a mechanism,
so building the rollout was what forced the choice. Three were live.

**An ingress-controller canary annotation is disqualified by this platform's
topology, not by taste.** It splits traffic at the Ingress, and this platform
has exactly one — the gateway's ([§10.1](10-api-gateway.md)). Everything behind
it is reached by Service name from YARP's route file and from `PricingHop.cs`,
both of which hold those names as literals on the stated grounds that "the host
is the Kubernetes Service name" ([§10.2](10-api-gateway.md),
[§9.7](09-messaging.md)). So an Ingress-level weight can canary the edge and
**cannot canary Catalog or Ordering at all** — the split would happen upstream
of the hop that chooses them. A mechanism that works for one deployable out of
four is not a platform's canary.

**A mesh or a rollout controller is the better answer and is not this one.**
Argo Rollouts or Flagger would give exact weights, an analysis loop and a
`Rollout` CRD, and Linkerd or Istio would give the traffic split without the
replica arithmetic. Each is a cluster-wide component with its own upgrade
cycle, its own failure modes and its own vocabulary, added to a platform whose
entire deploy surface is `helm upgrade` and whose charts a shell script
verifies by rendering them. It is also a component that would have to be
present before any of this could be tested at all, and **no cluster exists** —
so adopting one would mean shipping a dependency on faith and a rollout nobody
could read. The replica-weighted version needs nothing that is not already
here.

What it costs is the 5% rung. With [§15.3](15-cicd-deployment.md)'s
`replicaCount: 3`, one canary pod already serves 25% — five times what §15.5's
first step asks for — so 5% requires **19 stable replicas**, and the rollout
scales up to that before anything rolls. The chart's own
`autoscaling.maxReplicas` is 20 on the three service charts, which is exactly
19 plus one canary — on those, the smallest configuration in which 5% is
expressible is the largest the chart allows, and neither number was chosen with
the other in mind. **It is not a platform-wide coincidence**: the gateway's
ceiling is 30, because every external request passes through it, so there the
19 is what the weight costs and nothing more. The rollout's scale-up fits under
either, and the gateway's autoscaler can still climb above the count the step
was planned against — which is a residual of raising a floor rather than
pinning a replica count.

**Consequences.** The Deployment's `matchLabels` gains
`app.kubernetes.io/track`, and **that field is immutable** — so this is a
breaking change to any installed release, which has to be deleted and
recreated. It costs nothing today because nothing has ever installed these
charts, and it would cost a downtime window if taken later. The Service's
selector is deliberately unchanged: a Service that selected only `stable` would
route the canary nothing, which is the same failure as having no canary.

The canary release renders no Service, Ingress, HorizontalPodAutoscaler or
PodDisruptionBudget. Those carry fixed names the stable release owns, and Helm
refuses to touch another release's objects (§15.3) — so the suppression is what
makes the install possible rather than a tidying decision. Autoscaling is off
on the canary for a second reason as well: the served weight *is* the replica
ratio, so an autoscaler would move the blast radius underneath the analysis
judging it.

§15.5's requirement that "every migration must be backward compatible with the
previous release" becomes sharper rather than softer. The canary release runs
[§7.4](07-persistence.md)'s migration hook, because it is the first thing
carrying the new image; a rollback then removes the pods and leaves the schema
migrated. That is exactly the case §15.5 calls unrecoverable if the migration
was not backward compatible, and the cheap rollback this decision buys is worth
nothing against an incompatible one.

**The weight is a ceiling under ordinary operation, and a voluntary disruption
can exceed it.** The PodDisruptionBudget is the stable release's and its
selector matches both tracks, which is right — the pods serve one Service — but
it constrains the *total*. At the 5% rung that is 19 stable and one canary
against a `minAvailable` well below twenty, so a node drain during a dwell can
evict stable pods and leave the canary serving far more than the rung asked
for.

Two things bound what that costs, and neither makes it disappear. **The verdict
is measured rather than assumed**: `analyse` reads both tracks' real error rate
and p99, so an exceeded weight means more exposure for one dwell, not a wrong
decision about the release. And the disruption is voluntary, so it is somebody
draining a node rather than something the rollout does.

The fix — a temporary stable-track budget, created for the ladder and removed
with the rest — is **deliberately not taken here**, and the reason is the shape
of this workflow rather than the size of the change. It is a fourth object for
a cleanup path that has already had three defects found in it, added to a
rollout no one has run; guarding a voluntary-disruption edge by enlarging the
surface that must be undone on every failure is the wrong trade until there is
a cluster to observe either behaviour on. Recorded rather than fixed, on the
terms this ADR already uses for the connection-spreading premise below.

**Not verified against a cluster.** `deploy/canary/canary.py` has a suite and
`deploy/helm/smoke.sh` renders the canary track and asserts what comes out, but
both stop at the manifest. Whether kube-proxy's spread actually approximates
the replica ratio under real connection patterns — keep-alive, HTTP/2
multiplexing to the gRPC listener, a client that opens one connection and holds
it — is **not** established here, and long-lived connections are the known way
this mechanism under-delivers a weight. It is named as owed rather than
implied.

## ADR-023 — The consumer-driven contract is a linked file, not Pact

**Decision.** [Appendix C](appendix-c-delivery-plan.md)'s PR-26 is delivered as
one C# source file — `tests/Web.Bff.TestSupport/PricingContract.cs` — written by
the consumer, compiled into the consumer's suite, and **linked** into the
provider's exactly as `pricing.proto` is linked into `Web.Bff`. **Pact is not
adopted**: no package, no broker, no plugin, and no row in
[Appendix B](appendix-b-licences.md).

It covers one relationship, which is the only contentious one this platform has:
[§9.7](09-messaging.md)'s single synchronous hop, `Web.Bff → Catalog`. The file
holds six interactions and the consumer's per-reply tolerance;
`Web.Bff.Tests` drives each through the BFF's own screen against the stub, and
`Catalog.Api.Tests` verifies each against the real provider over
`ServiceFixture`.

**Why.** Three things, and the first alone is decisive.

**Pact's .NET binding cannot express gRPC.** PactNet 5.0.1 ships HTTP and
message pacts. Protobuf and gRPC are a *plugin* —
`pactflow/pact-protobuf-plugin` — and the .NET binding for the plugin
framework is `PactNet.Extensions.Grpc`,
pull request 548 against `pact-foundation/pact-net`, opened on 4 September 2025
and **still open**. So the mechanism Appendix C named cannot reach the
relationship Appendix C made it conditional on.

**The relationships Pact *could* express are not contentious.** The async
contracts travel as a shared assembly that both ends compile
([§4.3](04-solution-structure.md)) and [§12.6](12-test-strategy.md) already
round-trips every one of them through the bus serialiser; the gateway is a
reverse proxy with no semantic contract of its own
([§10.1](10-api-gateway.md)); and the BFF's own HTTP API has no consumer inside
this repository. Adopting Pact for those would be adopting it where it has
nothing to catch.

**The route that does exist costs more than it buys.** Driving the plugin out of
band means `pact_verifier_cli` plus a platform-specific plugin binary installed
into `~/.pact/plugins`. Neither is a NuGet package, so
`Directory.Packages.props` cannot pin them and the licence gate — which reads
that file and Appendix B as text, ahead of the build
([§15.1](15-cicd-deployment.md)) — would never see them. Pact's own
documentation also rules out `WebApplicationFactory` for provider verification,
because its Rust core makes real TCP calls, so Catalog's suite would need a
second hosting shape as well. All of that to express expectations the consumer
already states in C#.

> **What is taken is Pact's property, and only its machinery is declined.** The
> value of a pact is that **one artefact is authored by the consumer and
> verified against the provider** — not the broker, the wire format or the Rust
> core, which are how that property is shipped across a *repository* boundary.
> This is a monorepo, and the boundary Pact's machinery exists to cross is not
> there. `pricing.proto` already makes the argument one level down: the
> syntactic contract is one file with two generated halves, and this is the
> semantic contract shared the same way. The `.proto` is Catalog's because
> Catalog serves the RPC; this file is Web.Bff's because only a consumer can say
> what it needs.

**Consequences.**

**The contract is compiled rather than parsed, which moves a class of failure
from a run to a build.** An interaction naming a field the generated types do
not have is a compiler error in both suites. A pact file is data, and the same
mistake is a verification failure at best and a silently skipped expectation at
worst.

**It does not cross a repository boundary, and that is the whole of what is
given up.** Extract `Web.Bff` into its own repository and this file becomes
something that has to be published — at which point Pact, or a package, is the
answer after all. The decision is reversible and its trigger is nameable, which
is the most that can be asked of one taken against a plugin that may merge next
month.

**Only one relationship is covered**, and no gate says a second one is owed. The
platform has exactly one synchronous hop today by §9.7's design; if a second
appears, whether it earns a contract is the same conditional judgement Appendix
C already applies here.

**Both edges of the ceiling are pinned deliberately.** `GetPricesValidator`'s
`MaxProductIds` changing in *either* direction fails verification — one
interaction requires a basket at the ceiling to be served, the next requires one
past it to be refused. A provider free to change a number its consumer wrote
down has a contract nobody is holding; Pact pins an interaction the same way.

**The linked file is a build-time path into another suite's tree**, which is the
`.proto`'s cost paid a second time. It is cheaper here: no Dockerfile builds a
test project, so unlike a `ProjectReference` or a linked `.proto` there is no
`COPY` line to keep in step with it. What it does reach is `tools/new-service`,
which drops both the link and the verification suite — a contract copied to a
service no consumer calls is an expectation nobody holds.

> **The relationship was contentious, and it was measured rather than
> asserted.** `CheckoutEndpoints` compares a reply's currency to the request's
> with `OrdinalIgnoreCase`; `StubCatalog` echoed the *request's* spelling, so
> the comparison had never once been given two spellings to reconcile. Tightened
> to `Ordinal`, all 62 of `Web.Bff.Tests`' pre-PR tests that need no container
> still passed — while production would answer 500 on every lower-case currency
> a customer typed, because Catalog projects its own upper-cased column. **62 is
> the fast half and not the suite**: the four tests that want a Keycloak were
> not in the run, and nothing here claims they were. Three more divergences
> sat beside it: the stub filtered currency case-sensitively where Catalog does
> not, formatted amounts at the test's own scale rather than the column's
> `decimal(19,4)`, and enforced no ceiling at all. **A hand-written stub is a
> second, unverified specification**, and every one of those was it drifting.

## ADR-024 — A release answers for the order, not for the reservation

**Decision.** `ReleaseStock` is idempotent over **ordering** as well as over
repetition, and Inventory owes two guarantees for it:

1. **It always publishes `StockReleased`.** The event reports the command's
   postcondition — no stock is held for this order — and not a state change, so
   a release that finds nothing to release publishes exactly as one that frees a
   reservation does.
2. **A release for an order whose `ReserveStock` has not arrived is
   remembered**, and the `ReserveStock` that follows it is refused rather than
   creating a reservation nobody is waiting for. **The refusal answers with
   `StockReleased`**, because that is what it establishes — no stock is held
   for this order — which is the same postcondition guarantee 1 reports and
   needs no new member in the vocabulary.

> **`StockReservationFailed` is the obvious answer and cannot carry it.** That
> event means an out-of-stock decision and requires `UnavailableProductIds`
> ([§9.1](09-messaging.md)); a reserve refused because the order was already
> released has **no** unavailable products, so the producer would have no
> truthful payload and every consumer would read a stock shortage that did not
> happen. Answering with `StockReleased` follows from guarantee 1 rather than
> being a second decision: once the event reports a postcondition instead of a
> state change, the refused reserve and the no-op release are reporting the
> same fact. The alternative — a new event, or a `Reason` on
> `StockReservationFailed` — is a [§9.2](09-messaging.md) contract addition
> this ADR does not need.

Both are stated in [§3.2](03-bounded-contexts.md) beside Inventory's row. They
are commitments on a service that does not exist yet, which is the cheapest
moment to take them.

**Why.** [§9.4](09-messaging.md) orders nothing between two deliveries, and
[§9.6](09-messaging.md)'s saga sends `ReleaseStock` from every compensating
transition it has, without ever knowing whether the `ReserveStock` it undoes has
been handled. **The count is deliberately not given**: it read "four" here for
as long as the machine had five, because three *states* send a release and four
absorb an early one, and a third figure between them is exactly the kind nothing
recomputes.
Nothing in the sender can establish that, so the guarantee has to be the
receiver's.

**A conditional acknowledgement pages a human on the ordinary case.**
`Compensating`'s **stock half** settles exactly two ways: `StockReleased`, and
a ten-minute `ReleaseTimeout` that raises a `stock_not_released` review. Under
the other reading — a release of nothing has nothing to report — every no-op
release leaves through the timeout.

> **This paragraph said "`Compensating` has exactly two exits" and the state
> outgrew it.** [#124](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/124)
> made that state a join: it can also be waiting on a payment verdict, so
> `Finalize` is conditional and a stock answer no longer ends the instance by
> itself. Nothing in this ADR turns on that. What the argument needs is that
> the stock half has two settlements and that one of them escalates, which is
> unchanged — the wider claim was a convenience the sentence did not require,
> and it is the kind that goes stale one state machine change later.

`StockReservationFailed` reaches `Compensating` having *proved* the reservation
was never taken, so that is not a corner case but a routine one, and the review
row it raises names a stranded reservation that never existed. A contract whose
normal path escalates to on-call is the wrong contract.

**The second guarantee is the only thing that closes the stranding.** With the
first alone, a release handled before its reserve is a no-op that publishes,
the saga finalises on it, and the `StockReserved` that follows correlates to no
instance and is discarded — a reservation held for an order that is cancelled,
with nothing raised anywhere
([#125](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/125)).
The saga cannot close that from its side: by the time the late `StockReserved`
exists the instance is gone, so any transition written for it is a transition
nothing can reach. The tombstone moves the reconciliation to the only place
that still has both facts.

> **The alternative was a second `ReleaseStock` from `Compensating`, and it is
> unreachable rather than merely weaker.** Sending one on `StockReserved`
> was the cheap fix while the first guarantee was unstated; with it stated, the
> no-op release has already answered and finalised the instance, so the branch
> that would send the second release is never entered. A fix whose precondition
> is the defect it shares a release with is not a fix.

**Consequences.**

**Inventory carries a tombstone, and it is not free.** A release for an unknown
order is a row that must persist long enough to meet the `ReserveStock` it is
waiting for, and be reaped afterwards. That is the same retention question
[§9.5](09-messaging.md)'s inbox already answers for itself, and Inventory's PR
inherits it rather than inventing it.

> **The bound is the order's lifetime, not a figure off the retry ladder**, and
> quoting the ladder is how this paragraph first got it wrong. It cited 635
> seconds as though that were the far end; 635 is the *seventh* failure's
> cumulative wait, `OutboxDispatcher.MaxAttempts` is **10**, and the eighth and
> ninth land near 1,275 and 2,555 seconds — some three quarters of an hour
> before the row may be reaped, with broker backlog on top of that and no bound
> of its own. A tombstone reaped on the ladder's midpoint lets a late
> `ReserveStock` recreate exactly the reservation this ADR exists to prevent,
> which makes the retention a correctness property rather than housekeeping.
> **A horizon derived from one term of a ladder is a horizon that expires
> mid-ladder.**

**The saga may absorb a `StockReleased` that overtakes its own cancellation.**
Inventory consumes `OrderCancelled` directly ([§3.2](03-bounded-contexts.md)),
so one publication starts two races to the saga's queue. Absorbing the early
arrival used to lose it — the saga would then wait out a release it had already
discarded — and under the first guarantee it does not, because the saga's own
`ReleaseStock` is answered whatever Inventory did with the event. `Ignore` is
sound *because of this ADR* and not on its own, which is stated at each of the
three sites that send one.

> **The fourth site does not rest on this ADR, and that is the interesting
> half.** `Confirmed` absorbs an early release too, and deliberately sends no
> `ReleaseStock` — a reservation being picked is not one Inventory can be told
> to drop on a saga's word. So there is no exit for the discarded copy to have
> come from, and nothing there depends on the command being answered. Writing
> it out is worth doing anyway, because the arrival is legitimate and an
> unwritten one faults; the argument for it is simply a different argument, and
> collapsing the four into one reason is what an earlier revision of this
> paragraph did.

> **Whether the event should reach Inventory at all is
> [ADR-029](#adr-029--inventory-releases-on-the-cancellation-not-on-the-sagas-word)'s
> question rather than this one's, and it keeps it.** This ADR takes the direct
> subscription as given and makes the early arrival harmless; that one asks
> whether to delete it and declines, because a cancellation that finds no
> instance to send a `ReleaseStock` would otherwise release nothing, and
> because the early arrival is the only evidence a cancellation gives the saga.
> So the absorptions this ADR names, here and under *Nothing enforces it*
> below, have since stopped being an `Ignore`: each records the arrival on the
> instance, and forward transitions are guarded on what it recorded (§9.6).
> What this ADR establishes — that the discarded copy costs the saga nothing —
> is unchanged; what the copy is *for* is not.

**A `stock_not_released` review now means what it says.** It is raised only when
a release genuinely never completed, so
[`order-review.md`](../runbooks/order-review.md)'s procedure no longer has to
open with "check whether there was ever a reservation".

**Nothing enforces it until Inventory is built.** No gate can hold an unwritten
service to a contract, and this repository's own rule is that a list of things
known to be missing needs something asserting they are still missing. What
stands in for it here is that every place the machine leans on it says so at
the line: `AwaitingStock`, `AwaitingPayment` and `AwaitingConfirmation`'s
`When(StockReleased)`, the cancellation branches those three states absorb
for, and `Compensating`'s `Ignore(StockReserved)` and
`Ignore(StockReservationFailed)`. So an Inventory built to a different rule
contradicts a paragraph rather than failing silently. **The sites are named
rather than counted** for the reason the Why section declines a count — a
figure over a set the next branch can add to is one nothing recomputes.

> **The first three were spelled `Ignore(StockReleased)` until #143, and the
> spelling is updated here rather than left standing.** An ADR is superseded
> and never rewritten, and nothing above has been: the decision, its two
> guarantees and every argument for them are untouched. What changed is an
> **index** — this paragraph exists so an Inventory implementer can find the
> lines that lean on the ADR, and a name that greps to this file and not to
> the machine defeats the only job it has. The callout above records that
> those absorptions stopped being an `Ignore`; this is the same fact where
> somebody would look it up.

## ADR-025 — A saga state that waits on two services finalises on neither alone

**Decision.** Where a state can be waiting on more than one participant, what
is outstanding is recorded **on the instance** and the state finalises only
once every obligation has been discharged. A state name may carry one such
fact; it cannot carry the rest.

Three rules follow, and [§9.6](09-messaging.md)'s `Compensating` is the worked
example:

1. **The obligation is recorded where it is incurred**, not inferred later.
   `PaymentVerdictOutstanding` is set in the activity that sends
   `AuthorisePayment`, so it commits with the transition that creates the
   debt.
2. **A timeout ends the wait, not the obligation.** A participant that has not
   answered has not answered *yet*, so a timeout is a bound on how long the
   instance is held and never a substitute for the answer. It is the arrival
   that discharges the obligation; the timeout only stops asking.
3. **Every exit asks about the other halves.** Either answer may land first,
   so `Finalize` is conditional on the join rather than attached to whichever
   transition was written first.

**Why.** `Compensating` is reached from `AwaitingPayment` with
`AuthorisePayment` already sent and unanswered, so Inventory and Payments both
owe it an answer and [§9.4](09-messaging.md) orders nothing between them. Both
exits finalised on the stock half alone, so whenever Inventory answered first —
the **expected** interleaving, since a prompt warehouse and a slow PSP is the
ordinary shape rather than the degenerate one — `SetCompletedWhenFinalized`
deleted the instance and the authorisation still in flight correlated to
nothing. It was consumed cleanly: no transition, no fault, and no
`payment_authorised_during_compensation` row, which is the escalation §9.6
provides for precisely that case. The money moved and nobody was told
([#124](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/124)).

**The state could not answer the question and that is the general fault.**
`Compensating` is entered five ways and whether a verdict is owed differs by
route — nothing from `AwaitingStock`, already answered from
`AwaitingConfirmation`, and from `AwaitingPayment` it depends on whether a
decline, a timeout or a cancellation brought it there. A single state name
compresses all five into one, so the machine had thrown away the fact its exit
needed before the exit ran.

> **The alternative was a sixth state and it buys nothing here.** A
> `Compensated` state meaning "stock settled, still waiting on a verdict"
> would make the same distinction in the state name, at the cost of a new
> state, a new schedule and a new timeout type — and it would have to be
> paired with a seventh for the mirror case, where the verdict lands first and
> the stock half is outstanding. Two booleans and one join express what four
> states would enumerate. The rule is worth stating in that direction: a state
> per *combination* of outstanding obligations is a product, and the instance
> is where a product belongs.

**Consequences.**

**A cancelled order is held for as long as a verdict can still arrive, and
that is bounded rather than open.** The fifteen-minute payment wait armed with
`AuthorisePayment` is deliberately **not** unscheduled by the cancellation
branch, so it runs on into `Compensating` and ends the wait there; the timeout
door re-arms it once, giving one further window. The longest an instance is
held is therefore thirty minutes from `AuthorisePayment`, which is inside
[§13.6](13-observability.md)'s one-hour unfinalised-saga alert with room to
spare. **A join with no bound would have traded a silent loss for a pager**,
which is not a trade worth making.

**The bound raises no review row, and that is a decision.**
[§3.2](03-bounded-contexts.md) has Payments consuming `OrderCancelled`, so an
authorisation abandoned on a cancelled order is what *should* happen. A row on
the timeout would escalate the healthy path — one per cancelled order the PSP
correctly dropped — and the escalation belongs where money actually moved.

**A decline stops being ignorable.** It moves no money and still raises
nothing, but it is an **answer**, so ignoring it held the instance open until
the wait expired for a verdict that had already arrived. The general form is
that a catch-all `Ignore` is only safe for arrivals that carry no information
the machine is waiting for.

**The tail past the bound is answered by a fault rather than by the machine.**
An authorisation landing after the saga has stopped waiting finds no instance,
so §9.6 gives `PaymentAuthorised` `OnMissingInstance(m => m.Fault())` and it
reaches the error queue §13.6 pages on with the message retained. That is
sound for this event and for no other in the machine: Payments produces it, so
unlike `OrderCancelled` — Ordering's own echo — or `StockReleased`, which
ADR-024 has answered for every release including a no-op one, it can never be
a routine arrival at a finalised instance. **Recovering the review row instead
would mean persisting the obligation outside the saga**, which is a bigger
change than this one and is not taken here.

> **The contrast with `OrderCancelled` has since been narrowed, and this
> paragraph is left as written.** It reads that event as Ordering's own echo
> without qualification, which was true of every arrival when this ADR was
> taken and is true of only some of them now:
> [#123](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/123)
> gave the contract an `Origin` field, so §9.6 asks rather than assuming and
> faults for anything it cannot account for. The distinction this ADR draws —
> **provenance, not timing** — is what survived and is what the field makes
> mechanical; only the claim that provenance was unavailable for a
> cancellation has moved. Recorded here rather than edited above, because an
> ADR is superseded and never rewritten.

**The instance carries facts as well as obligations, and the second use is
§9.6's `CancellationObserved`.** An early `StockReleased` absorbed in a state
that sent no release proves a cancellation reached Inventory
([ADR-029](#adr-029--inventory-releases-on-the-cancellation-not-on-the-sagas-word)),
and the saga records that where it observes it and guards its forward
transitions on it. Nothing is outstanding and no exit joins on it, so rule 1 is
the half that generalises: the place a fact a state name cannot carry belongs
is the instance, and only rules 2 and 3 are about waiting.

**Nothing enforces the rule beyond §9.6.** No gate reads a state machine for
states that wait on two participants, and the only one this platform has is
the one above. What stands in for a gate is the structural test that
partitions `Compensating`'s declared next-events, which fails when a branch is
added without being argued — it caught nothing here, because the join was
written deliberately, but it is what a later state waiting on two things would
run into first.

## ADR-026 — Consumer capability is a release ahead of the producer that uses it

**Decision.** Anything a consumer must be able to *recognise* — a new message
type, a new binding on an existing endpoint, a new member of a closed
vocabulary — is deployed everywhere before the release that starts emitting it.
The two go out as two ordinary releases in an order, never as one.

Where the consumer and the producer are in the same build and the queue is
shared, ordering the *deploy* separates nothing, so the change is split across
two *releases* before there is an order to impose: one that declares the
consumer and publishes nothing new, then one that publishes. The alternative
is a cutover with no overlap, which is not a canary and should not be called
one.

**Retirement is the mirror and takes the opposite order.** A consumer removed
before the producer stops emitting leaves the binding live — MassTransit does
not unbind a queue when a consumer is deleted — so every subsequent message is
skipped, permanently rather than for the length of a rollout. Producer
capability retires a release ahead of the consumer, which is [§9.2](09-messaging.md)'s
own deprecation-window rule read in the direction it does not spell out.

`<queue>_skipped` is alerted on ([§13.6](13-observability.md)), which is what
makes this a rule rather than advice.

**Why.** [§9.2](09-messaging.md) said additive changes need no version bump
because "consumers deserialising an unknown field ignore it" — true, and true
only of fields. A deserialiser is built to skip an unknown field. Nothing is
built to skip an unknown *message type*, and a closed vocabulary is a
whitelist by construction, so both fail while looking additive.

**The two failure modes are opposite, and the quiet one is the reason this is
an ADR.** A new binding on a shared queue: the broker hands the message to a
replica whose build declares no consumer for it, MassTransit parks it in
`<queue>_skipped`, nothing throws, and [§13.6](13-observability.md) watched the
*error* queue only. A new vocabulary member: the mapper refuses a code it does
not know and [§9.8](09-messaging.md) deliberately excludes
`ContractMappingException` from retries, so a well-formed escalation from a
newer producer reaches the error queue on the first attempt. One loses the
message in silence and the other pages immediately, and a single rule fixes
both because the root is one — a producer shipped in the same release as the
consumer that understands it.

**The blueprint reasoned about rolling deploys in a dozen places and none of
them was this.** §6.5's live cache key, §7.3's migration race, §8's key rename,
§9.4's `MessageTypeMap` alias, §15.5's backward-compatible migrations — every
one is a **state** compatibility rule, about a schema, a key, a persisted type
name or a persisted payload. Not one is about **routing** or **vocabulary**,
which is exactly why both instances fell through a pattern that otherwise looks
complete. **A gap inside a well-covered pattern is harder to see than a gap in
an uncovered one**, because the neighbouring cases make the topic feel
answered.

**Consequences.**

**A binding change that also starts publishing costs a release, and the canary
cannot absorb it.**
[ADR-022](appendix-a-adrs.md#adr-022--the-canary-is-a-second-release-weighted-by-replicas)
makes the canary a second release of the same chart answering the same
Service, so both tracks consume the same queue for the length of the ladder —
roughly half an hour at the ladder's current dwells. The consumer and producer of a saga event
are in the same build, so ordering the *deploy* separates nothing — for the
length of the ladder the old track is bound to neither. What separates them is
splitting the change across two *releases*, which is the usual answer and a
real cost, paid per binding.

**This is an ordering constraint and not a coordinated deploy**, and the
distinction is the one §9.2 closes on. A lockstep deploy is what this
architecture exists to avoid; two independently shippable releases that must go
out in a known order is what every expand/contract migration already is. If the
two ever cannot be separated, that is a signal the boundary is wrong rather
than a reason to relax the rule.

**Nothing gates the ordering itself, and the alert is what stands in.** No CI
check can know that a consumer is deployed everywhere — that is a fact about
the cluster and not about the branch. What `SkippedQueueDepth` gives is
detection rather than prevention: a violation pages within a minute instead of
being discovered when someone asks where an order went. **A rule whose
enforcement is a page rather than a build failure is weaker and is worth
having**, on the same terms as ADR-024's guarantees, which no gate holds
Inventory to either.

**It also fires on a genuinely missing binding**, which is a different fault
with the same symptom. The runbook separates them by asking whether every
replica is on the same build; the alert cannot, and says so.

**The detection is owed a deployment this repository does not configure, and
saying so is the point.** `rabbitmq_queue_messages` carries a `queue` label
only where the broker runs `rabbitmq_prometheus` with per-object metrics
enabled and something scrapes it. §14.1's image enables the delayed-exchange
and shovel plugins and neither of those; Compose publishes 5672 and 15672 and
not 15692; and §13.7 already states that nothing here deploys Prometheus. So
the rule above is enforceable *by an operator who has wired that up*, and is
advice until then. `ErrorQueueDepth` has carried the same dependency since
PR-24 without anyone writing it down — the difference is that this ADR leans a
**specification rule** on it, which is a heavier claim than an alert makes,
and a claim of enforceability that quietly depends on an unwired signal is the
exact shape of *a registered name is not a live signal*.

## ADR-027 — The order summary stores product ids and resolves the name locally

**Decision.** `ordering.OrderSummaries.Products` holds a JSON array of product
ids and nothing else. Catalog's display facts — the name and the thumbnail —
are projected once per product into a new `ordering.Products` table, and
[§6.6](06-cqrs.md)'s history query resolves them in a second, page-bounded
statement. The `ProductsUpdatedAt` column and the `JSON_MODIFY` patch handler
that versioned it are both removed.

**Why.** The design this replaces delivered its own payload only by accident.
It inserted `name` and `thumb` as empty strings and left them for "a later
`ProductPublished`" to patch in, and ordinarily none arrives: a product must be
published before it can be ordered, because `PlaceOrder` reads
`ordering.ProductPrices` and the same event fills it. So `ProductPublished` is
ordinarily consumed *before* the summary row exists, and a patch scoped to
summaries that already contain the product then touches nothing. The ordering
is the ordinary flow and not a guarantee: `IntegrationEventConsumer` runs the
two handlers sequentially but each commits on its own connection, so an order
placed in the window after the price handler commits and before the patch
handler runs would find its summary row patched. Narrow, and it is why this
reads *in the ordinary flow* rather than *always*. **Every summary carried
empty names in the normal flow** — which is the payload §6.6 exists to deliver,
filed as
[#121](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/121).

The obvious repair — read the names at insert time instead — closes that door
and leaves a second one open. `ProductPriceProjection`'s upsert inserts on its
`NOT MATCHED` branch for `PriceChanged` as well, so a product whose
`ProductPublished` never reached the queue still acquires a price row and is
orderable. §6.6's rebuild callout describes that population, and this change
corrects it in passing: it read *unorderable until somebody republishes it*,
which the `PriceChanged` branch has never made true. An order placed through
that door would carry
an empty name permanently, and the only thing that could ever fix it is the
patch handler this ADR removes.

**Resolving on read fixes both, because it makes the name a fact about a
product rather than a copy inside an order.** A `ProductPublished` that arrives
late — or for the first time, months later — fills every order that ever
referenced that product, retroactively and with no rebuild.

**The load-bearing sentence in the old design was false.** It justified
denormalising the name on the grounds that "joining at read time is not an
option — the products live in Catalog". They do not, once Ordering projects
them: a primary-key lookup against a table in the same database is not the
cross-service join the argument was about. `ordering.ProductPrices` had
already been exactly that local projection of Catalog's data on the write path
since PR-20, on a table PR-18 shipped with its reader and no producer — so the
premise was false before this change was written.

**What that table held is the price, and it is worth being exact about it.**
`ordering.ProductPrices` has never had a `Name` column; the name was copied
once, into every order's JSON, and it is that copy this ADR removes. The
argument is that a local projection of Catalog was already established and
paid for, not that the name was stored twice.

**Consequences.**

A watermark now belongs to exactly one sequence. `ProductsUpdatedAt` versioned
as many `ProductPublished` streams as an order had products, so a delayed
rename of product B, handled after a newer one for product A, was discarded and
B kept a stale name — a residual §6.6 named and could not close without a
fourth JSON member compared inside an `OPENJSON` predicate. One row per product
retires it by construction rather than guarding it.

**The chapter's "one table, no joins" claim is narrowed, and it said so twice.**
The history query is now two statements. The second is one round trip
for the whole page rather than one per row, seeks a primary key, and carries
its ids as a single JSON parameter read through `OPENJSON` — against a patch
handler that scanned every summary in the table on every rename.

**It is deliberately not an expanded `IN` list, and the first draft of this
ADR said it was bounded by the page clamp.** The clamp bounds *rows* at a
hundred and each row admits a hundred items, so the distinct ids reach ten
thousand — and an `IN` list of that size exceeds SQL Server's 2,100-parameter
ceiling and fails the request outright. **A limit one multiplication away from
a stated bound is exactly the kind a reassurance hides**, which is why the
count is written out here rather than deferred to the clamp. What the trade
actually buys is a key lookup per distinct product against a handler that
scanned the whole table on every rename, and it is still the better half of
the exchange.

**The stored payload loses its member names, which removes a hazard rather
than moving it.** `SummaryProduct` was one type doing two jobs, and its three
JSON names were pinned with `[JsonPropertyName]` because the `JSON_MODIFY`
paths spelled them a third time in string literals no compiler reads. A `Guid`
array has no member name to mismatch, so `JsonSerializerOptions.Default`'s
case-sensitive binding — the quiet failure §6.6 spends a callout on — has
nothing left to fail on.

**Nothing in `src/` changes, and that is why this is cheap now and would not
have been later.** `OrderSummaryProjection` is unbuilt and Appendix C carries
no row that builds `OrderSummaries` at all, so there is no shipped code to
correct and no data to migrate. The same correction after that row lands is a
migration, a backfill and a projection rebuild.

**What this does not do is remove the second copy.** `ordering.Products` is
still a projection of Catalog's data into Ordering's database, with its own
staleness and its own rebuild procedure — §6.6's closing trap applies to it
exactly as written. What changes is that there is now one copy on the read path
instead of one per order line that mentions the product.

## ADR-028 — A money-movement command carries no subject

**Decision.** A command that crosses the broker into a decision about whose
money moves carries **no subject identifier**. The service that owns the
decision resolves the subject from its own record, built from an event whose
subject was bound from a principal.

Concretely: `AuthorisePayment` is
`(Guid OrderId, decimal Amount, string Currency)`. Payments consumes
`OrderPlaced` ([§3.2](03-bounded-contexts.md)) and keeps its own record of the
order — **the payer, the total and the currency**, all three of which that
event carries — then resolves the payer from that record when the command
arrives and checks the command's amount and currency against it. `Amount` and
`Currency` stay on the command. Ordering's saga instance drops its `CustomerId`
too, so the value is not available to a later transition that might put it back
on a message.

**Why.** §11.4's subject rule — *a subject identifier is bound from the
principal, never from the request* — excluded the message path, because a
command arriving over the broker has no principal to bind from. That exclusion
was recorded as an open question rather than a decision, and it left
`AuthorisePayment` naming the customer whose instrument Payments would charge
in a field nothing on the receiving side could check
([#63](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/63)).

The rule that closes it is not "bind on the message path" — there is nothing to
bind from — but **re-derive**, and what makes re-derivation available is that
the subject is already written down somewhere else. `OrderPlaced` carries a
`CustomerId` bound from the principal at Ordering's endpoint, so a service that
consumes it holds the same fact from a party that authenticated somebody,
rather than from a sender that merely asserted one.

**That is a statement about the legitimate path and not about the event's
provenance**, and the difference is the residual this ADR closes with: nothing
authenticates an `OrderPlaced`, so the shared broker principal
([#44](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/44))
can forge one and seed a payer of its choosing. What this decision buys is that
the **command** no longer offers a payer-selection field — a narrowing whose
exact reach is argued below. Calling the event-backed value *trustworthy* would
be claiming authenticated provenance the platform does not have until #44 or
event signing exists, and an earlier draft of this paragraph did.

**Not every field is the same question, and the line between them is
instruction versus authority.** `Amount` and `Currency` say *what to do*. The
sender decides them, so they must travel, and Payments may compare them against
its record and refuse a mismatch — a consistency check between two parties who
both have a view. A subject says *on whose behalf*, and that is not the
sender's to state: it is the deciding service's to derive.

**The reason is not that only the subject is uncheckable, and getting that
wrong is instructive.** An earlier draft of this ADR argued exactly that — a
field the receiver can check is a claim, one it cannot check is an assertion —
and the record this decision *itself introduces* refutes it. Payments stores
the payer along with the total, so a supplied `CustomerId` would be as
checkable as the amount; checkability separates none of the three.

What survives is stronger than the argument it replaces. **A transported
authority is a second source for a decision that must have exactly one.** The
check that would catch a mismatched subject is a check somebody has to
remember to perform, and a redundant authority-bearing field is precisely the
one a later code path reads *instead of* deriving — cheaper at the call site,
identical in the happy case, wrong exactly when it matters. Removing the field
removes the possibility rather than guarding against it.

**So a money-movement command carries its instruction and never its
authority**, and that is the form to apply to the next such contract.

**The precedent is one service over, and the closer of the two is §6.4's price
projection.** `PlaceOrder` reads `ordering.ProductPrices` — Catalog's price
events projected into a table Ordering owns, behind an `IProductPriceReader`
documented as *never a remote call* — so a handler needing another service's
fact on the deciding path looks it up locally. Payments' lookup is the same
shape: write path, at the moment of decision, against a record built from
events. [ADR-027](#adr-027--the-order-summary-stores-product-ids-and-resolves-the-name-locally)
is the same mechanism on the **read** path and for product **names**; the two
tables are distinct and `ordering.ProductPrices` has never carried a name.

Only the thing the local copy buys differs: there a synchronous hop avoided,
here an unverifiable assertion removed.

**Consequences.**

**The field is removed from `V1` in place, and [§9.2](09-messaging.md) is what
that has to be reconciled against.** Removing a field is a breaking change and
the standing rule is a new version with both published for a deprecation
window. The exception taken here is the one §9.2 now states: `Payments.V1` has
no consumer — Payments is unbuilt, and no service in the solution consumes
`AuthorisePayment` — so there is nobody for a window to serve.

**"No consumer" and "nothing deserialises it" are not the same claim**, and an
earlier draft of this paragraph made the second, which is false: §12.6's
contract suite round-trips every contract through the bus serialiser, this one
included. A test that asserts a shape is not a consumer bound to it — it moves
with the contract in the same commit, which is exactly what a deprecation
window exists to make unnecessary. The condition §9.2 states is about
*services*, and it is worth keeping the two apart, because the wider claim is
the one a reader can falsify in thirty seconds and would then have no reason to
trust the rest.

**And here the standard remedy would have been actively wrong, which is worth
separating from "unnecessary".** A `V2` alongside `V1` keeps the version
carrying the subject published and consumable for the length of the window.
The whole point of this decision is that the subject must not be on the wire;
dual-publish would re-arm the defect under a rule written to protect consumers
that do not exist. The version bump is not a cost this ADR declined to pay — it
is a step that would have undone the change.

**Payments cannot honour a single command until it has the projection.** The
subscription is a precondition, not an enrichment, and the service's first PR
owes the record before it owes a charge. §3.2's Consumes cell is where that is
now written down. It is also the moment the exception above expires: from that
PR on, `AuthorisePayment` is a live contract and §9.2 binds it with no
exception.

**A command can arrive before the record it resolves against.** §9.4 orders
nothing between two deliveries, so an `AuthorisePayment` can overtake the
`OrderPlaced` it needs — the shape §3.2 already records for `ReleaseStock` and
`ReserveStock`. **A missing record is a wait, not a decline**: Payments must
not publish `PaymentDeclined`, which is a business verdict about a payer it has
not identified.

**The wait needs a mechanism that lasts as long as the wait, which the ordinary
retry envelope does not.** An earlier draft of this ADR said to fault the
command and let retries carry it, and §9.8's command policy is five exponential
in-memory attempts capped at a minute — so a reorder outlasting that reaches
the error queue §13.6 pages on, making an operational fault out of a race this
ADR calls routine, roughly fourteen minutes before the timeout that was
supposed to bound it. **Payments' command endpoint takes delayed
redelivery** — [ADR-021](#adr-021--saga-timeouts-are-scheduled-by-the-broker)'s
delayed exchange is already on this broker — with a window reaching §9.6's
fifteen-minute payment timeout, so an order whose `OrderPlaced` never arrives
compensates on that timeout rather than paging well before it.

**This narrows the broker exposure and does not close it.** One shared
RabbitMQ principal still writes every queue
([#44](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/44)),
so anyone reaching the bus can still send an `AuthorisePayment`. What they can
no longer do with **that command alone** is choose who it charges: a forged
command naming a real order re-triggers that order's own authorisation rather
than redirecting one at a customer of the sender's choosing.

> **The same principal can still select a payer, and an earlier draft of this
> ADR claimed otherwise while documenting the route two paragraphs below.**
> Forging an `OrderPlaced` seeds Payments' record, and a forged
> `AuthorisePayment` after it charges whoever that record names. So this is a
> narrowing, not a closure, and the honest statement of it is that **the
> command alone no longer carries the payer** — not that payer selection is
> gone.
>
> What the narrowing buys is cost and visibility rather than capability. The
> attack is now two messages on two exchanges instead of one, and the added
> one is an **event other services consume**: Ordering's own saga starts on
> `OrderPlaced` (§3.2), so a forged one runs a fulfilment saga for an order the
> write model has no row for, and Notifications sends a customer a message
> about an order they never placed. The single forged command left no such
> trace. What removes the capability is per-service broker identity
> ([#44](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/44))
> or verifiable event provenance; nothing in this ADR does.

> **Nothing in this platform absorbs that duplicate today, and naming a control
> that does not reach it would be worse than naming none.** [§8.5](08-caching-redis.md)'s
> `IdempotencyBehavior` is an Application-pipeline behaviour constrained to
> `IIdempotentCommand` and keyed on a `CommandId`; `AuthorisePayment` has
> neither, and as a `Common.Contracts` message it never enters that pipeline.
> [§9.5](09-messaging.md)'s inbox is the broker-side control, and it keys on
> `(MessageId, Endpoint)` — a forger picks a fresh `MessageId`, so the inbox
> suppresses an accidental redelivery and not a deliberate second send. What
> would absorb it is **Payments treating authorisation as idempotent per
> order against its own `PaymentIntent`**, which is a rule that service's own
> PR owes and no chapter yet states. Until then the control is #44.

**A forged `OrderPlaced` could still seed a false record**, and that is the
same issue rather than a new one: it is a broader compromise, it is visible to
every consumer of that event rather than to none, and per-service broker
identity is what closes both.

**Dropping the saga's column takes two releases, not one.** §15.5 requires
every migration to be backward compatible with the release serving beside it,
and that release's saga writes `ordering.OrderFulfilmentStates.CustomerId` on
every `OrderPlaced`. So this release maps the column as a shadow property with
a conservative default — `NOT NULL DEFAULT '00000000-…'`, the one shape that
survives a roll-forward whose `INSERT` omits it *and* an old build
materialising a non-nullable `Guid` from rows the new build wrote — and the
`DROP COLUMN` is owed to a release where nothing writes it. The empty GUID is
nobody, where any other default would name a real subject that was never that
order's.

**Two releases are enough here and would not be with a live consumer, and the
difference is worth stating because the shorter sequence looks complete.**
§15.5's canary runs both releases at once over the same queues, so the ordinary
ladder — not merely a rollback — lets a new pod create the instance with the
column defaulted and an old pod take the next event for it, read `Guid.Empty`
and send its four-field `AuthorisePayment` naming nobody. That reaches no
decision today for the same reason the in-place contract change is allowed at
all: nothing consumes the command. A platform whose Payments is live needs
**three** releases — stop sending the field, drop the property, drop the
column — which is §7.4's own sequence with its *stop writing the old one* step
performed rather than skipped. Skipping a step is what having no consumer buys,
and it buys nothing once there is one.

**The rule is gated in two halves, and only together do they force a
decision.** `ContractTests` asserts that no command contract declares a member
spelled like a subject — a list of six spellings, and therefore a
**deny-list**, which passes every spelling nobody predicted: `OwnerId` reaches
Payments with that assertion green, measured rather than argued. So a second
test enumerates **every member the judged commands are approved to carry**, and
any name absent from it fails the build.

**The allow-list does not decide whether a new member is a subject; it makes
adding one impossible to do quietly.** The verdict is still a person's, and the
build going red is what puts it in front of them — the scaffold's rule, that a
tool refusing input it has never been shown beats one that guesses, and the fix
this repository already applied to a terminal-state check that listed what it
refused instead of what it accepted. Stated this way because the earlier
wording — *enforced rather than reviewed* — claimed a completeness a
substring list cannot have.

**Defining "command" is the part that had to be got right, because the obvious
definitions fail in opposite directions.** §9.1
states one implication only — commands do not implement `IIntegrationEvent` —
so the converse is not available:

- **Every non-event refuses what the rule allows.** It sweeps in the payload
  records events carry, and an event is *permitted* a subject: `OrderPlaced`
  holds the one this ADR requires it to keep. An event that factored that field
  into its line type would fail a build for doing something legal.
- **Non-events minus the event closure lets one through.** That was the fix for
  the first and it created a worse fault: a payload carried by *both* a command
  and an event became exempt because an event reached it, so a subject inside it
  would travel on the command unjudged — a false negative on the exact path this
  decision exists to close.

**Reachability from a command root settles both.** The judged set is the
commands plus everything they carry transitively, so a shared payload is judged
(a command reaches it) and a purely-event payload is not (none does).
`StockLine` is judged through `ReserveStock`, because a subject one level down
reaches the same decision as a top-level one. The consequence for a shared type
is worth stating: it may not carry a subject at all, because the command side
forbids what the event side permits, and the stricter rule is the direction a
gate must fail in.

Controls sit beside the rule, because an absence-assert cannot fail
informatively on its own: one points the detector at `OrderPlaced` and requires
it to find the `CustomerId` this ADR keeps; one names all seven command roots
§3.2's Accepts columns list, so discovery cannot quietly drop four of them; one
asserts the exemptions really are excluded; and one exercises **every declared
subject spelling** rather than the first, after review found five of the six
unobserved — a control carrying the coverage defect it exists to catch. Its
cases are **generated from the vocabulary**, because the fix's own first
attempt restated the vocabulary as a case list and a spelling added to
everything except that list arrived unobserved all over again.

A gate that quietly stops
covering its surface is this repository's most-repeated failure, and an empty
offender set reads the same whether the rule holds or the detector broke.

**The shared-payload case is pinned with synthetic contracts, because the real
ones cannot express it.** No live payload is carried by both a command and an
event, so every assertion over the contract assembly stays green under the
rejected definition — the defect could be measured by hand and not held closed.
Four probe types in the test assembly supply the shape and are driven through
the same closure, which is why that closure takes its type universe as an
argument rather than reading a field.

## ADR-029 — Inventory releases on the cancellation, not on the saga's word

**Decision.** Inventory goes on consuming `OrderCancelled` directly, as
[§3.2](03-bounded-contexts.md) has always given it, and releases the stock it
holds for that order **whatever state §9.6's saga is in**. `ReleaseStock` does
not become the only trigger. The saga's `Confirmed` branch continues to send no
release, and that restraint is now documented for what it is: it withholds a
**second** instruction, not the first.

**Why.** [#141](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/141)
asked whether Inventory should decline to release for an order it knows reached
`Confirmed`, and named three sketches. Making `ReleaseStock` the only trigger
was called the cleanest and largest. It is neither, once two things beside it
are read together.

**The second producer is a safety net rather than a duplication.** With
`ReleaseStock` as the only trigger, a customer's stock comes back only if a
saga instance exists to send the command. §9.6 finalises down several branches,
some of them before any despatch has been arranged, and a cancellation arriving
after any of them would then release nothing at all — the reservation held
until a person noticed. Removing the direct subscription buys tidiness in the
diagram and pays for it with a single point of failure on the one obligation
the customer can see.

**And it is the only evidence a cancellation gives the saga.**
[#143](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/143)
turns on a `StockReleased` arriving in a state that sent no release: that
arrival *proves* a cancellation reached Inventory, and it is what four states
now record on the instance — `CancellationObserved`, which is
[ADR-025](#adr-025--a-saga-state-that-waits-on-two-services-finalises-on-neither-alone)'s
first rule applied to a fact rather than to an obligation — so a forward step
can be withheld. It exists only
because Inventory consumes the event directly. So the two issues cannot be
settled independently in the direction they were filed — taking #141's option 2
would delete the mechanism #143's fix is built on, and leave the saga with no
way to know a cancellation is in flight until its own copy lands.

> **Two open questions were being weighed independently and one decides the
> other.** #141 ranked its sketches by cost while #143 was still open beside
> it; answering #143 removes the cheapest of them from the table entirely. The
> same shape closed #125, and it is worth the second recording: before ranking
> options by cost, ask whether each survives its neighbours being settled.

**What the restraint is actually for, then.** Reaching `Confirmed` means a
despatch may be moving, and a reservation being picked is not one Inventory can
safely be told to drop on a state machine's word. That argument is about the
*command*, and it survives: the saga does not issue one. It was never an
argument about the reservation surviving, and three documents implied it was
until the PR that wrote `Confirmed`'s fourth absorption corrected them.

**Consequences.**

- **A picked parcel's reservation is released, and no mechanism prevents it.**
  §9.6 raises `cancelled_after_confirmation` and an operator reinstates the
  reservation by hand if the parcel is still in the warehouse;
  `docs/runbooks/order-review.md` step 2 owns that procedure and already says
  so. This ADR does not close that gap — it records that the gap is Inventory's
  to close when Inventory exists, and that closing it needs Inventory to know
  the order was confirmed.
- **The closing move is available and is not taken here.** #141's sketch 1 —
  Inventory declining to release for an order it has seen confirmed — needs
  `OrderConfirmed` in Inventory's Consumes column, which §3.2 does not give it
  and no chapter asks for. Adding a subscription to a service with no code, for
  a case no runbook has yet worked, is a decision better taken by whoever builds
  it against a real picking process.
- **One cancellation keeps two independent routes to `StockReleased`**, and
  ADR-024's guarantees are what make that safe rather than racy. This decision
  depends on that one; neither is correct alone.
- **Nothing enforces any of it until Inventory is built.** Like ADR-024, this
  is a commitment on a service that does not exist, which is the cheapest moment
  to take it and the reason it is written down rather than assumed.

## ADR-030 — Authorization is deny by default, in the building block

**Decision.** `AddCommonWebDefaults` (§13.2) sets a fallback authorization
policy requiring an authenticated user, so an endpoint carrying no
authorization metadata is refused rather than admitted. A public path says
`AllowAnonymous()` — or, at the edge, names YARP's reserved `anonymous`
([§10.2](10-api-gateway.md)). Everything already public says so: the three
probes `MapCommonHealthEndpoints` maps in every host, and Catalog's product
listing.
Every host composing that call inherits the policy, including the gateway.

**Why.** [#41](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/41)
observed that `UseAuthorization` with no fallback evaluates **nothing** on an
endpoint with no policy metadata, so authorization was opt-in: it applied
exactly where somebody had written `RequireAuthorization()`. Every endpoint
group in the solution had in fact written it, which is what made the gap easy
to miss — the defect this closes is not a live exposure but the *absence of a
mechanism*. A new `*Endpoints` class that omits the line produces no compiler
error, no `ValidateOnBuild` failure, no startup warning and no failing test,
and §4.5's scaffold copies whatever the composition root does into every
service that follows.

**A fallback is the only form of this rule that cannot be forgotten.** The
alternative on offer was §11.4's proposed `EndpointDataSource` test, asserting
that every non-health endpoint carries a policy. That is a real check and it is
weaker in the way that matters: it lives in one service's test project, so a
service scaffolded from it inherits the test and a host written by hand does
not, and it fails at test time rather than refusing the request. The two are
not alternatives so much as different distances from the request — and the
fallback is the one the request itself passes through.

> **The rule reaches endpoints nobody wrote, and that is where its cost is.**
> Routing's 405 short-circuit endpoint carries no authorization metadata, so an
> anonymous request using the wrong method on a real path is now challenged
> before the method is considered. An authenticated one still receives 405.
> That is a deliberate consequence rather than a side effect: 405 tells an
> unauthenticated caller which methods a path accepts, and §11.2's posture is
> that such a caller learns nothing. `ProxiedRouteTests` pins both halves,
> because the 401 alone would go on passing if the route stopped existing.

**The third is no endpoint at all.** A fallback policy is evaluated when
routing matched *nothing*, so an anonymous request for a path this service does
not have is a 401 rather than a 404 — measured, not deduced. Taken on the same
terms: a caller with no credentials learns nothing about which paths exist, and
a 404 is exactly the disclosure §11.4's ownership rule already refuses one
resource at a time. An authenticated caller still gets the 404, and both halves
are asserted, because the 401 alone would pass against a host that had stopped
routing.

**The second endpoint nobody wrote is the OpenAPI document.** `MapOpenApi()`
carries no metadata either, so `/openapi/v1.json` now requires a caller. Taken
on the same terms: the document enumerates every route and every schema the
service has, it is not routed by the gateway, and §11.2 assumes the network
inside the cluster is hostile. `HostSmokeTests` asserts the challenge and a
second test asserts the document still generates for a caller who gets through
— a 401 on its own is satisfied by a host that has stopped serving the
document at all.

**Consequences.**

- **A route file that names no policy is no longer a public route**, so
  [§10.2](10-api-gateway.md)'s `catalog-public` names `anonymous` explicitly.
  The chapter's old rule — that naming no policy was the only correct way to
  declare a route public — is reversed, and both the file and the chapter say
  so.
- **The named `authenticated` policy stays**, and is now belt-and-braces on
  every group that names it. It is kept because the gateway's route file names
  it, and a route file that says what it means is worth a line.
- **The failure direction is a 401 nobody expected rather than an endpoint
  nobody protected**, which is the trade this takes. A public path that loses
  its `AllowAnonymous` fails loudly at the first anonymous request instead of
  silently at the first attacker.
- **`Common.Web` gains no way to be selectively opted out of.** A host that
  wants a different fallback sets one after the call; nothing here takes a
  parameter, because a parameter is a way to turn the rule off.

## ADR-031 — The service owns `nosniff`; the Ingress owns HSTS

**Decision.** `Common.Web` ships `UseSecurityHeaders`
([§10.6](10-api-gateway.md)), called outermost by all four hosts, and it sets
one header: `X-Content-Type-Options: nosniff`. `Strict-Transport-Security`,
`X-Frame-Options` and `Content-Security-Policy` are deliberately not set by any
host.

**Why.** [#39](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/39)
asked, among other things, for an explicit owner for the response security
headers. There was none — the blueprint did not discuss them as done, as
deferred, or as out of scope, which is the state a decision is meant to end.

**The edge is not in front of the other three hosts.** Putting the header on
the gateway is the obvious placement and it has a hole: §11.2 assumes the
network inside the cluster is hostile, and three of the four hosts answer
requests that never traverse the edge. A building block every host composes is
the only placement with no such gap, and it is also what §4.5's scaffold copies
into every service that follows.

**The omissions are the argued half.** HSTS belongs to the Ingress, which is
the only component in this platform that terminates TLS
([§10.1](10-api-gateway.md), [§15.3](15-cicd-deployment.md)); a host behind it
sees plain HTTP and would be asserting something it cannot observe. Framing and
script policies govern how a browser renders a *document*, and no host here
serves one: the API responses are `application/json` or
`application/problem+json`, and §13.5's probes are `text/plain`. A policy that
protects nothing is a policy somebody later has to reason about. `nosniff` is
the one that is not about rendering: it stops a browser reclassifying a JSON
response, including one whose body carries a value a caller supplied, as
something it will execute.

> **Where the header is written from is the whole of its correctness.**
> `UseExceptionHandler` clears the response before it writes
> [§10.5](10-api-gateway.md)'s problem body, so a header assigned on the way in
> is gone from exactly the 500 on which a caller-supplied value is most likely
> to be reflected. The middleware registers an `OnStarting` callback instead,
> which fires after that clear. A test drives the 500 path, because the 200
> path passes either way.

**Consequences.**

- **The list is one entry, and a second needs this ADR amended.** A storefront
  serving HTML from this platform would change the framing and script argument
  above; nothing here anticipates that, deliberately.
- **The Ingress is now load-bearing for a header no chart sets.** HSTS is owed
  by `deploy/helm/`'s ingress annotations and is not there today. Named rather
  than closed: the decision is who owns it, and the owner is not the code in
  this repository.
- **Every host pays one delegate per request.** The callback is static and
  takes the response as state, so the middleware allocates nothing per request
  beyond what the pipeline already does.

---

## ADR-032 — The saga's outbox is MassTransit's, in the saga's own transaction

**Decision.** [§9.6](09-messaging.md)'s `ordering-fulfilment-saga` endpoint
takes MassTransit's Entity Framework outbox —
`AddEntityFrameworkOutbox<OrderingDbContext>` with
`UseEntityFrameworkOutbox<OrderingDbContext>(context)` on the endpoint — in
place of `UseInMemoryOutbox`. The bus-level call sets
`IsolationLevel = IsolationLevel.Serializable`, which is part of the mechanism
rather than a tuning knob: the outbox opens the consume transaction and the
saga repository joins it, so the level in force is this one and no longer the
`Serializable` the repository used to open for itself, and MassTransit's
default here is `RepeatableRead`. It brings three tables into the `ordering`
schema (`InboxState`, `OutboxState`, `OutboxMessage`), which is a **second
outbox table set** and therefore an exception to §9.3's prohibition on one. The
exception is this endpoint and no other; the platform's other three receive
endpoints keep the in-memory outbox, and every application-level integration
event still goes through §9.4's `ordering.OutboxMessages` and its dispatcher.

**Why.** [#128](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/128)
is a dual write. `EntityFrameworkRepository` persists the saga instance and
`UseInMemoryOutbox` buffers the messages that transition sends, and the buffer
flushes **after** the repository has committed. A crash in that window leaves
the instance advanced and its commands never sent. Concretely, for a
`StockReserved` arriving in `AwaitingStock`: the instance moves to
`AwaitingPayment` and commits, `AuthorisePayment` and the `PaymentTimeout`
schedule are both still in the buffer, the process dies — and the order sits
in `AwaitingPayment` with stock reserved, no authorisation requested and **no
timeout to rescue it**, because the schedule was in the same buffer.

**The redelivery notices and cannot repair, which is the distinction this ADR
turns on.** §9.5's `InboxFilter` writes its row after the consumer returns, so
the same crash leaves no row and the message is redelivered — but the instance
has already moved on, so no transition accepts the event. `StockReserved` in
`AwaitingPayment` is not one of the machine's declared `Ignore`s (those sit
`During(Compensating)`), so it is genuinely unhandled: MassTransit's default
raises, §9.8's five retries are spent, and the message lands in the error queue
§13.6 pages on.

> **So the window was observable, and an earlier draft of this ADR said it was
> silent.** That draft followed [#128](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/128)'s
> own text, which describes [#117](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/117)
> as having replaced the fault with an `Ignore()` and a log line. #117 did try
> that and then **removed** it; what shipped keeps MassTransit's default and
> enumerates the legitimate arrivals individually. Reading the issue instead of
> the state machine is how a claim about a signal survived into a decision
> record about durability — the same failure as the API name two paragraphs
> on, and Copilot caught this one.
>
> **It does not weaken the case, it relocates it.** A page is not a repair.
> The error queue says an order is stranded; nothing in it recreates the
> `AuthorisePayment` that was never sent or the `PaymentTimeout` that would
> have rescued the order, so the outcome is an operator reading
> `docs/runbooks/error-queue.md` and reconstructing by hand what the process
> lost. What this decision buys is that there is nothing to reconstruct.

**§9.3's prohibition does not reach this case, and the sentence that said it
did rested on a premise now known to be false.** §9.3 forbids a second outbox
because "two dispatchers means two retention policies, two sets of ordering
guarantees, and one of them will be the one nobody monitors" — an argument
about a second **application** outbox competing with the first for the same
job. It then exempted sagas on the grounds that routing their output through
§9.4's outbox "would add a second staging hop with no additional guarantee".
That was true only while the in-memory outbox was believed to be durable. It is
not, and the alternative it dismissed is not merely more expensive — it is
**unavailable**: the saga's timeouts are scheduled messages, the delay is a
transport feature ([ADR-021](#adr-021--saga-timeouts-are-scheduled-by-the-broker)),
and no dispatcher of ours can replay a delay it never held. An application
outbox carrying `AuthorisePayment` but not `PaymentTimeout` would close half
the window and leave the half with no bound at all.

> **The two mechanisms are not competing for one job, which is the whole of the
> exemption.** §9.4's outbox stages what a *handler* publishes, inside §6.3's
> transaction, and every one of Ordering's other three endpoints is durable
> that way already — their consumers publish through it, so the in-memory
> outbox there defers sends that have already committed. The saga is the one
> consumer that sends on the bus directly, and this is the outbox for that.
> Nothing is staged twice and no message can take either path.

**Both inboxes stay, and they answer different questions.** `InboxFilter` is
§9.5's long-window duplicate suppressor, pruned on §9.4's seven-day retention
— it is what stops a redelivered `OrderPlaced` starting a **second** workflow
hours after the first finalised, which is the defect PR-21 filed when §9.8's
saga exemption was removed. MassTransit's `InboxState` is a short-window
delivery record on its own duplicate-detection window, and it exists so the
outbox filter knows which of the committed messages it has already sent.
Retiring either one costs a guarantee the other never made.

**Consequences.**

- **The `ordering` schema now holds five messaging tables where the chapters
  describe two.** MassTransit's are singular (`ordering.OutboxMessage`,
  `ordering.InboxState`, `ordering.OutboxState`) and this platform's are plural
  (`ordering.OutboxMessages`, `ordering.InboxMessages`). They do not collide,
  and that is a property of MassTransit's naming rather than a decision anybody
  took — a reader of the database sees five and should not have to work out
  which chapter owns which.
- **There are two retention policies, which is exactly what §9.3 warns
  about — and the second is narrower than "MassTransit's tables".**
  `AddEntityFrameworkOutbox` registers an
  `InboxCleanupService<OrderingDbContext>`, and the package's own documentation
  scopes it to one table: it "is responsible for removing `InboxState` entries
  after the expiration window timeout has elapsed". `ordering.OutboxMessage` is
  not on a timer at all — the outbox middleware removes a row once the message
  has reached the transport, so that table drains as a consequence of delivery.
  `ordering.OutboxState` is emptier still: it belongs to the bus-side outbox
  and, with `UseBusOutbox()` not called, **nothing writes, reads or prunes it**
  in this configuration. §9.4's `RetentionPurgeService` prunes ours and does
  not read any of the three. Folding the two together was
  considered and refused: deleting an `InboxState` row whose outbox messages
  have not been delivered turns a retention job into the message loss this
  decision exists to close. The cost is one more unmonitored timer, and it is
  taken rather than dodged.
- **`UseBusOutbox()` is deliberately not called.** It intercepts
  `IPublishEndpoint` and `ISendEndpointProvider` *outside* a consume context —
  the API request path, which §9.4's application outbox already owns. Calling
  it would put a third staging mechanism on a path that has no dual write.
  **The visible consequence is that `ordering.OutboxState` stays empty for
  ever**: it is the bus-side outbox's own table, so with that call absent
  nothing writes it, nothing reads it and nothing prunes it. It exists because
  `OutboxMessage.OutboxId` carries a foreign key to it and the model would not
  build without it — say so where an operator can read it, rather than leaving
  a permanently empty table to be diagnosed.
- **This is the first artefact in `Ordering.Infrastructure` whose EF model is
  not described by an `IEntityTypeConfiguration<T>` in this repository.** The
  three entities are MassTransit's, mapped by
  `modelBuilder.AddTransactionalOutboxEntities()`, so §7.2's "mapping lives in
  configuration classes" rule has one stated exception rather than a quiet one.
- **The scaffold is unaffected and that is not luck.** §4.5's scaffold reads
  Catalog as its template, and Catalog has no saga. A second service with one
  inherits this decision by citing it, not by copying a file.
- **The consume transaction is now open across a broker publish, which it was
  not before.** The outbox delivers inside the same transaction it committed
  in: begin, lock the `InboxState` row, save, **publish to RabbitMQ**, save,
  commit. Under `UseInMemoryOutbox` no database transaction was open while a
  send was in flight. So a degraded broker now holds a SQL transaction and a
  row lock for up to MassTransit's `MessageDeliveryTimeout` — 30 seconds at
  this pin — per staged message, per in-flight delivery. That is inherent to
  the mechanism rather than a defect in the wiring, and it is the price of the
  guarantee: the alternative to holding the transaction is not holding the
  guarantee. It belongs in the capacity argument for the connection pool, and
  in any runbook reading lock waits during a broker incident.
- **The path this decision exists to make survivable is also the one that
  leaks.** `InboxCleanupService` deletes `InboxState` rows whose `Delivered` is
  set. A transition that commits and then fails delivery past the endpoint's
  five retries leaves `Delivered` null for ever, so that row and the
  `OutboxMessage` rows beside it stay until somebody removes them by hand.
  §9.4's purge does not read those tables. The volume is bounded by how often a
  message exhausts its retries and reaches the error queue — which §13.6
  pages on, so the leak has an alarm in front of it though nothing measures the
  leak itself. Stated rather than closed: a cleanup that removed undelivered
  rows would be the message loss this decision is about, so the right owner is
  whoever drains the error queue.
- **A schedule survives the crash; its deadline does not survive it exactly.**
  The delay is carried as a *relative* value and re-applied when the outbox
  delivers, so a message staged and then delivered ten minutes later fires ten
  minutes late. On the ordinary path the drift is milliseconds. On the path
  this ADR is about — commit, crash, redelivery — a `PaymentTimeout` fires
  at 15 minutes plus the outage and a `DespatchTimeout` at three days plus it.
  Later is the safe direction for both, and "the schedule commits with the
  instance" should not be read as "the deadline is preserved".
- **`MessageDeliveryLimit` and `DuplicateDetectionWindow` are left at
  MassTransit's defaults, and both are worth knowing rather than tuning
  blind.** One message is delivered per transaction, so a transition staging
  two of them costs several sequential begin/commit cycles and inflates that
  row's `ReceiveCount` — which is therefore **not** a redelivery count, and a
  runbook reading it as one would be wrong. The duplicate-detection window is
  thirty minutes, which is what "short-window" means where this decision
  contrasts MassTransit's inbox with §9.5's seven-day retention. Neither is
  tuned here because nothing has measured what they should be; naming them is
  what stops the next reader assuming they were chosen.
- **The saga's consume transaction is longer, wider and stricter than it was,
  and §12.4's fixture is where that shows.** It now spans `InboxState`,
  `OutboxMessage` and the instance, at `Serializable`, and holds through a
  broker publish. Nothing in production deletes a schema, so nothing there
  meets it the way a test does — but Respawn's reset deletes every row in the
  `ordering` schema while a consumer from the previous test may still be
  committing, and two multi-table transactions taking locks in opposing order
  deadlock. That hazard predates this decision and the fixture already
  documented it; what this decision did was widen it, from a single-row write
  to a three-table `Serializable` one. `ServiceFixture.ResetAsync` retries
  error 1205 and carries the argument. **Recorded here rather than only there,
  because the branch tried to close it by removing a background service and
  the deadlock reproduced anyway** — a fixture retry answers a property of this
  decision, not a property of that service.
- **The pre-flush window stops existing; the catch-all does not come back.**
  #117 removed an `OnUnhandledEvent(x => x.Ignore())` because three arrivals
  reached it and it could not tell them apart — a post-flush duplicate, a
  pre-flush crash that had lost the instance's commands, and a misroute. This
  decision deletes the middle one, which leaves two, and a callback that
  answers two cases the same way is still only as right as its worse one. The
  enumeration stays, and so does the default that faults everything it does
  not name.

## ADR-033 — Revocation is bounded by the token lifetime, and no denylist exists

**Decision.** This platform accepts a **bounded revocation window** of **330
seconds** — the access-token lifetime of 300, stated normatively in
[§11.3](11-identity-authorization.md), plus the 30-second `ClockSkew` a
lifetime check adds to `exp`. Both settings are part of the bound, and only
one of them is the realm's. There is no token denylist and no
introspection call: a service validates signature, issuer, audience and
lifetime locally and consults nothing else. [ADR-006](#adr-006--redis-for-cache-and-coordination-never-as-a-store-of-record)'s
listing of "the token denylist" among Redis's contents is withdrawn. The
`{service}:denylist:` keyspace keeps its row in [§8.1](08-caching-redis.md)'s
table as a **reservation**, on the terms `{service}:ratelimit:` already has, and
`RedisKeys` spells no member for it.

**Why.** The claim had no consumer and read as a control. `RedisKeys.Denylist`
existed, §8.1 gave the keyspace the strictest eviction policy in the platform,
and ADR-006 recorded the decision — while §11.3's own prose already admitted
that observing revocation "needs introspection or a deny list, neither of which
this platform has". Three sites implied a mechanism the fourth denied, and a
reviewer asking "is revocation handled" met a yes.

Building the consumer instead was considered and refused on four measurements,
not on preference. **Two of the four hosts have no Redis at all** — the gateway
and the BFF each carry one project reference and neither calls
`AddRedisConnections` — and the gateway is the edge every external request
enters. A `JwtBearerEvents` handler resolves per request, so `ValidateOnBuild`
cannot see the gap and the gateway would throw on its first authenticated
request; making the lookup optional instead would mean the edge silently never
checks, which is the fail-open shape §12's gate-coverage rule refuses.
**The keyspace is per-service and the ACL enforces it**: `RedisKeys` prefixes
with `ApplicationName` verbatim and §8.1 provisions `~{ApplicationName}:*` from
the same value, so one revocation is N writes against a host inventory nothing
enumerates. **There is no producer**, and no chapter specifies one — the realm's
`web-bff` client sets `backchannel.logout.session.required` with no
`backchannelLogoutUrl`, so a consumer shipped alone is a check that always
misses. And a mechanism no ADR decided is a design change to raise rather than
take.

**Consequences.** Logging a user out, disabling an account, or responding to a
stolen token at Keycloak has no effect on an already-issued access token for up
to 330 seconds. That is now a recorded number rather than a realm default
nobody chose, and `RealmImportTests` pins the lifetime half of it — the
chapter's figure and the realm's are two statements about one fact, and the
test is what keeps them from drifting apart. **Shortening the window is a realm
edit, a chapter edit and possibly a `ClockSkew` edit**, because the bound is a
sum: 300 from the realm and 30 from `TokenValidationParameters`, and cutting
the lifetime alone leaves the skew where it is.

> **The two halves of that number are enforced in different places, and only
> one of them travels.** `ClockSkew` is `Common.Web`'s, set in code and pinned
> by `JwtAuthenticationTests`, so every host that composes
> `AddCommonWebDefaults` carries the 30 wherever it is deployed. The lifetime
> is the realm's, and the realm this repository owns is
> `deploy/compose/keycloak/realm-export.json` — [§14.1](14-local-development.md)'s
> Compose realm, which is what `RealmImportTests` reads. Every chart points at
> `https://id.example.com/realms/commerce`, an externally provisioned realm
> this repository holds no configuration for and runs no deploy-time check
> against, so a deployed realm can issue five-hour access tokens while every
> sentence here still reads 300 seconds.
>
> **So the bound is half-guaranteed rather than local**, and saying "both
> halves are local" — as an earlier draft of this callout did — contradicts
> the Decision above, which is careful to say only one of the two settings is
> the realm's.
>
> **That division is the one §15.4 already draws for every Secret**, and the
> realm half is stated rather than closed for the same reason: the charts
> create no Secrets and provision no realm, so the platform's identity provider
> is somebody's operational input and not this repository's artefact. What this
> repository can honestly claim is the *shape* — the settings that must hold,
> the number they must hold at, and a test proving the one realm it owns holds
> them. **A deployed realm owes `accessTokenLifespan` 300 and no client-level
> `access.token.lifespan` override**; it owes no `ClockSkew`, which is not a
> realm setting at all and is why telling an operator to configure one would
> send them looking for something that does not exist.
> [#157](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/157)
> carries that gap and the three shapes a fix could take, because a gap this
> record merely described would be the TODO nothing re-checks.

**This is the whole of the revocation story**, so a future denylist
is a decision that supersedes this record rather than a gap someone may quietly
fill: it would owe a producer, a consumer reachable from every host including
the two with no Redis today, and a fan-out rule across per-service keyspaces.

## ADR-034 — The browser holds an access token and no refresh token

**Decision.** [§11.2](11-identity-authorization.md)'s browser client obtains a
short-lived access token through authorization code with PKCE and is issued
**no refresh token**. Continuity comes from silent renewal against the
authorization endpoint, bounded by the SSO session, rather than from a refresh
token held on the origin. The realm enforces it: the `web-app` client carries
`use.refresh.tokens: "false"`, and `RealmImportTests` pins that it does.

**Why.** A refresh token delivered to the browser must live somewhere script on
the origin can read. One XSS, or one malicious transitive dependency in the
bundle, then yields persistent account takeover that outlives the session and
survives a password change — and with no revocation path
([ADR-033](#adr-033--revocation-is-bounded-by-the-token-lifetime-and-no-denylist-exists))
it survives until the refresh token's own expiry. The realm's settings made
that concrete rather than theoretical: `revokeRefreshToken: false` and
`refreshTokenMaxReuse: 0` against an `ssoSessionMaxLifespan` of 36,000 seconds
meant a browser-held refresh token was reusable, never rotated, for up to ten
hours. Removing it collapses the exposure to ADR-033's 330 seconds.

**Terminating the flow in `Web.Bff` is the stronger answer and is not this
one.** Holding the tokens server-side behind a `HttpOnly; Secure; SameSite`
cookie is what the BFF pattern exists for, and the component is already in the
solution. It is not taken here because it is not one decision: it needs an OIDC
handler and a cookie stack in `Web.Bff`, antiforgery on every state-changing
route because cookie auth reintroduces CSRF, a realm change turning `web-bff`
from a service account into a standard-flow relying party, and a change to the
gateway's `web-bff` route, which today requires a bearer the browser would no
longer carry. That is an Appendix C row, and this decision is deliberately the
smaller one that can land now. §11.5's statement that the BFF's only credential
is its own client secret stays correct under this decision and would not survive
that one.

> **`use.refresh.tokens` is set in the local realm and in no other**, on
> exactly ADR-033's terms: `RealmImportTests` pins the Compose realm, every
> chart points at an externally provisioned authority, and nothing in this
> repository validates that one. A deployed realm that leaves the attribute at
> its default issues the browser a refresh token while this record says it does
> not. What is decided here is that the browser must not hold one; enforcing it
> where the realm is actually provisioned is an operational obligation this
> repository states and cannot check.

**Consequences.** The browser re-authenticates against the authorization
endpoint every 300 seconds — the token's own lifetime, which is the renewal
clock rather than ADR-033's 330-second acceptance bound; where the SSO session
has ended, the user sees a login. The residual is stated rather than closed: an
XSS still yields an access token a service will accept for up to 330 seconds,
and §11 says so instead of implying otherwise. Local development is
unaffected — the realm keeps
`directAccessGrantsEnabled` on `web-app` so a developer can obtain a token with
`curl`, which §14.1 documents as a local affordance and §11.2 now names rather
than leaving to a realm-file description.

## ADR-035 — An integration event carries identifiers, not personal data

**Decision.** An integration event published to every interested consumer
carries identifiers, not personal data — [§11.7](11-identity-authorization.md)'s
rule, applied without exception to the contracts in `Common.Contracts`.
`OrderConfirmed`'s `ShippingAddress` is removed and `ShippingAddressV1` is
deleted. How Shipping obtains a delivery address is left open and belongs to
Shipping's own PR.

> **This is not a claim that the events are anonymous, and an earlier draft of
> this record read as one.** `OrderConfirmed` still carries `CustomerId`, and a
> resolvable pseudonymous identifier is personal data under GDPR Art. 4 — as
> are order details linked to it. §11.7 says so itself one rule further on,
> where erasure at the owning service means "customer identifiers replaced".
> So the decision is narrower than "no personal data on the wire": what a
> broadcast contract may not carry is **directly identifying or free-text**
> personal data — a name, an email address, a postal address — the fields whose
> only remedy is to reach every copy and delete them.
>
> **What makes the identifier tractable is where its resolution lives.**
> `CustomerId` means nothing without the service that can turn it into a
> person, so severing that link once, in the one store that owns it,
> de-identifies every copy downstream without the erasure choreography ever
> having to reach the broker, an outbox row or a consumer's own store. That is
> the
> whole of why §11.7 tells a contract to carry the identifier rather than the
> value, and this ADR is that rule applied rather than a stronger guarantee
> laid over it.
>
> **The residual is real and is not closed here.** An event stream still
> carries linkable identifiers, so a consumer that independently resolves
> `CustomerId` re-creates the problem in its own store, and §11.7's rules for
> consumers are what bind it — unbuilt, like the rest of that section.

> **This removal may not ride a rolling deployment, and that is
> [ADR-026](#adr-026--consumer-capability-is-a-release-ahead-of-the-producer-that-uses-it)'s
> rule rather than a new one.** `ShippingAddress` was `required`, and
> `System.Text.Json` refuses a payload missing a required member — the same
> mechanism §9.2 records in the *adding* direction, where a new build faults
> every message an old build staged. Retirement is its mirror. Ordering is
> itself a consumer here: §9.6's saga binds `Event<OrderConfirmed>`, so it
> deserialises the whole contract even though its transition reads only
> `OrderId`. During an overlapping rollout a new replica's `OrderConfirmed`
> reaches an old replica that still declares the member, the message faults
> through §9.8's retries into the error queue, and §13.6 pages.
>
> **So the change takes one of the two shapes ADR-026 already allows**: a
> cutover with no overlap — which that record is careful to say is not a canary
> and must not be called one — or a two-release retirement, the first making
> the member optional everywhere and the second removing it. Either is a
> deployment decision rather than a code one, which is why it is recorded here
> and not solved above.
>
> **It is taken in place now because there is nothing deployed to break.** No
> cluster has ever run this platform — the charts, the alert rules and the
> canary are all artefacts no cluster has seen — so there is no old replica to
> hand a reduced payload to, and the cheapest moment to change a contract's
> shape is before anything runs it. **That is a fact with an expiry date**: the
> first deployment ends it, and after that a comparable removal owes one of the
> two shapes above. Recorded rather than left implicit, because "no consumer"
> was true of *other services* and was never true of Ordering.

**Why.** §9.1 argued the address onto the contract on "fat enough" grounds —
Shipping cannot act without one and should not call back to get it — while
§11.7 named an `OrderConfirmed` carrying personal data as *the* counter-example
of what must never happen. Two chapters stated opposite things about one
contract, so a reviewer reading either alone concluded the rule held.

The rule is the one that wins, because the address is unreachable once
published. It is serialised into `ordering.OutboxMessages`, whose retention
purge deletes only rows with `ProcessedAt IS NOT NULL` — deliberately, so that
abandoned rows survive for §13.6's alert — so an abandoned row keeps the
payload indefinitely, and a test pins that it does. It sits in the broker, for
which no chapter specifies a retention bound. And it reaches whatever each
consuming service persists from it — not the inbox, which stores a message id,
an endpoint and a time and no payload, but any projection, read model or log
that keeps what arrived — with §3.2 giving `OrderConfirmed` to Notifications,
which has no use for an address at all. §11.7's erasure choreography reaches
none of those surfaces, and §13.4's redactor matches on key names, none of
which cover an address.

**Removed rather than versioned**, on §9.2's own carve-out: where the point of
a change is that a value must not be on the wire, publishing V1 alongside V2
keeps the offending shape consumable for the length of the window, so the
standard remedy would re-arm the defect. §9.2's second condition is met by this
record existing, so that a later reader can check rather than take it on trust.

**Its first condition — that no service consumes the version — was not met,
and an earlier draft of this paragraph said it was.** Shipping and
Notifications are unbuilt, so no service on an independent release schedule
could be stranded; but §9.6's saga binds `Event<OrderConfirmed>`, which makes
Ordering a registered consumer, and a bound consumer deserialises the whole
payload however little its transition reads. "The saga reads only the
`OrderId`" is true and answers a different question.

**So the justification is not the condition; it is that nothing is deployed.**
No cluster has ever run this platform, so there is no old replica to hand a
reduced payload to — and the rollout callout above records what that costs
later, in full, because the exemption expires at the first deployment while the
rule does not. Stating the condition as met would have hidden a live-rollout
fault behind a rule about version bumps, which is exactly what that callout
exists to prevent. [ADR-028](#adr-028--a-money-movement-command-carries-no-subject)
remains the worked precedent for the removal itself, on the same reasoning:
removing the field removes the possibility rather than guarding against it.

**Consequences.** Shipping's PR inherits an open question and the context to
answer it: an explicit, auditable read back to Ordering, recorded as the
ADR-017 exception a second synchronous hop has to be, or a despatch-time lookup
against whatever store owns the address by then. Choosing now, before a
consumer exists to state what it needs, is the guessing §9.1's "ask the
consumers" rule refuses. `OrderConfirmedDomainEvent` keeps its `Address` and is
untouched: it never crosses a service boundary, and `ordering.Orders`
legitimately stores the address of the order it belongs to — §11.7 governs what
travels, not what a service holds about its own data.

---

## ADR-036 — The broker has a per-service identity

**Decision.** Every service authenticates to RabbitMQ as its own account, and
that account's `write` permission is scoped to the exchanges its own source
addresses: its bounded context's contracts, the receive endpoints it hosts, the
peer command endpoints its `Endpoints` class names, and the framework's fault
exchanges. The accounts and their permissions are declared in
`deploy/compose/rabbitmq/definitions.json`, imported at broker start, and held
to the code by `check_permissions.py`. `guest` is not among them.

**Why.** [§9.4](09-messaging.md) stamps a broker-borne command
`CommandOrigin.System` because it arrived on the service's own command queue,
and [§11.4](11-identity-authorization.md)'s ownership check is skipped for a
system-initiated command. That made queue write access sufficient to execute a
business command with owner privileges — confirm an order that was never paid
for, cancel any customer's order, mark one shipped with a forged tracking
number — and the platform had exactly one broker principal for every service to
share.

It was worse than "shared". Measured on the base image rather than assumed:
`guest` is tagged `administrator`, and `rabbitmq:4.1-management-alpine` ships
`loopback_users.guest = false` under the comment *"allow access to the guest
user from anywhere on the network"*. `rabbitmqctl environment` reported
`{loopback_users,[]}` and both services connected from container addresses. So
the least valuable service in the estate — [§3.2](03-bounded-contexts.md)'s
Notifications, which publishes nothing — would have been enough to drive
Ordering's write model.

[§11.2](11-identity-authorization.md) already states the posture this applies:
*"anything that reaches a service by another path would otherwise be
unauthenticated. Validation is cheap; assume the network is hostile."* That
reasoning had been applied to HTTP and never to the transport carrying
commands. [§8.1](08-caching-redis.md)'s per-service Redis ACL is the same
control one component over, and its argument — *"the ACL makes this impossible
rather than discouraged"* — is the one taken here.

**The alternative was a signed envelope, and it is not what this closes.** #44
proposed either broker isolation or a verifiable origin header; §9.4 forbids
the cheap version of the second outright, because an origin a caller can set is
an origin a caller can forge. A real one needs message signing, key
distribution and rotation that no chapter specifies — and it would buy nothing
here, because `ordering-commands` has exactly one producer today: Ordering's
own saga. Restricting write to that identity *is* the second half, reached by
the first half's mechanism.

**Consequences.** Three costs, all taken deliberately and none of them hidden.

`configure` is not exclusive. A consumer declares the exchange it binds, so
Ordering's `configure` reaches every `Common.Contracts.*` exchange including
Catalog's, and it could delete one it does not own. A topology where producers
pre-declare and consumers only bind would close it; MassTransit does not offer
one.

`read` on a peer's command endpoint is unavoidable, and it is wider than it
looks. A send to `queue:inventory-commands` declares and binds that
destination, `queue.bind` takes `read` on the exchange — measured, as a refusal
with `write` already granted — and a RabbitMQ permission pattern cannot
distinguish a queue from an exchange of the same name. So granting Ordering the
bind grants it the ability to consume Inventory's commands. The property this
ADR claims is about `write`, and that limit is stated rather than papered over.

**Provisioning is an obligation on a deployed broker, not a guarantee.** The
Helm charts name one Secret per service — `catalog-rabbitmq`,
`ordering-rabbitmq` — and this repository holds no configuration for the broker
those Secrets point at and runs no deploy-time check against it. That is the
division [§15.4](15-cicd-deployment.md) already draws for every Secret and
[ADR-033](#adr-033--revocation-is-bounded-by-the-token-lifetime-and-no-denylist-exists)
draws for the realm: verified where the platform provisions its own
infrastructure, stated as an obligation where it does not.

What is verified is local, and **which half `dotnet test` verifies is worth
stating exactly, because it is not all of it.** Both Testcontainers fixtures
run a broker holding the same imported permissions the Compose one does, so
the suites run against real permissions rather than against a broker with
none: a grant too narrow to declare a receive endpoint or bind a peer queue
fails on the branch that narrowed it. That is the half that rots as endpoints
are added.

**They get there by two different routes, and saying they both build the
image was wrong.** Ordering's fixture builds it, because ADR-021's
delayed-exchange plugin leaves it no choice. Catalog's maps
`definitions.json` and `20-commerce.conf` onto the stock image instead: it
runs no saga, so the plugin buys it nothing, and building put its two test
hosts — `Catalog.Api.Tests` and `Catalog.Application.Tests` — in a race for
one Testcontainers build-context tar. The mapped paths are a second copy of
the Dockerfile's `COPY` targets and `check_permissions.py` holds the two to
each other, because a drifted mapping boots a broker with no definitions,
seeds `guest`, and passes.

**The negative half is not exercised by that suite, and cannot be.** §9.6's saga
is driven by events Inventory, Payments, Shipping and Catalog publish, and none
of those services exists — so `Ordering.Api.Tests` publishes them through the
host's own bus, as `ordering-svc`, which ADR-036's production permissions
correctly refuse. The harness therefore widens its own `write` at fixture
start, in test code and never in `definitions.json`: loosening the deployed
artefact so a test could pass would leave the gate agreeing with a permission
set nothing deploys. It shrinks to nothing as each of those services gains a
publisher of its own.

So the negative property rests on two other things, both of which are real. It
was **measured**: attempted as `catalog-svc` against a running broker —
`ConfirmOrder` onto `ordering-commands`, a forged `OrderPlaced`, a
`ReserveStock`, a forged `PaymentAuthorised` — and refused on all four, with a
positive control publishing Catalog's own contract to prove the credential
worked. And it is **gated**: `check_permissions.py` derives what each service
needs from its own source and asserts that no service may write another's, so a
new receive endpoint, a new peer queue or a sixth bounded context fails the
build rather than the deployment.

---

## ADR-037 — The idempotency marker is a row in the command's own transaction

**Decision.** A command that opts into [§8.5](08-caching-redis.md) writes a
marker row keyed on the idempotency key, inside the transaction
[§6.3](06-cqrs.md) opens for it, and [§6.3](06-cqrs.md) reads that row before
the handler runs. The Redis claim keeps its job — the fast, atomic exclusion
that makes a concurrent duplicate fail early — and the row is what makes the
*ambiguous* case decidable. The port is `IIdempotencyMarkerStore` in
`Common.Application`; the implementation writes through the service's own
`DbContext`, which is what puts it on the transaction's connection. The
marker's retention window may not be shorter than the Redis claim's, and
`RetentionPolicy` refuses one that is.

**Why.** §8.5's `IdempotencyBehavior` releases its claim on every exception out
of `next()`. One of those exceptions is raised over work that **already
committed** — a `CommitAsync` that succeeded on the server whose acknowledgement
was lost — and releasing there frees the key for a command that ran, so the
retry writes it twice. That is the outcome the behaviour exists to prevent,
arriving on the one path it cannot see. The race has been recorded as knowingly
open since PR-09, and §8.5 has carried it as a stated exception in its own
opening sentence ever since: *at most one commit per key within `Retention`,
except across a lost acknowledgement.*

**Redis cannot close it, and no cleverer use of the existing store can.**
`IIdempotencyStore` is a port over the coordination connection ([§8.1](08-caching-redis.md),
[§8.3](08-caching-redis.md)) — a different system from the one the transaction
commits to, so neither ordering of the two is atomic. Claim first and a lost
acknowledgement releases a claim over durable work; commit first and the claim
is not held while the work runs, so two concurrent requests both execute.

**Holding the claim instead of releasing it is not the fix either, and that is
the part worth being explicit about.** §8.5's release table already records
what holding buys: it *postpones* the duplicate rather than preventing it. Every
Redis entry has a TTL, so a held key expires and the attempt after that claims a
free key and runs the command a second time. It would also cost every ordinary
fault its retry for a day, which is a large availability price for a
postponement. A row has no TTL: it survives until something deletes it, and what
deletes it is a retention window this repository chooses.

**Consequences.**

- **§8.5's guarantee loses its exception and gains a different bound.** It was
  *at most one commit per key within `Retention`, except across a lost
  acknowledgement*; it is now *at most one commit per key while the marker
  survives*, and what bounds it is `RetentionPolicy.IdempotencyWindow` rather
  than the Redis TTL. Both the exception and the shorter bound go at once,
  because the row outlives the claim and answers the question the claim
  could not.
- **A refused retry is a 409 and not a replay, and it is reached two ways.**
  On the lost acknowledgement the attempt threw before it returned, so §8.5's
  store never recorded an outcome to hand back. On the commoner path the
  command succeeded and its payload *was* recorded, and then the claim expired
  at `Retention` while the marker lived on — so the retry finds the outcome
  gone rather than absent. `CommandAlreadyCommittedException` therefore says
  the command was applied and its result is **no longer available**, which is
  true of both; naming a cause would be wrong on one of them. A client that
  needs the outcome reads the resource.
- **A late retry that used to re-run is now refused, and that is the price of
  the bound moving.** While the guarantee ended at `Retention`, a retry after
  the claim expired ran the command a second time by design — the exception in
  the old sentence and the expiry in it were the same fact. Removing the
  exception necessarily removes the re-run: between the claim's expiry and the
  marker's purge, six days on the shipped defaults, a caller retrying a
  succeeded command gets a 409 where it used to get a fresh execution. That is
  a real loss against both a replay and a re-run, and it is strictly better
  than the duplicate write it replaces.
- **§6.3's transaction gains a read and a write it did not have.** The read is
  before `next()`, so a command that already committed does no work at all
  rather than doing it and losing it to a constraint violation on the way out.
  The write is after the failure guard and after §2.3's aggregate count, so a
  command this transaction is about to refuse leaves no marker to refuse its
  retry with.
- **The key crosses between the two behaviours on a scoped carrier.** §8.5
  builds `{subject}:{operation}:{commandId}` from a principal it binds and a
  `static abstract` member reachable only through its own
  `IIdempotentCommand` constraint; §6.3 is constrained to neither and would have
  to rebuild it by reflection. `IdempotencyContext` carries the built value, and
  §6.3 reads it **once**, before anything runs — a nested dispatch would
  overwrite it mid-transaction, and re-reading afterwards would mark the inner
  command's key against the outer command's rows.
- **Every service gains a table, a migration and a third retention purge.**
  `IdempotencyMarkers` sits beside `OutboxMessages` and `InboxMessages` in the
  service schema, mapped by an `IEntityTypeConfiguration` per service on
  [§7.2](07-persistence.md)'s terms, and §4.5's scaffold ships it — a service
  scaffolded without it would fail a purge every hour and then fail the first
  idempotent command it was ever given, both against a table that is not there.
- **The window is a correctness setting wearing a housekeeping setting's
  clothes, so it is the one with a floor.** A purged outbox row loses a
  debugging record and a purged inbox row loses a suppression the broker will
  not exercise again; a purged marker re-opens the duplicate. Setting the window
  below the Redis claim's life leaves a stretch in which the key is claimable
  again and nothing remembers the commit, so `RetentionPolicy` reads
  `IdempotencyRetention.MarkerFloor` and refuses anything shorter rather than
  restating the number. **The floor is the claim's window plus
  `MarkerLeadAllowance`, and equality is refused rather than admitted as the
  exact fit.** Two things reorder the expiries: the marker is stamped inside
  the transaction while the claim is re-armed after it commits, so the claim's
  window starts later by the commit's tail; and the marker's age is the purging
  pod's clock against the writing pod's timestamp, across three replicas, so
  skew moves it again. Five minutes bounds their sum rather than removing
  either, and the two need different fixes —
  [#167](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/167)
  for the clocks and
  [#168](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/168)
  for the lag.
- **The uniqueness of the key is a backstop and not the mechanism.** Two
  attempts that reach the write concurrently produce a constraint violation
  rather than two rows; what makes that case rare is the Redis claim, and what
  makes it loud is the primary key. Neither is asked to do the other's job.

> **The floor consequence above is superseded by
> [ADR-038](#adr-038--the-marker-and-its-claim-are-ordered-by-construction-not-a-margin),
> and nothing here has been edited.** An ADR is superseded and never rewritten:
> the decision this record took — the marker is a row in the command's own
> transaction, read before the handler and written inside it — is untouched and
> still binding, and so is every argument for it, including the one that the
> window is a correctness setting and therefore the only one of the three with
> a floor.
>
> **What moved is the floor's *composition*, not its existence.** This record
> says the floor is the claim's window plus `MarkerLeadAllowance` and that
> equality is refused, and names the two things that reordered the expiries —
> the claim re-armed after a commit the marker was stamped before, and two
> pods' clocks ageing one row. Both were closed at the source rather than
> bounded, so `MarkerLeadAllowance` is gone and the floor is the claim's window
> exactly. The two paragraphs above that describe those terms are the reason
> the allowance existed, which is why they are worth keeping where somebody
> reaching for one will read them.

## ADR-038 — The marker and its claim are ordered by construction, not a margin

**Decision.** [§8.5](08-caching-redis.md)'s Redis claim and [§6.3](06-cqrs.md)'s
durable marker are ordered by the shape of the code rather than by a margin:
the claim's window **starts** before the marker is stamped, on the same thread
in the same dispatch, for every configuration `RetentionPolicy` admits. Two
changes are needed and neither is sufficient alone.
`IIdempotencyStore.CompleteAsync` **preserves what the claim had left** instead
of re-arming a fresh retention, so the claim's window runs from the claim.
`IdempotencyMarker.CommittedAt` is written by a `SYSDATETIMEOFFSET()` column
default and the marker's purge cutoff is computed in SQL, so the row's age is
one clock's arithmetic. `IdempotencyRetention.MarkerLeadAllowance` is retired,
`MarkerFloor` is `Window` unchanged, and a marker window equal to the claim's
is admitted.

**That the marker's expiry then falls after the claim's is a conclusion drawn
from that ordering, and this record does not decide it.** It needs the two
windows counted at the same rate, and they are counted by two servers — Redis
for the claim, SQL Server for the marker — which is
[#171](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/171);
and it needs the marker to reach the database inside the claim's window, which nothing
here bounds. Both are stated under *Consequences* rather than folded into the
decision, because a decision that claims what its own consequences take back is
the contradiction this appendix exists to keep out.

**Why.** ADR-037 put a correctness property at a boundary set by a retention
number, and then could not make the two numbers alone decide it. The rule it
needed — the marker outlives the claim — is a statement about two *instants*,
and the two instants were produced by different events on different clocks. A
five-minute allowance was added to the floor to cover both, which made every
admissible configuration safe for ordinary values of each and left the rule
itself untrue: nothing bounded either term, and nothing detected one exceeding
the allowance. **A margin that stands in for an unbounded quantity is a
guess with a number on it**, and the failure it guesses wrong about is a
silent duplicate write.

**The first term was the lag between two writes, and it was not the clock's
fault.** §6.3 stamps `CommittedAt` inside the transaction, before
`SaveChangesAsync`; §8.5's `CompleteAsync` runs only after `next()` has
returned, which is after the commit. Re-arming there started the claim's window
at the *commit* — later than the stamp by the commit's own tail, ordinarily
milliseconds and unbounded in principle, since a suspension or a stalled
connection between those two points stretches it. Redis then outlived the
marker by exactly that lag with perfectly synchronised clocks. Preserving the
claim's remaining life starts the window at `TryClaimAsync`, which precedes the
stamp on the same thread in the same dispatch — so the ordering is a
consequence of the code's shape rather than of any duration.

**The second was that one row was aged by two clocks.** `CommittedAt` was
stamped from the writing pod's `TimeProvider` and the purge cutoff computed on
whichever pod ran the purge; [§15.3](15-cicd-deployment.md) ships three
replicas of each service, so those were routinely two machines. A purger whose
clock led the writer's by δ deleted the marker δ early, the claim then expired
into a table that had already forgotten the commit, and the next retry ran the
command a second time. A column default and a cutoff in SQL put both ends of
the comparison on the server that owns the row, and the term disappears rather
than being bounded.

**Only the marker moves to the database's clock, and the asymmetry with the
outbox and the inbox is the decision rather than an inconsistency to tidy
later.** [§9.5](09-messaging.md) keeps those two on the registered
`TimeProvider` with a stated reason: a test host substitutes the clock, and a
row written on the server's wall clock is one no substituted clock can reason
about. That reason is worth more where the window is housekeeping — a purged
outbox row loses a debugging record, a purged inbox row loses a suppression the
broker will not exercise again — and worth less than a single clock where the
window *is* the guarantee. The marker's own suites stage rows at explicit ages
against the real clock rather than a substituted one, so nothing was traded
away to get this; a service that later needs the marker purge under a fake
clock is a service that needs a different seam, not a different column.

**Consequences.**

- **§8.5's replay window is now the remainder of the window the claim opened,
  and that is a change to what a caller is promised.** It was a full retention
  starting at the commit, so the stated twenty-four hours were twenty-four
  hours a caller actually got; it is now twenty-four hours from the claim,
  which a command that ran for an hour has spent an hour of. The guarantee that
  matters — at most one commit per key while the marker survives — is
  unaffected, and it is the one the mechanism exists for. A caller who needs
  the outcome after that reads the resource, exactly as ADR-037 already says.
- **`CompleteAsync` loses its `retention` parameter, and the absence is the
  contract.** A signature that still took one would invite the next
  implementation to use it. `TryClaimAsync` keeps its own, because the claim is
  where the window is opened.
- **The floor admits equality, and the sentence a review round once corrected
  is correct again.** `RetentionPolicy.IdempotencyWindow` may now be set to
  `IdempotencyRetention.Window` exactly — equal is admitted, and is the
  smallest window that is — which is what makes narrowing the window towards
  the claim's own length a supported way to buy back the late retry ADR-037
  costs. **Admitted is not gap-free**, and the two paragraphs above say why:
  a lower bound is what a floor is for, and this one refuses the windows that
  are certainly wrong rather than certifying the ones that are left. The floor is
  still read from `IdempotencyRetention.MarkerFloor` rather than restated, and
  that member survives its own arithmetic becoming trivial because what it
  names is a relationship between two windows and not a duration.
- **Every service gains a migration, and §4.5's scaffold ships it.**
  `IdempotencyMarkerCommittedAtDefault` alters one column in one table, and it
  travels with the table for the reason the table travels: a service scaffolded
  with the marker and without the default ages its rows on the writing pod's
  clock while the purge ages them on the server's, which is precisely the skew
  this record removes, reintroduced by omission on every new service.
- **Staging a marker at a controlled age still works, and the mapping is why.**
  `ValueGeneratedOnAdd` over a store default means EF omits the column while the
  property holds its sentinel and writes it when it does not — a default is
  what happens in the absence of a value, not a trigger overriding one. The two
  `RetentionPurgeTests` suites depend on that and are unchanged.
- **What this does not close is the claim expiring under a running handler.**
  Nothing here bounds the retention against a handler's runtime; past the
  claim's expiry a successor may claim the key and both attempts run, and what
  keeps the loser from corrupting the winner's entry is still the claim token
  (#127). That residual is §8.5's and is unchanged — and it is now the only one
  of the three, where it used to be one of three.
- **What holds by construction is the ordering of the two *start* events, and
  not that the two windows are counted by one clock.** This is the limit of the
  decision and it is stated here rather than left for a reader to find. Redis
  expires the claim after `Window` elapsed by *Redis's* clock; the purge deletes
  the marker after `IdempotencyWindow` elapsed by *SQL Server's*. Nothing
  couples the two rates, so a forward step of the database's clock relative to
  Redis's — an NTP correction, a host migration, a resumed snapshot — can carry
  the cutoff past the marker while the claim is still live, and what absorbs it
  is the handler's runtime plus whatever the window exceeds the floor by: six
  days on the shipped defaults, and nothing at all at the floor
  ([#171](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/171)).
- **Reinstating an allowance is not the answer to that, which is why the floor
  is still `Window`.** Five minutes never bounded a clock step either, and a
  step is not bounded by anything this repository can assert — so a number there
  would repeat in a third term the mistake this record removes from two. What
  closes it is giving both deadlines one time source, which is a change to how
  the claim is stored and belongs to its own record.
- **A backward step of the server's clock is harmless on its own, and there is
  no second hazard here to state.** Both ends of the purge predicate —
  `CommittedAt` and `DATEADD(second, -@WindowSeconds, SYSDATETIMEOFFSET())` —
  read the clock the step moves, so a marker stamped after it is aged against a
  cutoff on the same shifted timeline and expires on schedule; the rows stamped
  before it survive the step's own size longer, which is the direction that
  costs nothing — a marker outliving its claim by more is what the floor is
  for. What is not harmless is the forward correction that follows, and that is
  #171's forward step rather than a hazard of its own.

---

[← §15 CI/CD](15-cicd-deployment.md) · [Index](README.md) · [Appendix B →](appendix-b-licences.md)
