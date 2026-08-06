# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this repo is

`dotnet-ddd-blueprint` is a monorepo for an ASP.NET Core microservices platform
built with DDD, CQRS and TDD. It is currently in its **documentation phase**:
the only artefact so far is the blueprint under `docs/backend-architecture/`.

**The C# solution will land in this repo.** The blueprint is the specification
for it, and Appendix C sequences that code into 26 pull requests starting with
`chore: solution structure, SDK pin, central package management, CI skeleton`.
Treat every chapter as a commitment the implementation will have to honour.

Both READMEs read **"Reference blueprint — adapt, don't copy wholesale. The C#
solution it specifies will be built in this repository"** — the blueprint serves
a visiting reader and this repo's own build at once.

Keep two different things apart:

- **Solution shape** — the projects §4.1 lays out: Catalog, Ordering, Inventory,
  Payments, Shipping, Notifications, plus the building blocks, gateway and BFF.
- **Service build order** — Appendix C.1, which is *not* the §4.1 listing
  order: **Notifications → Catalog → Ordering → Inventory and Payments →
  Shipping**. Notifications goes first precisely because it has no domain logic.

One thing is genuinely **undecided**: the READMEs call the e-commerce domain
"illustrative only", while §4.1 and Appendix C name those six services
concretely. Whether the real solution keeps that domain or substitutes another
has not been settled. Until it is, build the structure the blueprint specifies
and raise the domain question rather than assuming.

Present:

```
docs/backend-architecture/
  README.md                      index and chapter table
  01-purpose.md .. 15-cicd-deployment.md
  appendix-a-adrs.md             ADR-001 .. ADR-018
  appendix-b-licences.md         dependency licence register
  appendix-c-delivery-plan.md    PR sequencing plan
  appendix-d-type-inventory.md   type inventory
```

Planned, per §4.1 — do not invent a different shape for it:

```
src/BuildingBlocks/   Common.Domain, .Application, .Infrastructure, .Web, .Contracts
src/Gateway/          Gateway.Api (YARP)
src/BFF/              Web.Bff
src/Services/         Catalog, Ordering, Inventory, Payments — five projects each:
                        Domain, Application, Infrastructure, Migrator, Api
                      Shipping — the same five, but Worker in place of Api
                      Notifications — four: no Domain, and a Worker
tests/                <Service>.Domain.Tests, .Application.Tests, .Api.Tests,
                      .TestSupport, plus Platform.IntegrationTests
deploy/               compose/, helm/, k8s/
Directory.Build.props, Directory.Packages.props, Platform.slnx
```

Two things live outside that tree because §4.1 does not draw them:
`global.json`, which PR-01 delivers and whose SDK pin §4.1's prose relies on for
the `.slnx` floor; and `src/AppHost`, the optional Aspire host of §14.2. Aspire
is **not adopted** — Compose is the baseline (§14.1) and nothing references an
`Aspire.*` package today, which is why §4.4 pins none of them. If it is adopted,
`src/AppHost` is the only project taking `Aspire.Hosting.*`, but each service
picks up the client integrations for the resources it consumes — so backing it
out again costs a line per resource per service, not one deletion (§14.2).

### Which phase are you in

Check before assuming. If `Platform.slnx` exists, code is present and both the
build rules and the drift rules below are live. If it does not, this is still a
docs change and there is nothing to compile.

Once code exists, the commands are the ones the target solution uses:

```bash
dotnet restore Platform.slnx
dotnet build Platform.slnx
dotnet test  Platform.slnx
```

Central package management means versions live in `Directory.Packages.props`
with **exact** pins — never add a `Version=` attribute to a `PackageReference`.

PR-01 also ships `Directory.Build.props`, but the blueprint does not say what
goes in it beyond "shared MSBuild settings" (§4.1). An analyzer policy —
`TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`, a StyleCop package — is the
obvious candidate and **has not been decided**. Do not assert one; if you need
it settled, that is an ADR.

## The one rule that matters

