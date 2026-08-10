# PR-14 Transactional Outbox Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** §7.5's flow end to end for the template service — collect, map
through the §9.3 allow-list, stage `Broker` and `Local` rows in the command's
transaction, deliver them after commit through §9.4's claim-and-deliver
dispatcher — with `MessageTypeMap` and `OutboxJson` landing before the first
row exists in the columns they define.

**Architecture:** Per the frozen spec
(`specs/2026-08-11-pr14-transactional-outbox-design.md`). Ten decisions, the
load-bearing ones being: `Common.Contracts` is created here with the envelope
interface and Catalog's one contract; the mapper interface is an Application
port; `Common.Application → Common.Domain` is drawn by `IDomainEventCollector`;
`Common.Infrastructure` takes its first project references but no EF Core;
the outbox schema is a registered `OutboxTable` rather than a SQL literal;
Catalog registers no projection handler and the `Local` lane is proven by
`Catalog.TestSupport` types; `Money` gains a public validating constructor
because `System.Text.Json` rebuilds it as `default` otherwise.

**Tech Stack:** .NET 10 / C# 14, EF Core 10, Dapper 2.1.66, MassTransit 8.5.3,
System.Text.Json, xunit.v3 + Shouldly + Testcontainers.

## Global Constraints

- House C# dialect (CLAUDE.md): explicit types unless the RHS names the type;
  spread over `.ToArray()`; file-scoped namespaces; blank line after
  `namespace X;`; no aligned `=`; four-space indent, CRLF, newline at EOF;
  British spelling in prose, identifiers keep their real spelling; no `#pragma`.
- `TreatWarningsAsErrors` — a warning is a stop. CA1848 means every log call
  on a hot path is a `LoggerMessage.Define` delegate.
- Central package management — no `Version=` on a `PackageReference`; a new
  package needs an Appendix B row in the same change.
- The migration is hand-dressed into house style; the `.Designer.cs` and the
  snapshot are left exactly as the tool wrote them.
- A Catalog change that breaks `tools/new-service` is reconciled in the same
  change; `py -3.12 -m unittest` is the gate.
- Docker required: `dotnet test` needs it for three projects.

---

### Task 1: `Common.Contracts` — the envelope and Catalog's first contract

- [ ] Create `src/BuildingBlocks/Common.Contracts/Common.Contracts.csproj` — no
      packages, no project references. It is primitives and interfaces.
- [ ] `IIntegrationEvent.cs` — §9.1's three envelope members, with the
      single-identity callout carried into the doc comment.
- [ ] `Catalog/V1/ProductPublished.cs` — the envelope written out rather than
      inherited (§9.1: a shared base is a shared versioning fate), plus
      `ProductId`, `Name`, `ThumbnailUrl`, `Amount`, `Currency`.
- [ ] Add the project to `Platform.slnx`.

### Task 2: `Common.Application` — the §7.5 and §9.3 ports

- [ ] `Common.Application.csproj`: add the `Common.Domain` project reference
      and rewrite the comment that predicted it into the past tense, keeping
      the `ModifiedAggregateCount` argument.
- [ ] `IDomainEventCollector.cs`, `IProjectionHandler.cs`,
      `IProjectionRegistry.cs`, `OutboxLane.cs`,
      `IIntegrationEventPublisher.cs`, `IIntegrationEventMapper.cs`.
- [ ] `ProjectionRegistry.cs` and `DomainEventDispatcher.cs`, both internal.
      The registry is scoped with a static `ConcurrentDictionary` cache —
      §7.5's callout: scoped because handlers are, and the cache memoises the
      container's shape rather than any instance.
- [ ] `DependencyInjection.cs`: `AddDomainEventDispatcher()` in the existing
      `extension(IServiceCollection)` block, registering both internal types.
- [ ] `PluggableInterfaces.All` gains `IProjectionHandler<>`; the comment
      drops it from the "three more join later" list.
- [ ] `IUnitOfWork` and `IDomainEventDispatcher` remarks: the edge is drawn.
- [ ] Tests first — `Common.Application.Tests/DomainEventDispatcherTests.cs`:
      no events → nothing staged; mapped events → `Broker` rows; an event with
      a registered handler → a `Local` row; one without → none; a registered
      mapper that throws → the exception propagates. Fakes for the collector,
      mapper and publisher live beside the existing `FakeUnitOfWork`.
- [ ] `RegistrationTests` covers the new pluggable interface.

### Task 3: `Common.Infrastructure/Outbox` — the persisted format

- [ ] csproj: project references to `Common.Application`, `Common.Domain` and
      `Common.Contracts`; packages `MassTransit`, `Dapper`,
      `Microsoft.Extensions.Logging.Abstractions`. Replace the "no project
      references" comment with the edges and why they arrived.
- [ ] `OutboxJson.cs` — one `JsonSerializerOptions`, the three explicit
      settings §9.4 argues for.
- [ ] `MessageTypeSource.cs` and `MessageTypeMap.cs` — `FullName` not
      `AssemblyQualifiedName`; both directions throw with the message §9.4
      writes; duplicate names fail the host.
- [ ] `OutboxMessage.cs` and `OutboxClaim.cs` — `Stage` takes its identity
      from the envelope where there is one.
- [ ] `OutboxTable.cs` — the schema, shape-checked.
- [ ] `Messaging/MessagingMetrics.cs` — `Projected` only, with the two that
      join at PR-15 named in a comment.
