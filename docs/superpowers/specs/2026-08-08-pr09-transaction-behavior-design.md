# PR-09 — TransactionBehavior over IUnitOfWork

Design for `feat(common): TransactionBehavior over IUnitOfWork` (Appendix C,
PR-09; depends on PR-04 and PR-08). Written before implementation and frozen at
write time — where this document and the blueprint disagree, the blueprint
wins.

The row commits the §6.3 behaviour and three tests: `SaveChanges` is called
once on success and never on failure; a handler that writes through
`ExecuteRawAsync` and then returns `Result.Failure` leaves no row; and queries
never open a transaction. CLAUDE.md's phase note adds one thing the row does
not list, decided on PR #15: `ChangeTracker.Clear()` at the top of every retry
attempt in `EfUnitOfWork`, so a transient fault cannot double-apply a tracked
mutation.

## 1. The behaviour, verbatim from §6.3

`TransactionBehavior<TCommand, TResult>` lands in `Common.Application` exactly
as §6.3 prints it — the chapter says "**This is the whole behaviour.** Nothing
below adds to it", and the one rule that matters makes any divergence a drift.
Constructor takes `IUnitOfWork` and `IDomainEventDispatcher`; the constraint is
`where TCommand : ICommand<TResult>`, which is what keeps queries out without
the behaviour checking.

The flow, in order: pass through when `HasActiveTransaction` (a nested dispatch
must not open a second unit); otherwise `ExecuteAsync` wraps `next()`, a failed
`Result` returns before anything else happens, domain events are dispatched,
`ModifiedAggregateCount > 1` throws `InvariantViolationException` — after
dispatch, so a legitimate single-root command's staged rows are not
miscounted — and `SaveChangesAsync` runs last.

**`Common.Application` still does not reference `Common.Domain`.** The
behaviour reads `ModifiedAggregateCount` as an `int` and calls
`DispatchAsync(CancellationToken)`; neither signature names a domain type. The
csproj comment, CLAUDE.md and the `IUnitOfWork` remarks all already say this,
because a review corrected the opposite claim once.

`InvariantViolationException` is a new public sealed exception in
`Common.Application`, message-only constructor. Appendix D.5 already lists it
("thrown when a command modifies more than one aggregate root"), so the
inventory needs no change.

## 2. `IDomainEventDispatcher` arrives as an interface, implemented by nothing real

The behaviour cannot compile without the port, so §7.5's interface comes
forward: `IDomainEventDispatcher` in `Common.Application`, one member,
`Task DispatchAsync(CancellationToken ct)`. The interface is copied from §7.5
verbatim, XML doc included. What does **not** come forward is everything §7.5
builds it from — `IDomainEventCollector`, `IProjectionRegistry`, the mapper,
the publisher, and the real `DomainEventDispatcher` all arrive with the outbox
(PR-14), and `IDomainEventCollector` is the type that finally draws the
Application → Domain edge.

Catalog therefore needs a registration or the first resolved
`TransactionBehavior<,>` throws. The truthful implementation today is one that
does nothing: `Catalog.Domain` has no aggregate until PR-10, so no domain event
can be raised, and there is no outbox to stage into until PR-14. So
`Catalog.Infrastructure` gains an `internal sealed class
NullDomainEventDispatcher : IDomainEventDispatcher` returning
`Task.CompletedTask`, registered in `AddCatalogInfrastructure` beside
`EfUnitOfWork` — the same side of the boundary its replacement will live on,
and explicitly rather than scanned, because §7.5 registers the real one
explicitly too. Its comment names both PRs: PR-10 is when dropped events become
possible and PR-14 is when this class is deleted.

Not in `PluggableInterfaces.All` — that list is the §6.2 convention scan, and
this port is registered explicitly, like `IUnitOfWork`. The three entries that
list is waiting for are handler-shaped interfaces; this is not one of them.

Not a no-op in `Common.Application` either: a building block that ships a
do-nothing implementation beside a port invites every service to keep it, and
the null object is only truthful while a service has no events. It is a
Catalog-local, deliberately temporary fact, so it lives in Catalog.

## 3. Registration — third of four, and the order argument

`AddCatalogApplication` gains one line after `ValidationBehavior`:

```csharp
services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));
```

Registration order is pipeline order (§6.3): Logging, Validation, Transaction.
`IdempotencyBehavior` slots **between** Validation and Transaction when its PR
builds it — the comment above the block already carries the "not scanned"
argument and is updated from "two of four" to "three of four", saying where the
missing one goes so the insertion point survives the wait.

## 4. `ChangeTracker.Clear()` — every attempt starts from committed state

