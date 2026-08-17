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
**not** include `application/problem+json`. No other host in the platform
compresses anything.
**Why.** The framework ships `EnableForHttps = false` because compressing a
response that mixes attacker-influenced input with a secret leaks the secret's
length, which is BREACH and CRIME. Leaving it false here would be a mitigation
this topology cannot deliver: TLS terminates at the load balancer or Ingress
([§10.1](10-api-gateway.md)) and plain HTTP is forwarded inside the cluster, so
the gateway sees `http` on every request and the flag never fires — the edge
would compress everything regardless while the code read as though something
were guarding it. That is the same class of defect as a guard claiming a closed
set it never checks, and the *response* still reaches the browser over TLS
either way, so the exposure is not removed by the flag and cannot be argued
from the scheme. It has to be argued from content, and the content is what
makes it safe: the bodies crossing this edge are proxied API JSON, and the
platform puts no secret in one. Tokens are issued by Keycloak and reach the
gateway on an `Authorization` header rather than in a body
([§11.2](11-identity-authorization.md)), no
response sets a session cookie, and no endpoint returns an anti-forgery token.
The one body that *does* reflect a client-supplied value back — §10.5's
problem+json, carrying the `X-Correlation-Id` the caller may have chosen
(§10.4) — is the one the default type list omits, so the input half and the
compression never meet.
**Consequences.** The gateway now spends CPU per response — which is precisely
the resource §15.3 deliberately leaves *unlimited*, because CPU is compressible
and a cap on it surfaces as unexplained p99 spikes long before the pod is short
of capacity. Memory is the one §15.3 bounds, and a compressor's buffers are
small beside a body this same PR refuses past a mebibyte. So an edge latency
regression is investigated as CPU spent here and never as a leak, and a
compression provider is the first thing to look at. The omission of
`application/problem+json` is a framework default this platform relies on and
does not state, so `CompressedResponseTests` pins it from the wire in both
directions — adding the type to
`CompressibleContentTypes` would be re-taking this decision, and the test is
what makes that visible. **The rule is inherited rather than re-decided by
every host behind the edge**: PR-19's BFF is the first that could hold a
session and its responses pass through this middleware, so a BFF response
carrying a secret must not merely avoid compressing itself — it has to encode
itself, because the middleware declines any response that already carries a
`Content-Encoding` and that is the only opt-out there is. Verified, not read
off the documentation, in the same suite. And a service that one day needs to
accept an upload meets §10.1's body ceiling first, which is a number in
`GatewayLimits` rather than a per-route setting: raising it is a platform
decision made once, in the open.

---

[← §15 CI/CD](15-cicd-deployment.md) · [Index](README.md) · [Appendix B →](appendix-b-licences.md)