- [ ] `ProjectionInvoker.cs` — cached generic invoker, sequential handlers,
      throws on none, records `Projected` after the handlers.
- [ ] `OutboxDispatcher.cs` — `BackgroundService`, `PeriodicTimer(500ms)`,
      public `ProcessBatchAsync`, per-row accounting, `LoggerMessage.Define`
      delegates.
- [ ] Tests: `Common.Infrastructure.Tests/MessageTypeMapTests.cs` and
      `OutboxMessageTests.cs` (unit half, no containers) — `Stage` copies the
      envelope's `MessageId` **and** `CorrelationId`, and mints both for a
      domain event; `NameOf` throws for an unmapped type; `Resolve` throws for
      an unknown name; a duplicate `FullName` fails construction;
      `StageableDomainEvents` excludes contracts; `OutboxTable` refuses a
      schema that is not an identifier.

### Task 4: `Money`'s constructor, and the round-trip rule

- [ ] Move `Of`'s guards and rounding into a public constructor; `Of` and both
      operators delegate to it. Amend the type's remarks.
- [ ] `MoneyTests`: the constructor validates the same way `Of` does, and a
      round trip through `OutboxJson.Options` returns an equal value.
- [ ] Amend §5.3 in the blueprint.

### Task 5: Catalog's mapper — the allow-list

- [ ] `Catalog.Application.csproj`: reference `Common.Contracts`, replacing
      the comment that says the project does not exist.
- [ ] `Integration/CatalogIntegrationEventMapper.cs` — the static registry,
      one entry, `ToContract` decomposing `Money` into `Amount` and
      `Currency`.
- [ ] `DependencyInjection.cs`: register the mapper, call
      `AddDomainEventDispatcher()`, delete the `NullDomainEventDispatcher`
      registration.
- [ ] Delete `NullDomainEventDispatcher.cs`.
- [ ] Tests: `CatalogIntegrationEventMapperTests` — the registered event maps
      and carries every field; an unregistered `IDomainEvent` is skipped
      silently; the contract's `CorrelationId` is the product id.

### Task 6: Catalog's persistence — the table, the collector, the publisher

- [ ] `Persistence/OutboxMessageConfiguration.cs` — column types matching
      §9.4's DDL, `Lane` stored as a string, `MessageId` unique, the filtered
      index on unprocessed rows.
- [ ] `CatalogDbContext`: the `DbSet<OutboxMessage>`.
- [ ] `Persistence/EfDomainEventCollector.cs` — §7.5's collector, clearing as
      it collects.
- [ ] `Persistence/EfIntegrationEventPublisher.cs` — writes on the command's
      `DbContext`; one correlation id per scope, minted lazily, overridden by
      an integration event's envelope.
- [ ] `dotnet ef migrations add AddOutbox`; hand-dress the migration, leave
      the designer and snapshot alone.
- [ ] `DependencyInjection.cs`: collector, publisher, `OutboxTable`,
      `MessageTypeSource`, `MessageTypeMap`, `MessagingMetrics`, and
      `OutboxDispatcher` as a hosted service and as itself.

### Task 7: the fixture, the doubles and the container tests

- [ ] `Catalog.TestSupport/Outbox/` — `AlwaysThrows`, `NoOpEvent`,
      `UnhandledEvent` (`IDomainEvent`s), their handlers, and the `Poison`,
      `Healthy` and `LocalRowFor<T>` builders over the real `MessageTypeMap`.
- [ ] `CatalogApiFactory`: remove only the `OutboxDispatcher` hosted
      descriptor, re-register it as a singleton, add the TestSupport assembly
      to `MessageTypeSource` and scan it for projection handlers.
- [ ] `ServiceFixture`: `MessageTypes`, `ProcessOutboxBatchAsync`,
      `OutboxAsync`, `StageOutboxAsync`, `SetOutboxAttemptsAsync`,
      `ExpireOutboxLeasesAsync`.
- [ ] `Catalog.Application.Tests/PublishProductHandlerTests`: the aggregate
      row and the outbox row commit together; a failed command leaves neither;
      the `Broker` row carries the contract type and no `Local` row is staged.
- [ ] `Catalog.Application.Tests/OutboxSerialisationTests`:
      `Every_stageable_domain_event_round_trips_through_the_outbox_options`.
- [ ] `Catalog.Api.Tests/OutboxDispatcherTests`: a failing row does not block
      healthy rows; a row stops being claimed at the attempt cap; a `Local`
      row with no registered handler fails loudly; a `Broker` row reaches the
      broker.

### Task 8: reconcile the blueprint, the scaffold and the guidance

- [ ] §5.3, §7.5, §9.3, §9.4 per the spec's table; §4.1's tree gains
      `Common.Contracts`.
- [ ] Appendix C's PR-14 and PR-15 rows; Appendix D's new types; Appendix B if
      any package moved.
- [ ] `tools/new-service`: `COPIED` gains the template files, `OMITTED` the
      slice ones, `PATCHES` empties the mapper's registry and drops the
      `NullDomainEventDispatcher` entry. `py -3.12 -m unittest` green.
- [ ] `CLAUDE.md`: the phase section, the tree, the test counts, the standing
      facts that stop being true (raised events are no longer dropped).
- [ ] `dotnet build`, `dotnet test`, `/validate-blueprint`.