**The blueprint must not contradict itself.** It is ~10,500 lines that describe
one coherent system; the failure mode is a statement in §9 that quietly
disagrees with §6, or an appendix that lists a package no chapter uses. Most of
the work done in this repo has been finding and closing those gaps.

So: when you change any load-bearing claim — a timeout, a retry count, a type
name, a registration order, an endpoint path, a package version — **grep the
whole blueprint for every other mention of it** and reconcile them all in the
same change. Fixing one site and leaving three is worse than not touching it,
because it converts a consistent error into an inconsistent one.

**Once code lands, this rule spans both.** The blueprint and the solution are
one artefact with two representations, and they drift the moment someone changes
a retry count in `Program.cs` and nowhere else. From then on:

- A code change that contradicts a chapter is not done until the chapter is
  amended in the same PR, or the code is changed to match. Pick one, in the PR —
  never leave the disagreement for later.
- A blueprint change that the code already implements differently is a bug
  report against one of them. Say which, and say why.
- `appendix-d-type-inventory.md` and `appendix-b-licences.md` are the two that
  rot fastest: a type renamed in code or a package added to
  `Directory.Packages.props` has to reach the appendix in the same change.
- Where the blueprint is genuinely wrong, fix the blueprint. It is a
  specification, not a historical record — but ADRs are the exception and are
  superseded, never rewritten.

Run `/validate-blueprint` after any substantive edit.

## Prose conventions

- **Wrap prose at 80 columns.** Tables, links and code blocks may exceed it.
- **British spelling in prose** — `behaviour`, `serialisation`, `licence`,
  `normalise`, `organised`. **Identifiers keep their real spelling**:
  `IPipelineBehavior`, `AddAuthorization`, `[Authorize]`. Never "correct" a type
  name into British spelling, and never Americanise the surrounding prose to
  match a type name.
- **Chapter headings are numbered**: `# 9. Messaging`, `## 9.1 Integration
  events`. Third-level headings are unnumbered prose titles
  (`### Handler contracts`). Appendices use `# Appendix A — <title>` and
  `## ADR-001 — <title>`.
- **Cross-references use the section sign**: `§9.3`, and link the first mention
  in a passage — `[§9.3](09-messaging.md)`. Within a chapter, `(§6.5)` bare is
  fine. Cite the section that actually states the claim; a reference to a
  section that only mentions the topic is a defect.
- **Callouts are blockquotes whose opening sentence is bold**, no emoji, no
  admonition syntax. Of the 68 in the blueprint, two forms are named and
  recurring — `**Trap — …**` (9) for a mistake worth naming, and
  `**Decision — …**` (10), which always points at the ADR that records it:

  ```markdown
  > **Trap — projecting everything by default.** Each projection is a second
  > copy of the truth, with its own bugs and its own rebuild procedure.

  > **Decision — no mediator library.** See [ADR-004](appendix-a-adrs.md#adr-004--no-mediator-library).
  ```

  The other 49 are a bold assertion followed by its argument —
  `> **Unregistered, this fails silently and completely.** …`. That is the
  default; reach for `Trap` or `Decision` only when the callout genuinely is
  one. `**Decision.** / **Why.** / **Consequences.**` are the ADR body form
  (see *Working in this repo* below), not callouts.
- **Em dashes** (`—`) for asides, **en dashes** (`–`) for ranges. Both are
  literal Unicode, not `--`.
- **Every chapter ends with a rule and a nav footer**, in this exact shape:

  ```markdown
  ---

  [← §5 Tactical DDD](05-tactical-ddd.md) · [Index](README.md) · [§7 Persistence →](07-persistence.md)
  ```

  Separator is ` · ` (U+00B7). First chapter omits the `←` link, last appendix
  omits the `→` link. One `---` only — a doubled rule before the footer is a
  regression that has been fixed before.
- **Tables carry the summary data**; prose carries the argument. A two-column
  borderless table (`| | |`) is the established form for metadata blocks.

## C# style — samples now, source later

**One dialect, both phases.** The rules below govern the fenced samples today
and the compiled solution when it arrives, so a sample and its implementation
read identically.

The repo `.editorconfig` is not a documentation convenience — it is the file
PR-01 ships. Change it deliberately, and reconcile any change with the samples
already written against it.

