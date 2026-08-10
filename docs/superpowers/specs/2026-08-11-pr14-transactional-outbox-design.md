# PR-14 — Transactional outbox and allow-list event mapper

Design spec, frozen at write time. Appendix C names the PR
(`feat(template): transactional outbox and allow-list event mapper`, depends
09, 13 and 10) and its deliverables: the outbox table and dispatcher (§9.4),
the `IIntegrationEventMapper` allow-list, `IIntegrationEventPublisher` with the
§9.3 contract, `MessageTypeMap` and `OutboxJson` landing *here* rather than
later, and integration tests proving the aggregate row and the outbox row
commit in one transaction, that `Stage` copies the envelope's `MessageId`, and
that every stageable domain event round-trips through `OutboxJson.Options`.

It is the heaviest PR in the plan (6 ideal days, `docs/roadmap.md`), and it is
where four things that have been deferred since PR-08 all come due at once:
the `Common.Application → Common.Domain` edge, `Common.Infrastructure`'s first
project references, the deletion of `NullDomainEventDispatcher`, and the first
message this platform actually persists.

## What this PR is

The whole of §7.5's flow, made real for one service: collect → map through the
§9.3 allow-list → stage `Broker` and `Local` rows → one `SaveChanges` →
post-commit delivery driven by §9.4's dispatcher. Catalog is the template
(§4.5), so the machinery lands in the building blocks and the wiring lands in
Catalog, and the scaffold inherits the wiring with none of the allow-list.

## Decision 1 — `Common.Contracts` is created here, with two files

**Decision.** `src/BuildingBlocks/Common.Contracts` is created by this PR
carrying exactly `IIntegrationEvent` (§9.1) and
`Common.Contracts.Catalog.V1.ProductPublished`. Appendix C's PR-15 row keeps
the rest: the remaining versioned records, the inbox filter, the
`IntegrationEventConsumer<T>` adapter and the purge service.

**Why.** §9.4's `OutboxMessage.Stage` is normative and reads
`message is IIntegrationEvent e ? e.MessageId : …`; `MessageTypeMap` selects
on `IIntegrationEvent` or `IDomainEvent`. Neither compiles without the
envelope interface, and Appendix C's own acceptance criterion for this PR —
"`Stage` copies the envelope's `MessageId`" — names an envelope. The interface
has to be here.

Given that, the one contract costs a single file and buys the thing the PR is
titled after. An allow-list mapper with an empty registry is the vacuous gate
PR-10 was praised for ending: §12.4's assertion that the domain type must
never reach the broker "is only checkable because the names differ", and with
no contract there is no second name. A test-local contract would prove the
lane and not the allow-list, because the registry is a static dictionary in
production code that no test can add to.

**Consequences.** `Catalog.Application` gains a `Common.Contracts` reference —
§4.3's one assembly that crosses a service boundary, arriving at the layer
that maps into it. PR-15 adds records to a project that already exists rather
than creating one, which is a smaller PR and not a larger one. The scaffolded
service gets the reference and an empty mapper, so its first contract is a
file and a dictionary entry.

## Decision 2 — `IIntegrationEventMapper` is an Application port, not a per-service type

**Decision.** The interface lives in `Common.Application`; only the
implementation is per-service (`Catalog.Application.Integration`). §9.3 is
amended: its single fenced block currently carries one
`namespace Ordering.Application.Integration;` over both the interface and the
implementation.

**Why.** `Common.Application.DomainEventDispatcher` injects
`IIntegrationEventMapper`. A common type cannot name a per-service one, so the
chapter as written does not compile. The same split §9.3 already applies to
`IIntegrationEventPublisher`, which it puts in `Common.Application`
explicitly.

## Decision 3 — the `Common.Application → Common.Domain` edge is drawn here

`IDomainEventCollector.CollectAndClear()` returns
`IReadOnlyList<IDomainEvent>`, which is the first Application member to name a
domain type. `Common.Application.csproj` and `IUnitOfWork`'s remarks have both
been predicting this edge since PR-08 and naming this PR; both are updated to
past tense rather than deleted, because the argument for why PR-09 did *not*
draw it is still the argument for counting `ModifiedAggregateCount` behind the
port.