`EfUnitOfWork.ExecuteAsync` retries its whole delegate through
`CreateExecutionStrategy`, and `db` is the same scoped `CatalogDbContext` on
every attempt. EF does not reset the change tracker when a transaction rolls
back, so after a transient fault mid-handler, attempt 2's load returns attempt
1's tracked, already-mutated instance from the identity map, the domain method
runs again, and one `SaveChanges` commits the mutation twice — with the staged
outbox rows surviving the same way. The fix is one line at the top of each
attempt, before `BeginTransactionAsync`:

```csharp
db.ChangeTracker.Clear();
```

**Not** a fresh `DbContext` per attempt: that moves retry out of `IUnitOfWork`
and up into the dispatcher, and destroys the property that makes §6.3's
behaviour reviewable — that it depends on nothing but the port.

§6.3's `EfUnitOfWork` sample gains the same line in the same change, and a
sentence lands beside the existing "may therefore run **more than once**"
note: that note covers side effects *outside* the transaction, not in-memory
state surviving inside it, and the clear is what closes the second gap.

**What this does not fix — the commit-acknowledgement race — stays open on
purpose.** If `CommitAsync` succeeds on the server and the connection drops
before the ack, the strategy retries work that is already durable, and no
in-process tidying can tell those states apart. Closing it needs an idempotency
marker written inside the transaction; that decision belongs with PR-14, where
a double-apply becomes externally visible. This spec records the deferral so
the PR body can point at it.

## 5. Tests

**Unit suite** — `TransactionBehaviorTests` in `Common.Application.Tests`, over
a recording `FakeUnitOfWork` and `FakeDomainEventDispatcher` (both scoped, in
the shared test-types file per house pattern):

- A successful command enters the unit, dispatches, saves — once, in that
  order. The order matters twice: dispatch before save is what puts outbox rows
  into the same save, and dispatch before the count read is §6.3's own comment.
- A failed `Result` skips dispatch and save, and comes back unchanged.
- A command whose result is not a `Result` commits normally — the failure guard
  is a pattern match, and a `string`-returning command must not trip it.
- `HasActiveTransaction` true → straight to `next()`, no second unit, no
  dispatch, no save.
- `ModifiedAggregateCount` of 2 → `InvariantViolationException` naming the
  command type and the count, and nothing saved.
- A query dispatched through a provider holding the real `TransactionBehavior`
  never touches the unit of work — Appendix C's third test, on the real type
  rather than `CommandOnlyBehavior`'s stand-in.

**Registration suite** — `Catalog.Application.Tests`: the descriptor-order test
goes from two behaviours to three.

**Container suite** — `Catalog.Api.Tests`, Docker required, extending
`DatabaseSmokeTests`' probe pattern:

- Appendix C's second test, end to end: the real `TransactionBehavior` over the
  scope's real `IUnitOfWork` and registered `IDomainEventDispatcher`, whose
  `next` writes the probe row through `ExecuteRawAsync` and returns
  `Result.Failure` — no row survives. PR-08 proved the `EfUnitOfWork` half; this
  proves the behaviour drives it.
- The retry test: a `DbContextOptions` swap gives the context a
  `SqlServerRetryingExecutionStrategy` subclass that also retries a test marker
  exception; the operation writes the probe row and throws the marker on
  attempt 1. Assert two attempts, **one** row — attempt 1's write rolled back,
  attempt 2's committed once. This is the transient-fault/no-double-apply test
  for every path observable today, and it is what proves the strategy actually
  re-runs the delegate rather than surfacing the fault.

**A limitation, stated rather than hidden:** the tracked-entity double-apply —
attempt 2 reading attempt 1's mutated instance from the identity map — cannot
be asserted until an entity type exists, and `CatalogDbContext` has none until
PR-10. The `Clear()` line ships now because the sample must not teach the
defect; the identity-map assertion is recorded here as PR-10's to add, beside
its first aggregate. CLAUDE.md's phase note is amended to carry that handover.

`Catalog.Api.Tests` takes a `PackageReference` to
`Microsoft.EntityFrameworkCore.SqlServer` for the strategy subclass — already
pinned centrally and already in Appendix B, so no register change.

## 6. What this PR deliberately does not do

- No `IdempotencyBehavior` (§8.5) — its PR builds it, and the registration
  comment holds its seat.
- No real domain-event machinery — collector, registry, mapper, publisher and
  the Application → Domain edge all wait for PR-14.
- No repository, no aggregate, no endpoint — PR-10.
- No fix for the commit-acknowledgement race — PR-14, argued in §4 above.