**Follow:**

- Four-space indent, spaces not tabs. CRLF line endings. Newline at end of file.
- `using` directives **outside** the namespace; no blank lines between
  consecutive usings. This one binds source only — the samples carry no `using`
  directives at all, because they are excerpts rather than compilable units
  (Appendix D). Do not "complete" a sample by adding them.
- Pascal case for types, properties, methods and events; `I` prefix on
  interfaces; namespace matches folder.
- **A single statement may omit braces; two or more always take them.** The
  statement goes on the following line — never beside the condition — and it may
  wrap:

  ```csharp
  if (amount < 0)
      throw new DomainException("Money cannot be negative.");

  foreach (IDomainEvent domainEvent in events.Where(projections.HasHandler))
      await publisher.StageAsync(domainEvent, OutboxLane.Local, ct);
  ```

  This holds across all 53 braceless bodies in the blueprint — 15 guard-clause
  `throw`s, 13 `return`s, and 25 single calls and `continue`s.
  `csharp_preserve_single_line_statements = false` keeps a format run from
  pulling any of them back up onto the condition's line. The one exception is a
  **wrapped** condition, which takes braces — see the multi-line condition rule
  below.

- **Explicit types for locals**, except where the right-hand side names the
  type.
  `var order = new Order(...)` and `var id = ProductId.New()` are fine —
  restating the type there is noise. Everything else is explicit:

  ```csharp
  IReadOnlyDictionary<ProductId, Money> priceList =
      await prices.GetAsync(productIds, command.Currency, ct);
  ProductId[] missing =
      productIds.Where(id => !priceList.ContainsKey(id)).ToArray();
  ```

  A reader of a fenced code block has no hover and no go-to-definition, and this
  blueprint's job is to teach types and contracts. The same rule governs the
  solution, so a sample and its implementation read identically. Code blocks may
  run past 80 columns and hundreds of lines do, so length alone is never a
  reason to hide a type — if a declaration turns unwieldy, split the expression.

  Four cases keep `var`, and only these four:

  | | Example |
  |---|---|
  | The RHS names the type | `var order = Order.Place(…)`, `var id = Guid.CreateVersion7()` |
  | Anonymous types | `var args = new { OrderId = orderId.Value };` |
  | Tuple deconstruction | `foreach (var (product, qty, price) in items)` |
  | Fluent resource DSLs | The whole Aspire AppHost block in §14.2 — eleven of its thirteen locals are an `IResourceBuilder<T>` whose name only repeats what the `Add*` call already said. Explicit types are possible there and read worse; keep the block uniform rather than typing part of it |
- Binary operators spaced. Where a wrapped one goes is the operator-placement
  rule below, which states it once.
- **A list is on one line, or one element per line. Never a ragged middle.**
  "List" means anything comma-separated inside brackets — parameters,
  arguments, collection expressions, initialisers, tuple members. The budget is
  **120 columns** (`max_line_length` in `.editorconfig`); within it the list
  stays on one line, and past it every element gets its own:

  ```csharp
  string[] forbidden = [
      "Microsoft.EntityFrameworkCore",
      "MassTransit",
      "StackExchange.Redis",
      "Microsoft.AspNetCore"
  ];
  ```

  `[` and `{` end the line they open and their closer sits on its own line at
  the opening construct's column. `(` also ends its line, but `)` trails the
  last element — `);`, not a line of its own:

  ```csharp
  _deliveryLag = meter.CreateHistogram<double>(
      "messaging.delivery.lag",
      unit: "s",
      description: "OccurredAt to consumer start.");
  ```

  Continuations indent **four**, never to a bracket column. A list too wide for
  one line was previously wrapped under its opening bracket
  (`string[] forbidden = ["…",` / 26 spaces / `"…"];`); that form is gone and a
  surviving one is a leftover. Two things keep it from being mechanical: a line
  comment on an element forces the broken form regardless of width, and the
  four `var` cases above still apply inside the elements.