## Decision 4 — `Common.Infrastructure` draws its first references

`Common.Application`, `Common.Domain` and `Common.Contracts` as project
references; `MassTransit`, `Dapper` and `Microsoft.Extensions.Logging.Abstractions`
as packages. The csproj comment that says "no project references, deliberately
… PR-14's outbox is what draws edges here" is replaced by the edges themselves.

**No EF Core.** `OutboxMessage` is a POCO with private setters and a static
factory; nothing in it names an EF type. The `IEntityTypeConfiguration<OutboxMessage>`
lives in `Catalog.Infrastructure/Persistence`, where the existing
`ApplyConfigurationsFromAssembly` scan finds it and where the service's schema
is already decided. A configuration in the building block would need EF Core
there *and* would not be discovered by the scan, so it would be a package
reference and a silent no-op at once.

## Decision 5 — the schema is a registered value, not a literal

**Decision.** §9.4's SQL hard-codes `ordering.OutboxMessages`, which is right
for a chapter written about Ordering and wrong for common code. `OutboxTable`
is a small registered record holding the schema; `OutboxDispatcher` composes
its three statements from it. The schema is shape-checked on construction
against `^[A-Za-z_][A-Za-z0-9_]*$` and the table name is fixed, so no
caller-supplied text reaches SQL unvalidated.

**Why.** The alternative is a per-service dispatcher, which is §9.3's
prohibition on "a second outbox table set" arriving by the back door — two
dispatchers, two retention policies, two sets of ordering guarantees. §9.4 is
amended to show the composition.

## Decision 6 — Catalog registers no projection handler, and the `Local` lane is proven by `Catalog.TestSupport`

**Decision.** `PublishProduct` stages one `Broker` row and no `Local` row,
because Catalog has no `IProjectionHandler<>` and §7.5 stages a `Local` row
only where `IProjectionRegistry` finds one. The lane's three behaviours —
per-row isolation, the attempt cap, and a staged `Local` row with no
registered handler failing loudly — are proven by domain events and handlers
declared in `Catalog.TestSupport` and admitted to the map through
`MessageTypeSource.Add`.

**Why.** This is the mechanism §9.4 designed `MessageTypeSource` for: "mutable
and resolved before the map, so a test host can add its own without replacing
the registration". Inventing a Catalog projection would mean inventing a read
model, and caching a read before §8.4's invalidation has a cached query to
invalidate is what PR-12 already refused. `Catalog.TestSupport` rather than
either test assembly, because the two suites cannot reference each other and
both need the types.

**Consequence.** That Catalog stages *no* `Local` row is itself asserted — it
is the `IProjectionRegistry` contract observed from outside, and it is what
makes "the Broker row carries the contract type" a statement about lanes
rather than about rows.

## Decision 7 — `Money` gains a public validating constructor

**Decision.** `Money`'s constructor becomes public and carries the guards
`Of` used to hold; `Of` and both operators delegate to it. §5.3 and the type's
own remarks are amended in the same change.

**Why.** This is the defect §9.4's round-trip assertion exists to catch,
found on the day the assertion was written rather than mid-batch during a
deploy. `Money` is a `readonly record struct` with a private constructor and
two get-only properties. `System.Text.Json` does not throw on that shape — a
struct always has a parameterless constructor, so it builds `default(Money)`,
finds no setters, and returns `Amount = 0, Currency = null`. Every domain
event carrying a `Money` silently deserialises to nonsense on the `Local`
lane, and the blueprint's own `OrderPlacedDomainEvent` has the same hole.

Three fixes were considered:

