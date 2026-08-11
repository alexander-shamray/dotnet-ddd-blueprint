# PR-15 — Contracts, inbox consumers, inbox and outbox retention purge

Design spec, frozen at write time. Appendix C names the PR
(`feat(messaging): Contracts, inbox consumers, inbox + outbox retention purge`,
depends 14 and 12) and its deliverables: the **remaining** versioned records in
the `Common.Contracts` PR-14 created, the inbox filter (§9.5), the
`IntegrationEventConsumer<T>` adapter (§9.4), one purge hosted service covering
**both** tables, and `Platform.IntegrationTests` starting here with §12.6's
contract suite — no domain reference, versioned namespace, round-trip.

It is the consume side of §9 arriving in one PR, and it is the first PR whose
main deliverable is a vocabulary rather than a mechanism: twenty-odd records
that no code in this repository publishes yet, because the services that own
them do not exist. That is unusual enough to be the first decision below.

## What this PR is

§9.5 end to end, plus the half of §9.4 that faces the broker rather than the
database. The mechanism lands in `Common.Infrastructure` and the vocabulary in
`Common.Contracts`; Catalog gains the inbox table, the two registrations that
make the filter's transaction shared, and the purge — and no receive endpoint,
for a reason Decision 6 argues rather than leaves implied.

## Decision 1 — every remaining contract lands, and §3.2 is the completeness check

**Decision.** All five versioned namespaces are filled in one go:
`Common.Contracts.{Catalog,Ordering,Inventory,Payments,Shipping}.V1`, holding
every name in §3.2's Publishes and Accepts columns plus the payload types §9.1
and §9.6 give them. Twenty-three records and two static vocabularies.

**Why.** The rule this repository normally applies — "a record belongs in the PR
whose code publishes or consumes it" — is Appendix C's own rule, and Appendix C
suspends it here by naming *the remaining versioned records* as this PR's
deliverable. It suspends it for a reason: §12.6's contract suite is the other
half of this PR, and its three assertions are about the assembly as a whole. A
suite that constrains three contracts is not the suite §12.6 describes, and the
rules "arrive with the assembly they constrain" — which is the row's own
justification.

§3.2 is what makes the set decidable rather than a judgement call. It closes in
both directions by construction: every name in a Consumes cell appears in
exactly one Publishes cell, and every Publishes name appears in at least one
Consumes cell. So the contract assembly is complete when it holds exactly the
union of those columns and §9.6's commands, and incomplete otherwise — a
mechanical check, which is the standard this repository holds a gate to.

**Consequences.** `Common.Contracts` keeps its two absences — no packages, no
project references — and both are now load-bearing over twenty-five files
rather than two. `MessageTypeMap` in Catalog's host grows to hold every
contract in the platform, which is harmless and worth stating: the map admits
them, `CatalogIntegrationEventMapper`'s allow-list still produces exactly one,
and nothing else can stage a row.

## Decision 2 — the members of a contract nobody publishes are still decided here

**Decision.** Where §9.1, §9.6 or Appendix D.5 writes a contract's members, they
are copied exactly. Where they say only "the envelope plus `OrderId` and what
its own step decided" (D.5), this PR decides, and the choice is argued at the
type. Appendix D.5 is amended in the same change so the record and the
inventory agree.

**Why.** The alternative is a placeholder — `OrderId` alone on every saga event
— which reads as complete and is not. §9.6's state machine already names three
members this PR would otherwise omit: `ctx.Message.Reference` on
`PaymentAuthorised`, `ctx.Message.TrackingNumber` on `ShipmentDispatched`, and
`ctx.Message.Lines` on `OrderPlaced`. A contract missing a member the saga
sample reads is a contradiction the moment PR-19 compiles that sample.

**Consequences.** Four contracts carry a member no chapter states:
`StockReservationFailed.UnavailableProductIds`, `StockLevelChanged.QuantityAvailable`,
`PaymentDeclined.Reason` and `OrderCancelled.Reason`. Each is the fact its own
step decided and each is a primitive; all four reach Appendix D.5.

## Decision 3 — the filter and both consumers are common, not per-service

