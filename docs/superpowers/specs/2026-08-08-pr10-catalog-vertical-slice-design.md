# PR-10 — Catalog's first vertical slice

Design for `feat(catalog): first vertical slice — command, query, cursor
pagination` (Appendix C, PR-10; depends on PR-07 through PR-09). Written before
implementation and frozen at write time — where this document and the blueprint
disagree, the blueprint wins.

The row commits: one aggregate, one command, one cursor-paginated query, the
service's Dockerfile and Compose block, the `docker-compose.infra-only.yml`
override, and a README stating that the endpoints are deliberately
unauthenticated until PR-16. It is the first containerised service and the
template PR-11's scaffold copies, so every shape chosen here is chosen for six
services.

## 1. What the blueprint fixes, and what it leaves open

§3 and §5 bind the pattern and name no Catalog types. Catalog owns Product,
Category and Price (§3.2), publishes `ProductPublished`, `PriceChanged` and
`ProductDiscontinued`, and **accepts no message-borne commands** — PR-10's
"one command" is an HTTP command. The only specification of Product state
anywhere is Appendix D.5's `ProductPublished` payload: `ProductId`, `Name`,
`ThumbnailUrl`, `Currency`, `Amount`. §3.1 puts SKU explicitly on Inventory's
side.

So the aggregate is **`Product`** (the name PR-08's spec already assumed:
"the line PR-10's `IEntityTypeConfiguration<Product>` is found by"), and its
first slice is the smallest one the contract payload describes:

- `ProductId` — §5.2's readonly record struct over `Guid.CreateVersion7()`,
  in `Catalog.Domain.Products`.
- `Money` — §5.3's value object, copied into `Catalog.Domain.Common`. Not
  shared with Ordering's future copy: §4 forbids a shared kernel, and §3.1's
  whole argument is that a term must not share a class across contexts.
- `Product` — `sealed`, `AggregateRoot<ProductId>`, members `Name`,
  `ThumbnailUrl` (nullable — a marketing object may lack an image), `Price`
  (`Money`) and `PublishedAt`, all private-set. A private parameterless
  constructor for EF, a private assigning constructor, and one factory:
  `Product.Publish(string name, string? thumbnailUrl, Money price,
  DateTimeOffset now)`. The clock is a parameter (§5.4); guards throw
  `DomainException` (§5.7 — domain exceptions signal bugs, the validator
  handles user input).