| | Rejected because |
|---|---|
| `[JsonConstructor]` on the private constructor | `System.Text.Json` in `Catalog.Domain` fails the §4.2 domain allow-list gate, which names that assembly as the extension the table forbids |
| Decompose `Money` into `decimal` + `string` on every domain event | Contradicts §5.5 — domain events carry domain types, and decomposition belongs at the *contract* boundary. It also fixes one value object and leaves the next one to fail the same way |
| A resolver modifier in `OutboxJson` that constructs through non-public constructors | Forty lines of reflection in the one class whose whole point is that both sides agree about a persisted format |

The public constructor is the smaller change and it *strengthens* the
always-valid claim rather than weakening it: today `operator +` and
`operator *` construct through the private constructor and skip `Of`'s
validation entirely, so "the constructor is private and `Of` is the only way
in" already relies on discipline. With the guards in the constructor there is
no unvalidated path at all, and `Of` stays the named factory §5.3 asks for.

**Consequence.** The rule generalises and is enforced rather than written
down: a type that reaches the `Local` lane must be one
`System.Text.Json` can reconstruct, and
`Every_stageable_domain_event_round_trips_through_the_outbox_options` is what
says so on the day it stops being true.

## Decision 8 — `MessagingMetrics` lands with one of its three instruments

`Projected` only, because `ProjectionInvoker` is its only call site until
PR-15's consumers. `Delivered` and `Rejected` join with
`IntegrationEventConsumer<T>` and `CommandConsumer<,>`. The comment in the
file names them and the PR they arrive with — the `PluggableInterfaces.All`
shape, which is this repository's established answer to a class the blueprint
describes whole and the code grows in instalments. The `Commerce.Messaging`
meter is already in `AddObservability`, so nothing about §13.2 changes.

## Decision 9 — the dispatcher is registered as a hosted service and removed in tests

`AddCatalogInfrastructure` registers `OutboxDispatcher` as an
`IHostedService` and as itself. `CatalogApiFactory` removes **only** that
hosted registration — never `RemoveAll<IHostedService>()`, which would stop
MassTransit's bus and silently disable every messaging test — and keeps the
singleton resolvable so `ServiceFixture.ProcessOutboxBatchAsync()` drives
exactly one claim-and-deliver pass. §12.4 prints this shape; it is followed
verbatim.

## Decision 10 — the commit-acknowledgement race stays open, and now has an answer

PR-09 left it open and named this PR. It is *not* closed here, and the reason
has changed: with the outbox in place the failure is no longer invisible, it
is an ordinary duplicate. A retry after a lost acknowledgement re-runs the
mapper, which mints a fresh `MessageId`, so the broker sees the same fact
twice — which is exactly the at-least-once delivery §9.4 promises and §9.5's
inbox is built to absorb. A SQL-side marker written inside the transaction is
still the fix for the *command*, and it still belongs with §8.5's
`IdempotencyBehavior`, whose seat between Validation and Transaction is
already reserved.

## What this PR does not do

- **No inbox, no consumers, no purge** — PR-15, and the receive endpoints and
  retry policies of §9.8 go with them.
- **No cached query and no cache invalidation handler** — §8.4's `Local` row
  needs something cached to invalidate, which is still nobody's PR deliverable.
- **No `OutboxMetrics` and no outbox alerts** — §13.6, PR-24.
- **No projection handler in Catalog** — Decision 6.
- **No `IdempotencyBehavior`** — §8.5, and Decision 10.

## Blueprint reconciliation carried by this PR

| Chapter | Change |
|---|---|
| §5.3 | `Money`'s constructor is public and validating (Decision 7) |
| §7.5 | The registration snippet becomes `AddDomainEventDispatcher()`, because `DomainEventDispatcher` and `ProjectionRegistry` are internal to `Common.Application` — the `AddDispatcher()` precedent of §6.2 |
| §9.3 | The mapper interface's namespace splits from its implementation's (Decision 2) |
| §9.4 | `OutboxTable` composes the SQL's schema (Decision 5) |
| Appendix C | PR-14 creates `Common.Contracts`; PR-15's row says which parts it adds |
| Appendix D | The eleven new types |
| §4.1 tree | `Common.Contracts` exists |