**Decision.** `InboxFilter<T>`, `IntegrationEventConsumer<T>` and
`CommandConsumer<TMessage, TCommand>` live in `Common.Infrastructure`
(`Inbox/` and `Messaging/`), not in each service's Infrastructure. §9.4, §9.5
and §9.8 are amended: their samples say `namespace Ordering.Infrastructure.Messaging`
and `InboxFilter<T>(OrderingDbContext db)`.

**Why.** This is PR-14's finding, one type over. That PR found §9.4 writing
`ordering.OutboxMessages` into code every service shares and corrected it to a
registered `OutboxTable`, because a common type naming one service's schema
cannot be right. These three are the same shape: nothing in any of them is
per-service, and the chapter names Ordering only because the chapter is written
from Ordering's point of view. Six copies of a generic consumer is six places
for the §9.4 "empty is a decision" throw to be dropped from.

The `OrderingDbContext` parameter is the whole of the problem, and §9.5's own
argument is what solves it: the filter must write through **the service's**
context so the inbox row shares the handler's transaction. `DbContext` is that
context's base type, and `db.Set<InboxMessage>()` reaches the entity without
naming a `DbSet` property only Ordering has.

**Consequences.** `Common.Infrastructure` gains an `EntityFrameworkCore`
package reference — the first, and argued in the csproj: the outbox reads
through Dapper by design, and the inbox is the opposite case, because sharing a
transaction is the point rather than an incidental.

Each service registers the alias:

```csharp
services.AddScoped<DbContext>(sp => sp.GetRequiredService<CatalogDbContext>());
```

**The delegate is load-bearing and its absence is silent.**
`AddScoped<DbContext, CatalogDbContext>()` compiles, resolves, and constructs a
**second** context in the same scope — so the inbox row commits in its own
transaction and §9.5's "Yes" row quietly becomes its "No" row. A test asserts
the two resolutions are the same instance.

## Decision 4 — `InboxTable` beside `OutboxTable`, from one schema literal

**Decision.** A second registered value, `InboxTable`, with the same shape check
as `OutboxTable`. Both are constructed in `AddCatalogInfrastructure` from one
`"catalog"` literal in one local, and the identifier guard moves into an
internal `SqlSchema` helper both call.

**Why.** Three shapes were considered. Adding an `InboxQualifiedName` to
`OutboxTable` gives a type whose name covers half its contents. Renaming it to
`MessagingTables` is honest and rewrites PR-14's registration, its tests and
the scaffold's anchors for a naming improvement. A sibling type keeps both
names true, and the risk it introduces — two schema strings that could
disagree — is removed by there being one literal, not by there being one type.

**Consequences.** `SqlSchema` owns §9.4's regex and its bracket-quoting, so the
bound-at-128 argument and the reserved-word argument are stated once. Both
tables' `QualifiedName`s are shape-checked at construction for the same reason
the outbox's was: a schema cannot be a parameter.

## Decision 5 — one purge service, two statements, and `ProcessedAt IS NOT NULL` is tested

**Decision.** `RetentionPurgeService : BackgroundService` in
`Common.Infrastructure.Messaging`, resolving `OutboxTable`, `InboxTable` and a
registered `RetentionPolicy`. Each pass deletes in batches until a batch comes
back short, outbox first, then inbox.

**Why.** §9.5 states the shape — "both purges run from the same hosted service
on a slow schedule, batched so neither holds a long lock" — and §9.4 states the
one predicate that is not defensive: purging the outbox on age alone deletes
the abandoned rows §13.6's alert exists to surface, "turning permanent data
loss into a clean, empty table". That is a claim a test can make, so it is a
test: a row with ten attempts and a null `ProcessedAt`, aged past the window,
survives the purge.

`RetentionPolicy` is a registered value rather than three constants, because
§9.5 says the inbox window "must exceed the broker's longest possible
redelivery delay… Seven days is a starting point to check against RabbitMQ's
configured limits, not a default to accept". A number a chapter tells the
reader to check is a number the code has to let them change.

**Consequences.** The service holds no `IDbConnectionFactory` of its own; it
takes a scope per pass exactly as `OutboxDispatcher` does, and disposes the
connection every pass for the same pool reason. It logs a failed pass and
returns, because a purge that throws out of `ExecuteAsync` stops the host.