- `ProductPublishedDomainEvent(ProductId ProductId, string Name,
  string? ThumbnailUrl, Money Price, DateTimeOffset OccurredAt)` — raised in
  the factory, carrying everything the §9.1 contract needs (§5.5's rule), with
  the `*DomainEvent` suffix that keeps it a different type from the contract.
- `IProductRepository` — in Domain, implemented in Infrastructure (§5.6).
  **`Add` only.** §5.6's shape has `GetAsync`, but nothing in this PR loads an
  aggregate to change it, and an unused member is the same untruth as an
  unused project reference. `GetAsync` arrives with the first command that
  loads — price change or discontinue, on their PRs.

No Category, no Status, no discontinue lifecycle: one aggregate, one command,
and §5.7's "split by invariant" says Price-inside-Product is right while the
only invariant is "a product has a price".

**Raised events are dropped until PR-14**, and this PR is where that becomes
true rather than vacuous: `NullDomainEventDispatcher` now swallows a real
`ProductPublishedDomainEvent` on every publish. The domain model raises it
anyway — the aggregate must not teach the defect of not raising (§5.5), the
unit test pins the payload, and PR-14's outbox picks it up without touching
`Product`. The PR body and CLAUDE.md carry the statement.

`DomainException` does not exist yet and lands in **`Common.Domain`**. It is
Appendix D.2 vocabulary alongside `Entity<TId>` and `AggregateRoot<TId>` — a
building block, not a domain concept, so the no-shared-kernel rule does not
apply to it. Message-only constructor, same shape as
`InvariantViolationException`.

## 2. The command — PublishProduct

Namespace `Catalog.Application.Products.PublishProduct` (§6.4's feature-folder
form):

- `PublishProductCommand(string Name, string? ThumbnailUrl, decimal Amount,
  string Currency) : ICommand<Result<Guid>>`. No `CommandId` and no
  `IIdempotentCommand`: §8.5's behaviour does not exist, and §6.4 itself warns
  that the field without the interface is unprotected — the field joins with
  the interface, in the PR that builds the behaviour.
- `PublishProductValidator : AbstractValidator<PublishProductCommand>` —
  `Name` non-empty, max 200; `ThumbnailUrl` max 400 when present; `Amount`
  non-negative; `Currency` length 3.
- `PublishProductHandler(IProductRepository products, TimeProvider clock)` —
  `Money.Of`, `Product.Publish`, `products.Add`, `Result.Success(id.Value)`.
  Thin by §6.4's design; no `SaveChanges` (the transaction behaviour owns it),
  no metric (§6.4's comment: counted on commit, not on attempt).

There is deliberately **no `CatalogErrors` catalogue yet**: no handler in this
PR can refuse, so an error catalogue would be an empty claim. It arrives with
the first refusable command, exactly as the repository's `GetAsync` does.

Validators are registered by `AddValidatorsFromAssemblyContaining
<PublishProductValidator>()` — §4.2's sample line, which is the first consumer
of `FluentValidation.DependencyInjectionExtensions`. New package: exact pin
beside `FluentValidation`, and its backticked identity reaches Appendix B in
the same change or the licence gate fails the build.

## 3. The query — GetProducts, and the three types it forces into Common.Application

Namespace `Catalog.Application.Products.GetProducts`:

- `GetProductsQuery(string? Cursor, int Limit) :
  IQuery<CursorPage<ProductSummaryDto>>`
- `ProductSummaryDto(Guid ProductId, string Name, string? ThumbnailUrl,
  decimal Amount, string Currency, DateTimeOffset PublishedAt)`
- `GetProductsHandler(IDbConnectionFactory connections)` — §6.5's shape
  verbatim: Dapper, `const string Sql` with the keyset predicate over
  `(PublishedAt DESC, Id DESC)`, `Math.Clamp(query.Limit, 1, 100)`,
  `TOP (@Take)` with `limit + 1`, `Cursor.Decode`/`Cursor.Encode`, never
  `SELECT *`.

Three types §6.5 references land in `Common.Application`, none naming a domain
type (the `Common.Application ↛ Common.Domain` state holds):

- `CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor)` — the envelope
  (Appendix D.5).
- `Cursor` — the opaque codec. `Encode(DateTimeOffset sortKey, Guid id)` is
  Base64Url over `"{UtcTicks}:{Guid:N}"`; `Decode(string?)` returns
  `(DateTimeOffset SortKey, Guid Id)?` and answers **null for null and for
  anything unreadable**. The cursor is opaque by §6.5's decision; a client
  that edits one gets the first page back, not an error oracle and not a 500.
- `IDbConnectionFactory` — one member, `IDbConnection Create()`. The port
  §6.5's handlers inject; BCL types only. Its implementation is
  service-local: `SqlConnectionFactory` in `Catalog.Infrastructure` over the
  runtime connection string, registered as the singleton §4.2's sample shows.
  `Common.Infrastructure` does not exist, and inventing it early is the thing
  CLAUDE.md forbids; when its PR arrives, moving the two-line class is that
  PR's business. Direct use of `Microsoft.Data.SqlClient` means an explicit
  reference: pinned at 6.1.1 (what EF 10 resolves), registered in Appendix B.

Dapper joins `Catalog.Application` as a direct reference — already pinned,
already in Appendix B for exactly this role.

## 4. Persistence

- `ProductConfiguration : IEntityTypeConfiguration<Product>`, internal sealed,
  §7.2's sample translated: `ToTable("Products", "catalog")`, key conversion +
  `ValueGeneratedNever()`, `Name` max 200, `Price` as `ComplexProperty` with
  `PriceAmount`/`PriceCurrency` columns, `Version` as rowversion,
  `Ignore(DomainEvents)`, and an index on `(PublishedAt, Id)` — the exact seek
  the query's keyset predicate performs.
- `ProductRepository(CatalogDbContext db) : IProductRepository`, internal
  sealed, `Add` only. Registered explicitly beside `EfUnitOfWork`.
- Migration `AddProducts` via the pinned `dotnet ef`, migrator as startup
  project. The migration `.cs` is rewritten into house style (IDE0161 makes
  the generated block-scoped namespace a build error); the `.Designer.cs` and
  the snapshot are left exactly as the tool wrote them — PR-08's rule, the
  snapshot being the input to the next `migrations add`.
- `CatalogDbContext` is untouched: `ApplyConfigurationsFromAssembly` was
  landed by PR-08 precisely so this PR adds a configuration and no discovery
  line.

## 5. Endpoints

`Catalog.Api.Endpoints.ProductEndpoints` — the namespace the §4.2 gate selects
on, the per-aggregate static class ADR-015 requires:

- Group `MapGroup("/v1/catalog/products").WithTags("Products")` — §10.2's one
  shape: the gateway strips `/api` from `/api/v1/catalog/{**catch-all}`, so
  the service maps `/v1/catalog/...`, and PR-17's config test will assert the
  stripped path against this group.
- `POST /` binds `PublishProductCommand` from the body directly — no separate
  request record while the wire shape and the command are identical; §11.4's
  split earns its place when they diverge (the enum parse there, the
  idempotency key here later). Returns `result.ToHttpResult()` — 200 with the
  id.
- `GET /` binds `string? cursor, int limit = 20`, dispatches, returns
  `Results.Ok(page)` — the query's result is `CursorPage`, not `Result`, so
  `ToHttpResult` has no part (§6.2's comment states this).
- **No `RequireAuthorization`** — deliberately unauthenticated, stated in
  `deploy/compose/README.md`, closed by PR-16. Note stated in the PR body too:
  the gateway's `catalog-public` route as specified in §10.2 admits GETs only,
  so the POST is unreachable through the gateway even after PR-17.

`Program.cs` gains `app.MapProductEndpoints();` and its "endpoints at PR-10"
comment retires. The gate stops being vacuous: `ProductEndpoints` is the first
type it actually judges.

## 6. Catalog.TestSupport arrives — §4.1's condition is now met

§4.1: "referenced by the two above, which cannot reference each other".
PR-08 gave the fixture one consumer and CLAUDE.md said the second candidate
was PR-16's test auth. It turned out to be this PR: §12.1 homes the handler
tests in `Catalog.Application.Tests` with real containers, and the endpoint
tests stay in `Catalog.Api.Tests` — two consumers, one fixture.

- New library `tests/Catalog.TestSupport` (not a test project — no runner
  packages).
- `SqlServerFixture` and `CatalogApiFactory` move there, namespace
  `Catalog.TestSupport`. The fixture is renamed **`ServiceFixture`** — the
  name §4.1 and §12.4 give the thing that lives in TestSupport, and the name
  PR-11's scaffold copies. It keeps its migrator-driving members and gains
  `ResetAsync()` — Respawn over the `catalog` schema, §12.4's reset idiom —
  and stays SQL-only: Redis and RabbitMQ containers join with the PRs whose
  code touches them.
- Each consuming test project declares its own
  `[CollectionDefinition(nameof(IntegrationCollection))]` (§12.4 — collections
  are per assembly). `DatabaseSmokeTests` moves from `IClassFixture` onto the
  collection so the Api assembly starts one container set, not two.

## 7. Tests

**Domain** (`Catalog.Domain.Tests`, no doubles — §12.3): `Publish` sets every
member and raises the event with the full payload; name guard throws
`DomainException`; `Money.Of` validation, rounding, `Zero`, operators,
currency-mismatch guard; the typed-ID compile-time distinction needs no new
test (Common.Domain.Tests owns the pattern). The gate re-anchors from
`AssemblyMarker` — deleted, its comment having named this PR — to
`typeof(Product)`.

**Application unit** (`Catalog.Application.Tests`, no fixture): validator
accepts the valid command and rejects each invalid field. Registration tests
extend: validators registered, `IProductRepository` and `IDbConnectionFactory`
asserted on the Infrastructure surface.

**Application container** (`Catalog.Application.Tests`, collection): the
dispatched `PublishProductCommand` persists a row the DbContext can read back
(the real pipeline, the real transaction behaviour, the real database); the
dispatched `GetProductsQuery` — seeded through real aggregates, §12.4's
seeding rule — clamps `limit`, pages by `limit + 1` without a count, breaks
`PublishedAt` ties by id (§6.5's tiebreaker), round-trips the cursor to the
second page, and ends with a null `NextCursor`. §12 prescribes no pagination
test; §6.5 and ADR-016 assert four behaviours, and each gets one here.

**Cursor codec** (`Common.Application.Tests`): encode/decode round-trip,
null → null, unreadable → null.

**API contract** (`Catalog.Api.Tests`, collection): POST returns 200 and a
Guid body (which also pins the `ToHttpResult<T>` overload — the §10.5 trap);
invalid POST returns 400 ProblemDetails with field-keyed errors (the
`ValidationBehavior` path over the wire); GET returns the page shape; GET
with the returned cursor pages forward. Existing suites keep passing;
`HostSmokeTests` is untouched apart from the factory's new namespace.

## 8. Containers

Two Dockerfiles, §15.2 verbatim with Catalog names: the API on
`aspnet:10.0-noble-chiseled` (EXPOSE 8080, `USER $APP_UID`), the migrator on
`runtime:10.0-noble-chiseled` (no EXPOSE, no BUILD_CONFIGURATION arg). A root
`.dockerignore` (`.git`, `**/bin`, `**/obj`) keeps host build debris out of
the context — the blueprint draws no such file, but a stale `obj/` copied into
a Linux build is a restore failure §15.2's COPY lines would otherwise invite.

Compose gains §14.1's pair: `catalog-migrator` (`restart: "no"`, gated on
`sql: service_healthy`, `ConnectionStrings__CatalogMigrator` only) and
`catalog-api` (gated on the migrator's `service_completed_successfully`,
`ConnectionStrings__Catalog` only, `ports: [ "5102:8080" ]` — the next slot
after ordering's 5101). Deliberate divergences from the printed ordering
block, each argued in a comment:

- **Inline defaults on the connection strings**, nesting
  `${SQL_PASSWORD:-…}` — `.env.example`'s stated contract is that every
  variable has a working default inline, and §14.1's bare `${…}` breaks
  `docker compose up` on a clean checkout. `.env.example` documents the two
  new variables.
- **No Redis, RabbitMQ or `Identity__Authority` keys** — Catalog reads none
  of them until PR-13/14/16, and an env var nothing reads is the container
  form of an unused registration. Each joins with the PR whose code reads it.

`docker-compose.infra-only.yml` is authored: it assigns the two application
services a `profiles:` entry, which removes them from the default `up` when
the override is applied — infrastructure in containers, the service under a
debugger on the host. §14.1 gains the override's illustrative form (ordering
names, matching the chapter's own elision of catalog) where it currently says
"the override below" and shows nothing.

The compose smoke workflow now builds both images inside `up --wait`;
its header comment says so and `timeout-minutes` rises to 25. The migrator
exiting 0 under `--wait` is verified locally before the PR relies on it.

`deploy/compose/README.md` gains the application-services section: the 5102
port row, the two connection-string variables, and the **unauthenticated
endpoints statement** Appendix C requires — this README is the running
platform's surface inventory, which is where the person who can reach the
endpoints is reading.

## 9. Blueprint reconciliation carried in this PR

- **§7.1's callout contradicts §14.2, and §14.2 is the side the code is on.**
  §7.1: "Compose seeds both logins from the same script the cloud path uses…
  exercised on every developer machine". §14.2: "locally there is one `sa`
  account, exactly as §12.4's fixture notes… The *key* still differs", and it
  claims Compose already does this. No seeding mechanism exists in §14.1's
  own printed file, the shipped fixture collapses the identities and says so,
  and the migrator must create the database — which `db_ddladmin` on a
  not-yet-existing database cannot. The callout is rewritten: the two
  identities are a cloud-side control, the key split is what every local
  environment exercises (and the shipped
  `Migrator_fails_when_only_the_runtime_connection_string_is_set` proves),
  and the seeding script lives below the divide. PR-08's frozen plan line
  saying the seed script is PR-10's home stays as written — frozen record.
- **§14.1's infra-only paragraph** gains the override's content (illustrative
  ordering names) in place of a reference to YAML it never showed.
- **CLAUDE.md**: phase note moves to PR-10 landed / PR-11 next; the
  TestSupport annotation ("PR-16's test auth is the other candidate") records
  that PR-10 was the second consumer; the tree annotations pick up the new
  Common.Application types, `DomainException`, TestSupport, and the two new
  package pins; the events-dropped-until-PR-14 note flips from prospective to
  live.

## 10. What this PR deliberately does not do

- No auth, no `Identity__Authority` — PR-16.
- No `IIdempotentCommand`, no `CommandId` — §8.5's PR, seat held.
- No `GetProductDetail`, no HybridCache — §8.2 is PR-13's worked example.
- No integration events, no outbox, no `CatalogErrors`, no repository
  `GetAsync` — each with the PR whose code first needs it.
- No gateway route change — §10.2's `catalog-public` (GET-only) is PR-17's.
- No Testcontainers category — PR-22's named deliverable.