- **A broken fluent chain puts every call on its own line**, at head + 4, never
  aligned under the receiver's dot. That includes the *first* call: if the chain
  breaks at all, nothing stays on the head's line. The head is whatever contains
  no invocation, so it is often a bare identifier sitting alone:

  ```csharp
  builder.Services
      .AddReverseProxy()
      .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

  builder
      .Property(o => o.Id)
      .HasConversion(id => id.Value, value => new OrderId(value))
      .ValueGeneratedNever();
  ```

  Unlike a list, a chain is **never joined back up to fit** — it is broken for
  reading order, not for width, and the example above is 100 columns broken
  across three lines on purpose. One exception: a short qualifier and its
  subject count as one call (`.That().ResideInNamespaceContaining(…)`,
  `.ShouldNot().HaveDependencyOn(…)` — NetArchTest's idiom reads as pairs).

  **The head is the line the chain starts on, which is not always the statement's
  first line.** When a declaration's initialiser wraps, that wrapped line is the
  head and the chain indents four past *it* — eight from the declaration:

  ```csharp
  ValidationFailure[] failures =
      (await Task.WhenAll(validators.Select(v => v.ValidateAsync(context, ct))))
          .SelectMany(r => r.Errors)
          .Where(f => f is not null)
          .ToArray();
  ```

  Measuring from the declaration instead would put `.SelectMany` level with the
  expression it is chained onto, and the chain would read as a sibling of the
  initialiser rather than as applied to it. A `})` closing a lambda mid-chain is
  a continuation, not a head — the calls after it keep the chain's indent.

  **"Contains no invocation" means no *dotted* call, and a receiver never
  outranks that.** `Types`, `app`, `_lines`, `from` and `Enumerable` all sit
  alone as heads, static classes and fields alike — `Types.InAssembly(a)` and
  `Enumerable.Range(0, n)` break after the receiver just as `builder.Services`
  does. What stays on the head is whatever has nothing to strand in front of
  it: object creation (`new MsSqlBuilder()`), a call with no receiver
  (`When(OrderPlaced)`, `GetInvoker<TResult>(…)`, `BuildServices()`), and a
  parenthesised expression (`(await Task.WhenAll(…))`). Splitting those would
  leave a `new` or a bare `(` on a line of its own, which is the thing the rule
  is trying to avoid.
- **A lambda body that is itself a call goes on its own line**, at + 4, rather
  than trailing after the `=>`. A bare parameter re-mention is not a call and
  stays — `p => p` heading its own chain is the fluent-DSL idiom, and moving it
  down would strand a single letter on a line:

  ```csharp
  builder.Services
      .AddCors(o =>
          o.AddDefaultPolicy(p => p
              .WithOrigins(builder.Configuration.GetRequiredSection("Cors:Origins").Get<string[]>()!)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()));
  ```

  So `o => o.AddDefaultPolicy(…)` breaks and `p => p` does not, in the same
  statement. Each nesting level is then one indent, and the reader can see which
  builder each call belongs to. This applies only when the construct is already
  broken across lines; inside 120 columns it stays on one.

  An **expression-bodied member** is not a lambda for this purpose:
  `public Money Total => _lines.Aggregate(…)` keeps its body on the `=>` line,
  because there is no nesting for the break to clarify. When such a member's
  body *does* need its own line, `=>` still trails the signature — it is an
  operator, and the rule above applies to it too:

  ```csharp
  public Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken ct = default) =>
      GetInvoker<TResult>(command.GetType(), typeof(CommandInvoker<,>))
          .InvokeAsync(services, command, ct);
  ```

  The body sits at the **declaration's** indent + 4, which is not necessarily
  the `=>` line's + 4: a signature that itself wraps puts its parameters at + 4
  already, and measuring from there would indent the body to match them. Join
  the signature where it fits inside 120 and the question does not arise.
- **Break at the outermost bracket, never a nested one.** When a call's argument
  is itself a call, it is the outer parenthesis that opens the line — reaching
  past it to break the inner one leaves the outer call glued to its argument and
  reads as though the inner list were the outer's:

  ```csharp
  // Wrong — the break is inside OrderPlacedDomainEvent, and Raise( is stranded.
  order.Raise(new OrderPlacedDomainEvent(
      order.Id, customerId, order.Total, order.SnapshotLines(), now));

  // Right.
  order.Raise(
      new OrderPlacedDomainEvent(order.Id, customerId, order.Total, order.SnapshotLines(), now));
  ```

  The argument moves to the next line **whole**; break it further only if it
  still does not fit, and then one element per line as usual. This is the same
  principle as the lambda rule above — a nested construct starts its own line —
  and it is why `.Send(queue, ctx => new CancelOrder(…))` breaks after `ctx =>`
  rather than after `CancelOrder(`.
- **A multi-line condition takes trailing operators, four-space continuations,
  and braces on its body.** The braces are what make four safe: without them the
  last `&&` line and the body sit in the same column and the reader cannot see
  where the condition stops.

  ```csharp
  if (currentUser.IsAuthenticated &&
      order.CustomerId.Value != currentUser.Id &&
      !currentUser.HasPermission("orders:admin"))
  {
      return Result.Failure(OrderErrors.NotFound);
  }
  ```

  This is the **one exception to the braces rule above** — a single statement may
  omit braces, unless the condition it hangs off is wrapped. Prefer not to wrap
  at all; joining is the better fix, and it has already been applied everywhere
  it fits. The block above is the corpus: one wrapped header, in §11.4, kept
  because the three clauses of an ownership check do not join inside 120. A
  second one appearing is a signal to join, not a precedent.
- **Operators go at the end of the line they continue from**, not the start of
  the next (`dotnet_style_operator_placement_when_wrapping = end_of_line`). Each
  line then ends by announcing that more is coming. This holds for `&&`, `||`,
  `??` and `+`, in conditions and in expressions alike; a leading `&&` or `??` is
  a leftover from the previous style. It governs wrapped lambda predicates as
  much as `if` headers.
- A base-type list is the one continuation that is **not** covered by any of the
  above: it has no bracket to hang off and no operator, and it is already one
  entry per line. Leave it aligned under the `:`.
- Prefer collection expressions, `is null` over `ReferenceEquals`, null
  propagation, compound assignment, simplified interpolation, primary
  constructors.
- **No `#pragma` suppressions** — there are none in the corpus and a sample that
  needs one is a sample whose design is wrong. If a suppression is genuinely
  warranted in source, it belongs in `Directory.Build.props` with a comment
  saying why, never inline.
- **No real credentials** — no production connection string, key or certificate
  path, in a sample or in source. Bind configuration or read the environment;
  deployed secrets come from a vault (§15.4).

  The deliberate exception is local development, and it is not a defect to be
  tidied away: §14.1's Compose file carries
  `${SQL_PASSWORD:-Local_Dev_Pa55w0rd!}` and
  `${BFF_CLIENT_SECRET:-local-dev-secret}`, and documents Keycloak as
  `admin/admin` and RabbitMQ as `guest/guest`. Those defaults are what make
  `docker compose up` work with no prior setup; the environment variable in
  front of each is the seam that keeps them out of anything deployed.

**Settled choices — do not "fix" them:**

| | |
|---|---|
| Namespaces | File-scoped (`namespace X;`), never block-scoped |
| Expression-bodied members | Used for one-line members, not for constructors |
| Braces | Optional for a single statement, required for two or more |
| Target framework | .NET 10 (LTS), C# 14 |

Each of these is a house style a reviewer might otherwise read as an oversight
and "correct". They are consistent across all ~135 existing C# blocks, and the
solution will be written the same way. Changing one is a decision about the
whole corpus, not about the file in front of you.

**Fence languages in use:** `csharp`, `sql`, `yaml`, `bash`, `json`, `mermaid`,
`dockerfile`, `xml`, and bare ``` for trees and console output. Always tag a
fence that contains a real language.

## SQL style

Most of the SQL lives inside C# raw string literals rather than `sql` fences, so
"the statement's own left margin" below means the literal's indent, not column
zero.

**Clause keywords start their own line at that margin, one space before what
follows; continuations indent four.** Continuations are the connectors that
extend a clause — `AND`, `OR`, and a `MERGE`'s `ON`. Each predicate gets its own
line: a chain packed onto one line hides the shape of the condition, which in
this blueprint is usually the point being made.

**An `AND` extending an `ON` aligns with the `ON`**, rather than indenting
another four. `ON` is itself a continuation — of `MERGE … USING` or of a
`JOIN` — so its predicates sit at the level it already occupies, and a second
indent would imply a nesting that is not there:

```sql
MERGE ordering.ProductPrices AS target
USING (SELECT ProductId = @ProductId, Currency = @Currency) AS source
    ON target.ProductId = source.ProductId
    AND target.Currency = source.Currency
```

This is the one place `AND` does not indent past the keyword it extends; under
`WHERE`, which is a clause keyword at the margin, it still does.

```sql
UPDATE ordering.OrderSummaries
SET FulfilmentCounted = 1
OUTPUT inserted.PlacedAt, inserted.ConfirmedAt
WHERE OrderId = @OrderId
    AND PlacedAt IS NOT NULL
    AND ConfirmedAt IS NOT NULL
    AND FulfilmentCounted = 0;
```

One space means one, `WHERE` included — it used to be written `WHERE  ` and no
longer is.

`UPDATE <table>` and `SET` are separate lines. The exception is `MERGE`'s
`UPDATE SET`, which names no table and stays one token.

**A `SET` that breaks keeps nothing on the keyword's line.** Like a fluent
chain, if the assignment list does not fit it goes below whole — the first
assignment does not stay up beside `SET` with the rest hanging under it:

```sql
UPDATE SET
    CustomerId  = @CustomerId,
    TotalAmount = @Total,
    Currency    = @Currency,
    LineCount   = @LineCount,
```

The `=` signs line up in a column here, and that alignment is deliberate — it is
the same one the C# initialisers use, and it survives because these are one
element per line rather than a wrapped list.

**A SQL list obeys the same rule as a C# one: one line, or one element per
line, never a ragged middle.** That covers the column list after `INSERT`, the
values after `VALUES`, the columns after `OUTPUT` and `GROUP BY`, and a
function's arguments. The budget is the same 120 columns, measured from the
literal's margin:

```sql
INSERT (OrderId, CustomerId, Status, TotalAmount, Currency, LineCount, Products, PlacedAt, UpdatedAt)
VALUES (@OrderId, @CustomerId, @Status, @Total, @Currency, @LineCount, @Products, @PlacedAt, @UpdatedAt)
```

Past the budget the `(` ends its line and each element takes one, indented four
— **not** aligned under the first argument. `DATEADD(second, …)` wrapping its
arguments into a column under `second` is the keyword river again, one scope in.

The single exception is a **DDL body**, where alignment is a table rather than a
wrapped list: `CREATE TABLE` and `CREATE INDEX` keep their aligned type and
constraint columns, because there the columns carry the meaning.

A parenthesised sub-expression short enough for one line stays on one line;
`AND (LockedUntil IS NULL OR LockedUntil < SYSDATETIMEOFFSET())` is one
predicate, not two. A parenthesised group that *does* break indents its `OR`s a
further four, so nesting depth is visible:

```sql
WHERE o.CustomerId = @CustomerId
    AND (@AfterPlacedAt IS NULL
        OR o.PlacedAt < @AfterPlacedAt
        OR (o.PlacedAt = @AfterPlacedAt AND o.Id < @AfterId))
```

**Column aliases are assignments, not `AS`** — `Total = o.TotalAmount`, never
`o.TotalAmount AS Total`. The name being defined then starts the line, so a
projection reads as the row shape it produces, and the `=` column lines up the
way `SET`'s does. This is the SELECT list only: `MERGE … AS target`,
`USING (…) AS source`, `WITH claimable AS (` and `CAST(x AS varchar(10))` are
required syntax and keep `AS`.

**`INNER JOIN` is spelled in full, and its `ON` gets its own line** at + 4. The
join condition is a predicate and belongs where predicates go, not trailing off
the end of a table name:

```sql
FROM ordering.Orders o
INNER JOIN ordering.OrderLines l
    ON l.OrderId = o.Id
```

An alias follows its table after one space. Padding table names into a column
(`FROM ordering.Orders      o`) is the old keyword river in another costume.

This replaced a right-aligned keyword river (`FROM   `, `WHERE  `, `  AND  `,
every argument at column 7). If you find one, it is a leftover — convert it.

## Working in this repo

- **Read before you edit.** Chapters run to 2,000 lines; the claim you are about
  to change is usually stated more than once.
- **Changing the chapter set** means updating four places: the file itself, the
  chapter table in `docs/backend-architecture/README.md`, the nav footers of
  both neighbours, and any `§n` cross-references that shift.
- **New ADRs** append to `appendix-a-adrs.md` with the next free number
  (currently ADR-019) and keep the
  `**Decision.** / **Why.** / **Consequences.**` three-part form. ADRs are
  never renumbered; supersede rather than rewrite.
- **New dependencies** — whether mentioned in a chapter or added to
  `Directory.Packages.props` — must reach the licence register in
  `appendix-b-licences.md` with their licence and role. Versions live in
  `Directory.Packages.props`, not the register; state one there only where the
  version *is* the point, as with MassTransit 8.x. A package in a sample or a
  csproj but not the register is a known drift class — NetArchTest and Aspire
  were both missing — and PR-01 wires a licence allow-list gate into CI that
  will fail on it.
- **Commit messages** are semantic and present-tense: `docs:`, `feat(<scope>):`,
  `fix:`, `chore:` — the delivery plan in Appendix C already names each PR in
  this form, so use its title verbatim when you implement one.
- `.remember/` is session state, not content. Never edit it as part of a change.

Once code is present, additionally:

- **TDD is the stated method** (§12), not a preference. Tests ship in the same
  PR as the code they cover — the convention starts at PR-02 and there is no
  PR in the plan that adds tests afterwards.
- **Follow the delivery plan's order.** Appendix C sequences 26 PRs with
  explicit dependencies, and the service order (Notifications → Catalog →
  Ordering → Inventory and Payments → Shipping) is deliberate. Building out of
  order is a design decision, not a shortcut — raise it rather than taking it.
- **The architecture tests are the enforcement mechanism**, not review.
  NetArchTest gates land at PR-07: domain isolation, Application ↛ EF Core,
  endpoints ↛ Infrastructure, Application and Domain ↛ MassTransit. If a
  change needs one of those gates relaxed, the gate is probably right and the
  design is probably wrong.
- **`Program.cs` in each `*.Api` is the only composition root** (§4.2). Wiring
  belongs in `AddXApplication()` / `AddXInfrastructure(config)`, not scattered.
- **`main` stays green.** Every PR in the plan is specified to leave it building
  and passing.

## Available commands

Content:

| | |
|---|---|
| `/validate-blueprint` | Multi-pass self-consistency audit; also code ↔ docs drift once `src/` exists |
| `/check-links` | Link, cross-reference and nav-footer integrity |
| `/new-chapter` | Scaffold a chapter and rewire its neighbours |
| `/new-adr` | Append an ADR in the established form |
| `/style-pass` | Apply one corrected code form corpus-wide, then record it in `CLAUDE.md` and `.editorconfig` |

Delivery:

| | |
|---|---|
| `/ship` | Run the three below in sequence, resuming where a previous run stopped |
| `/branch` | Start a correctly named branch, carrying uncommitted work off `main` |
| `/commit` | Split the working tree into semantic commits with arguing bodies |
| `/pr` | Open a PR in the house body form |
| `/review-copilot` | Triage Copilot's PR comments — verify each before acting |
| `/review-grok` | Triage an external review into a resolution record |

`git push` is denied to Claude in `.claude/settings.json`, so `/pr` stops and
asks the user to run `! git push -u origin <branch>` before it opens anything.
That is deliberate; `gh pr create`'s offer to push the branch is the same hole
by another route and is not to be used either. `/ship` inherits the stop rather
than routing around it, which is why it is written to resume: re-running it
after the push continues from where it halted.