## Decision 6 — Catalog wires no receive endpoint, and that is asserted

**Decision.** `AddMassTransitMessaging` gains no `ReceiveEndpoint`, no
`ConfigureConsumer` and no `UseConsumeFilter`. A test asserts the bus is
configured with no receive endpoint of its own.

**Why.** §3.2 gives Catalog exactly one Consumes cell — `StockLevelChanged`,
owned by Inventory, which does not exist. Even with the contract now present,
binding it would create an endpoint whose every message reaches §9.4's throw:
"the endpoint binds this type, so something should handle it" is one of the two
sites where an empty handler list must fail, and Catalog has no
`IIntegrationEventHandler<StockLevelChanged>` — §8.4's cache invalidator is the
handler that eventually arrives, and it needs a cached query to invalidate.

This is PR-14's shape exactly: that PR registered no projection handler and
asserted no `Local` row was staged, rather than leaving the absence to be
inferred. Absence asserted is a contract; absence observed is a coincidence.

**Consequences.** All three new types are proven by the MassTransit test
harness PR-13 established, over handlers, mappers and contracts that live in
`Catalog.TestSupport`. The inbox table ships to Catalog anyway, and to every
scaffolded service, for `AddOutbox`'s reason inverted: the purge runs from
first boot, and a purge against a missing table logs a failure every pass.

## Decision 7 — `MessagingMetrics` completes here, and `PluggableInterfaces` reaches five

**Decision.** `Delivered` and `Rejected` join the class, `IIntegrationEventHandler<>`
and `ICommandMessageMapper<,>` join `PluggableInterfaces.All`, and
`ContractMappingException` lands in `Common.Application` beside the mapper port.

**Why.** Both lists were written to be finished by this PR and both say so in
the file: `MessagingMetrics`' remark names `CommandConsumer<,>` and PR-15,
`PluggableInterfaces` names the two interfaces and the PR that defines them.
Landing one consumer and not the other would leave two instruments recording
nothing and a list two-thirds complete, with a comment in each promising the
rest — and Appendix D.4 already describes `CommandConsumer<,>` as defined.

**Consequences.** `CommandConsumer` is the one type here with no call site in
Catalog at all: Catalog's Accepts column is empty by §3.2, so it is exercised
through the harness with a test-local wire type and mapper. That is PR-13's
precedent — the harness smoke proves composition, and the transport is proven
elsewhere.

## Decision 8 — `Platform.IntegrationTests` holds §12.6 and nothing else

**Decision.** A new test project referencing `Common.Contracts` alone, with
`ContractTests` and `ContractSamples`.

**Why.** §4.1 calls it "the only suite that references every service", and
today the platform's contracts are one assembly that references nothing — so
"every service" is satisfied by one project reference and grows as services
arrive. `ContractSamples` is hand-written, one entry per contract, and throws
by name for a type it does not hold: §12.6's own argument is that every member
is `required`, so there is no reflection shortcut, and a new contract without a
sample must fail rather than be skipped.

**Consequences.** The static vocabularies are excluded by the `IsAbstract:
false` filter without a special case, because a C# `static class` compiles to
`abstract sealed` — the filter §12.6 already writes does the work its comment
describes.

## What this PR does not do

- **No saga, no receive endpoints, no retry policy.** §9.6 and §9.8's endpoint
  configuration is Ordering's, and PR-19's.
- **No `ProductPublished` publication path change.** Catalog's mapper keeps its
  one entry; `PriceChanged` and `ProductDiscontinued` are contracts without a
  domain event to map from, and inventing the aggregate methods that raise them
  is a Catalog feature, not a messaging one.
- **No claim token on the outbox.** §9.4 names the lease-expiry residual and
  places its fix with §13.6's work; it stays open and stays stated.

## Verification

`dotnet test Platform.slnx` with Docker, `py -3.12 -m unittest` in
`tools/new-service` and in `.github/licence-gate`, and — because this PR touches
`tests/Catalog.*` — a scaffolded service built end to end, which is the one
breakage class the Python suite cannot see.
