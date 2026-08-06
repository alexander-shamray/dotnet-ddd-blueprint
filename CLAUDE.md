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
  statement goes on the following line, and it may wrap:

  ```csharp
  if (amount < 0)
      throw new DomainException("Money cannot be negative.");

  foreach (IDomainEvent domainEvent in events.Where(projections.HasHandler))
      await publisher.StageAsync(domainEvent, OutboxLane.Local, ct);
  ```

  This holds across all 42 braceless bodies in the blueprint — 15 guard-clause
  `throw`s and 27 `return`s and single calls.

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
- Binary operators spaced; wrapped operators go at the **beginning** of the
  continuation line.
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

| | |
|---|---|
| `/validate-blueprint` | Multi-pass self-consistency audit; also code ↔ docs drift once `src/` exists |
| `/check-links` | Link, cross-reference and nav-footer integrity |
| `/new-chapter` | Scaffold a chapter and rewire its neighbours |
| `/new-adr` | Append an ADR in the established form |
