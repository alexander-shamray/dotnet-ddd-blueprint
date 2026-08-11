# PR-15 — Implementation plan

Derived from `docs/superpowers/specs/2026-08-11-pr15-contracts-inbox-and-retention-design.md`,
frozen at write time. Eight stages; each leaves `dotnet build Platform.slnx`
green, and every stage that adds behaviour writes its test first (§12).

## Stage 1 — the contract vocabulary

`src/BuildingBlocks/Common.Contracts`, five versioned namespaces. No packages,
no project references — the two absences PR-14 argued.

| Namespace | Types |
|---|---|
| `Catalog.V1` | `ProductPublished` (exists), `PriceChanged`, `ProductDiscontinued` |
| `Ordering.V1` | `OrderPlaced` + `PlacedLine`, `OrderConfirmed` + `ConfirmedLine` + `ShippingAddressV1`, `OrderCancelled`; `CancelOrder`, `ConfirmOrder`, `MarkOrderShipped`, `FlagOrderForReview`; `CancelReasons`, `ReviewReasons` |
| `Inventory.V1` | `StockReserved`, `StockReservationFailed`, `StockReleased`, `StockLevelChanged`; `ReserveStock` + `StockLine`, `ReleaseStock` |
| `Payments.V1` | `PaymentAuthorised`, `PaymentDeclined`, `PaymentRefunded`; `AuthorisePayment` |
| `Shipping.V1` | `ShipmentDispatched`, `ShipmentDelivered` |

Events implement `IIntegrationEvent` and write the envelope out; commands
deliberately do not (§9.1's callout). Each contract owns its line and address
types (§9.1).

## Stage 2 — `Platform.IntegrationTests` and §12.6

New project in `tests/`, referencing `Common.Contracts` only, added to
`Platform.slnx`. `ContractSamples.Create(Type)` is a hand-written dictionary —
one entry per contract, throwing by name for a type it does not hold.
`ContractTests` carries §12.6's three: no domain reference, versioned
namespace, round-trip. Plus one this repo owes its own rule: every concrete
contract has a sample, which is what makes the other three non-vacuous.

**Red first**: the suite is written against the stage-1 types and must fail on a
deliberately unsampled contract before the sample is added.

## Stage 3 — `SqlSchema` and `InboxTable`

Extract `OutboxTable`'s regex and bracket-quoting into an internal
`SqlSchema.Qualify(schema, table, paramName)`; `OutboxTable` calls it, and
`InboxTable` is its sibling. `OutboxTable`'s public shape is unchanged, so
PR-14's tests still hold — they are the regression net for the extraction.

## Stage 4 — the inbox

- `Common.Infrastructure/Inbox/InboxMessage.cs` — §9.5's entity, three members.
- `Common.Infrastructure/Inbox/InboxFilter.cs` — `IFilter<ConsumeContext<T>>`
  over `DbContext`, `db.Set<InboxMessage>()`. Handler first, row second.
- `Catalog.Infrastructure/Persistence/InboxMessageConfiguration.cs` — composite
  key, `varchar(300)` endpoint, the `OutboxMessageConfiguration` shape.
- Migration `AddInbox`, hand-dressed, machine files untouched.
- `AddCatalogInfrastructure`: the `DbContext` alias with its `GetRequiredService`
  delegate, and a test that both resolutions are one instance.

## Stage 5 — the consumers

- `Common.Infrastructure/Messaging/IntegrationEventConsumer.cs` — §9.4's
  adapter, `MessagingMetrics.Delivered`, the empty-handlers throw.
- `Common.Application/ICommandMessageMapper.cs` and
  `ContractMappingException.cs`.
- `Common.Infrastructure/Messaging/CommandConsumer.cs` — §9.4's mirror,
  `Rejected` on a domain failure, `LoggerMessage.Define` for CA1848.
- `MessagingMetrics` gains `Delivered` and `Rejected`; the class is now §13.3's.
- `PluggableInterfaces.All` reaches five.

Tests drive the harness (PR-13's `AddMassTransitTestHarness` swap) over
handlers, a wire type and a mapper in `Catalog.TestSupport`.

## Stage 6 — the retention purge

`RetentionPolicy` (windows, batch size, interval) and `RetentionPurgeService`.
Batched delete loops, outbox predicate carries `ProcessedAt IS NOT NULL`.
Registered in `AddCatalogInfrastructure` after the dispatcher, so it stops
first — the shutdown-ordering argument the dispatcher's registration already
makes, one line down.

Container tests: processed rows past the window go, abandoned rows stay, rows
inside the window stay, inbox rows go on age alone, and a pass larger than one
batch drains.

## Stage 7 — the scaffold

`tools/new-service/new_service.py` classifies every file under
`src/Services/Catalog` and `tests/Catalog.*`; each new file is a decision it
forces. The inbox configuration and the `AddInbox` migration are **template**
(a service without the table logs a purge failure every pass); the test-local
contracts, handlers and mappers in `Catalog.TestSupport` are **slice** where
they name Catalog's aggregate and template where they do not. Reconcile in this
change and run `py -3.12 -m unittest`.

## Stage 8 — reconcile the prose

The one rule that matters, in both directions:

- §9.4, §9.5, §9.8 — the three namespaces Decision 3 moves, and
  `InboxFilter<T>`'s parameter.
- §9.5 — the purge's home and the registered policy.
- Appendix D.3, D.4, D.5 — `InboxTable`, `SqlSchema`, `RetentionPolicy`,
  `RetentionPurgeService`, `ContractSamples`, and the four contracts whose
  members Decision 2 settles.
- Appendix B — any package that reached `Directory.Packages.props`.
- `CLAUDE.md` — the phase section, both suite counts, the tree.
- `docs/roadmap.md` is **not** touched: it is a schedule, and Appendix C wins.

## Verification

```bash
dotnet build Platform.slnx
dotnet test  Platform.slnx            # Docker
cd tools/new-service     && py -3.12 -m unittest
cd .github/licence-gate  && py -3.12 -m unittest
```

then the scaffolded-service build, which is the only thing that catches a
Catalog test using a helper the scaffold removes:

```bash
python tools/new-service/new_service.py Ordering --port 5103
dotnet build tests/Ordering.Api.Tests/Ordering.Api.Tests.csproj
rm -rf src/Services/Ordering tests/Ordering.*
git checkout -- Platform.slnx deploy/compose/
```
