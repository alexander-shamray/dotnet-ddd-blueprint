# PR-11 — the new-service scaffold

Design for `feat(tooling): new-service scaffold script` (Appendix C, PR-11;
depends on PR-07 and PR-10). Written before implementation and frozen at write
time — where this document and the blueprint disagree, the blueprint wins.

Appendix C's row is one sentence: "Copies and renames the template: ports,
database name, solution entries, Compose block. Dogfooded by PR-18." §C.4 adds
the acceptance criterion — "The scaffold script is proven by the next real
service, not by intent. If the script cannot produce Ordering, it is not
finished." Everything below is an argument about how to satisfy those two.

## 1. The template is Catalog itself, not a copy of it

Two shapes were available.

A **`dotnet new` template**, or any `tools/new-service/template/` directory of
tokenised files, is the obvious answer and it is the wrong one here. It is a
second copy of the wiring — a second `CatalogDbContext`, a second
`MigratorHost`, a second Dockerfile — that nothing builds, nothing tests, and
nothing reconciles. The one rule that matters is that this repository must not
contradict itself; a template directory is a contradiction generator with a
release schedule of "whenever someone remembers". It also cannot do the half of
PR-11's row that is not project files at all: `Platform.slnx`, the Compose
pair, the override, `.env.example` and the ports README.

So the scaffold **reads `src/Services/Catalog` and `tests/Catalog.*` at run
time** and renders a new service from them. There is exactly one copy of the
wiring, it is the copy that CI builds and `dotnet test` exercises, and an
improvement to Catalog reaches Ordering the next time the script runs. This is
also literally what Appendix C says: *copies* the template.

The cost is fragility, and it is real: the script names text inside files that
people edit. That cost is paid, not hidden — see §5.

## 2. What it copies, and what it deliberately does not

Catalog's tree is two things wearing one name: the **wiring** every service
needs, and the **Catalog slice** PR-10 built on top of it. The scaffold emits
the first and none of the second.

Excluded, by explicit path:

| | |
|---|---|
| Domain | `Common/Money.cs`, `Products/` (4 files) |
| Application | `Products/` (6 files) |
| Infrastructure | `ProductConfiguration.cs`, `ProductRepository.cs`, `*_AddProducts.*` |
| Api | `Endpoints/ProductEndpoints.cs` |
| Domain.Tests | `MoneyTests.cs`, `ProductTests.cs` |
| Application.Tests | `GetProductsHandlerTests.cs`, `PublishProductHandlerTests.cs`, `PublishProductValidatorTests.cs` |
| Api.Tests | `ProductEndpointsTests.cs` |

Everything else is copied and renamed. That is a larger inheritance than it
sounds: `DatabaseSmokeTests` and `TransientFaultInjection` are entirely
template-shaped — the migrator's exit code, §7.1's key boundary, the readiness
probe against a real engine, `EfUnitOfWork`'s commit, rollback and retry
semantics — and a scaffolded service arrives with twenty-four passing tests,
eleven of them against a real SQL Server, without anyone writing one.

**The output is PR-07's state with PR-08, PR-09 and PR-10's wiring on it**, not
PR-10's state with the nouns changed. A scaffold that renamed `Product` to
`Order` would hand PR-18 a deletion job and a vocabulary it did not choose;
`PublishOrderCommand` and `OrderPublishedDomainEvent` are Catalog's verbs
wearing Ordering's nouns. The domain is the author's, the wiring is the
scaffold's, and the line between them is where this list is drawn.

> **The scaffold is domain-neutral, and that matters beyond this PR.** The
> roadmap names the unsettled domain question as the largest single risk on the
> schedule. Whatever domain the platform ends up modelling, the scaffold is
> unaffected — it copies no aggregate, no command and no endpoint.

## 3. The seven places the wiring names the slice

Removing the slice leaves seven files making claims that are no longer true.
Each is an **anchored patch**: exact text, matched against the Catalog source
before renaming, asserted to occur exactly once.

1. **`Application/DependencyInjection.cs`** — the validator scan loses its
   `PublishProductValidator` anchor and its `using`. §4.2's line keeps its
   meaning, over the assembly rather than over a type in it, and re-anchors on
   the first validator that exists.
2. **`Infrastructure/DependencyInjection.cs`** — the `IProductRepository`
   registration and its `using` go. §5.6's line returns with the service's
   first repository.
3. **`Api/Program.cs`** — `app.MapProductEndpoints();` and its `using` go. The
   host then serves the probes and the OpenAPI document, which is exactly what
   PR-07's host served.
4. **`Infrastructure/Persistence/CatalogDbContext.cs`** — the comment naming
   `ProductConfiguration` as "what it finds today" is amended; the scan finds
   nothing in a fresh service, and §7.2's argument for landing the line early
   is the argument for keeping it.
5. **`Domain.Tests/ArchitectureTests.cs`** and
   **`Application.Tests/ArchitectureTests.cs`** — both anchor
   `typeof(Product).Assembly`. They re-anchor on the marker of §4 below.
6. **`Application.Tests/DependencyInjectionTests.cs`** — the two tests that
   assert on slice types go (`…registers_the_command_validator`,
   `…registers_the_slice_handlers`). The other four — dispatcher, clock,
   metrics, behaviour order — are about the template and stay.
7. **`Api.Tests/DatabaseSmokeTests.cs`** — the applied-migrations assertion
   drops from two migrations to one. Its doc comment about `AddProducts` goes
   with it.

Two of these are losses worth naming rather than papering over. The scaffolded
service has **no test that the validator scan is wired** (there is no validator
to find) and **no test that the §6.2 handler scan produces anything** (there is
no handler). Both guards return with the service's first slice, and the
generated `DependencyInjection.cs` carries a comment saying so at the line
concerned — the same shape as the seat `IdempotencyBehavior` holds in the
pipeline.

## 4. `AssemblyMarker` comes back, on purpose

Two architecture gates anchor on `typeof(Product).Assembly`, and a service with
no domain type has nothing to name. PR-07 shipped an `AssemblyMarker` for
exactly this reason and PR-10 deleted it when `Product` arrived; the scaffold
emits it again, because the scaffold's output *is* PR-07's state.

`{Service}.Domain/AssemblyMarker.cs`, one sealed empty class, whose doc comment
says what to do with it: delete it when the first aggregate lands and re-anchor
the two gates on that aggregate, as Catalog did. It is the only file the
scaffold writes that has no counterpart in Catalog, and it is written to be
deleted.

The `Common.Domain` project reference beside it is unused on day one, which
this repository normally forbids — an unused project reference is a claim about
the dependency graph that nothing makes true. It is kept because §4.2's
dependency table's first row *is* the claim: "Common.Domain and nothing else"
is the constraint the service is being created under, and the gate in
`{Service}.Domain.Tests` reads it as an allow-list, so an unused reference
neither passes nor fails anything it should not. The alternative — a scaffold
that omits the reference and leaves the first aggregate to add it — moves a
decision from the template into six future services.

## 5. Drift fails the run, loudly

Every anchor in §3, and every anchor in the four shared files of §6, is
asserted to match **exactly once** in its source. A miss raises
`ScaffoldError` naming the file and the text, and the script writes nothing —
the whole render is built in memory and validated before a single file is
created. There is no half-scaffolded state to clean up.

That converts the fragility of §1 from a silent wrong output into a loud
refusal, which is the same trade the repository already makes when it declines
to skip container tests on a missing Docker daemon: a tool that fails open on
its own precondition is a tool that reports success for work it did not do.

Two further checks run over the rendered output before it is written:

- **No stragglers.** `Catalog`, `catalog`, `CATALOG`, `Product` and `product`
  must not appear anywhere in the generated tree. This is the single best test
  a rename script can have, and it catches the case the anchors cannot: a
  mention of the slice in a file nobody thought to patch.
- **No collisions.** Every target path must not already exist, the service name
  must be PascalCase and must not be `Catalog`, and the requested port must not
  already be published in `docker-compose.yml`.

## 6. The four shared files, and why the port is an argument

`Platform.slnx`, `deploy/compose/docker-compose.yml`,
`docker-compose.infra-only.yml`, `.env.example` and `deploy/compose/README.md`
are edited in place — five, counting the README, which the row implies under
"ports".

The two Compose edits and the `.env.example` edit are **extractions**, not
templates: the script lifts Catalog's own block out of the file it is editing,
renames it and inserts the copy. One source of truth again, and the comments
that argue those blocks — the inline-default rule, the pair rule, the profiles
technique — travel with them. The `.slnx` and README edits are line insertions
in alphabetical position.

`--port` is **required**, and refusing to derive it is deliberate. The obvious
rule — one past the highest allocated — would have produced 5103 for Ordering,
where §14.1 prints 5101, and a script that quietly disagrees with a printed
chapter is worse than one that asks. A port is an allocation recorded in two
documents and a firewall rule; it is the caller's decision. The script's job is
to check the answer is free, which it does.

## 7. Python, and where it lives

Stdlib Python 3.12, no dependencies, no restore — the same choice, for the same
reasons, as the licence gate the repository already runs. It is cross-platform
in a repository whose author is on Windows and whose CI is Ubuntu, and it is
testable with `unittest` without a build.

It does **not** live under `.github/`. The licence gate is there because it is
CI-only and §4.1 draws no tree for it; the scaffold is a developer tool that
happens to be tested in CI, and filing it under the CI provider's directory
would be filing it by its least important property. It goes in
`tools/new-service/`, and §4.1's tree gains a `tools/` entry — the honest fix
for "the blueprint draws no such tree" is to draw it.

**Cross-platform means the anchors cannot name a line ending.** The template
does not have one: `.gitattributes` forces `*.cs text eol=crlf`, so C# is CRLF
everywhere, while `.csproj`, `.slnx`, the Compose YAML, the Markdown and the
Dockerfiles carry no attribute and so arrive CRLF on Windows and LF on the
Ubuntu runner. Every anchor here is therefore spelt with LF and matched against
text normalised to LF, and each file's own endings are put back on the way out —
so a generated `.csproj` follows the checkout it was rendered from rather than
the platform its author used. The `AssemblyMarker` is the one file with no
template beside it to take endings from; it takes the ones the template's own
C# has, read rather than assumed, so a change to `.gitattributes` carries into
generated code without a second edit.

## 8. Tests

`tools/new-service/test_new_service.py`, stdlib `unittest`, run against the
**real repository** rather than a fixture — the drift the design admits in §1
is only caught by rendering the tree that actually exists. Every test renders
into a temporary directory; none writes to the checkout.

| | |
|---|---|
| Renders every project | five under `src/Services/Ordering/`, four under `tests/`, plus the marker |
| Excludes the slice | no `Product*`, no `Money`, no `AddProducts` migration, in any generated path |
| No stragglers | the rendered text contains no `Catalog`/`catalog`/`CATALOG`/`Product`/`product` |
| Renames the schema and the keys | `HasDefaultSchema("ordering")`, `ConnectionStrings:Ordering`, `ConnectionStrings:OrderingMigrator`, `ORDERING_CONNECTION` |
| Patches all seven files of §3 | each asserted on its rendered content |
| Emits a fresh migration id | file name and `[Migration("…")]` agree, and the id is not Catalog's |
| Writes the snapshot for an empty model | derived from the copied `InitialCreate.Designer.cs`, not from Catalog's snapshot |
| Edits the five shared files | solution entries in alphabetical position, the Compose pair before `otel-collector`, both override entries, the env vars, the README row |
| Refuses a bad run | non-PascalCase name, `Catalog`, an existing target directory, a taken port — each raises and writes nothing |
| Refuses drift | a patch anchor deleted from a copy of the template raises `ScaffoldError` |

CI gains a `scaffold` job beside `licence-gate`: checkout, Python, `python -m
unittest`. It does not gate `build` — a broken scaffold does not make the
solution wrong — but it turns the workflow red, which is the point.

## 9. The migration, and the one machine-owned file

The scaffold copies `InitialCreate.cs` and `InitialCreate.Designer.cs` with a
fresh UTC migration id, and it must also produce a **model snapshot**, because
copying Catalog's would carry `Product` into a service that has no such entity
and the next `migrations add` in that service would generate a `DROP TABLE`.

CLAUDE.md's rule is that the snapshot is left exactly as the tool wrote it. The
scaffold honours it by not writing one from scratch: `InitialCreate.Designer.cs`
already contains the tool's own description of an **empty model with a default
schema**, which is precisely the model a fresh service has. The snapshot is
derived from that file by four anchored substitutions on the class wrapper —
the attribute, the class declaration, the method name and one `using`. The
model body, the part that can be wrong in a way that costs a migration, is
never retyped.

Verified empirically rather than reasoned about: scaffold a service, run
`dotnet ef migrations add` against it, and the generated `Up` must be empty. A
snapshot that had lost `HasDefaultSchema` would emit an `EnsureSchema` there.

## 10. What this PR deliberately does not do

- **No Worker template.** §4.1 gives Shipping and Notifications a Worker in
  place of an Api. No worker host exists to copy, and copying one that does not
  exist is inventing a project early. `--kind worker` joins the PR that builds
  the first one; the script rejects anything but an API service today, by not
  offering the choice.
- **No gateway route.** PR-17 builds the gateway; there is no
  `ReverseProxy:Routes` to append to. PR-18's row lists "gateway route" as its
  own deliverable for exactly this reason.
- **No Helm chart.** PR-23's, and `deploy/helm/` does not exist.
- **No Ordering.** PR-18 is the service; PR-11 is the script. The dogfood run
  in this PR renders Ordering, builds it, tests it and discards it — the
  evidence is in the PR body, not in the tree.
- **No `--dry-run`.** The repository is a git checkout; `git status` is the
  preview and `git checkout .` is the undo. A second mode is a second thing to
  keep correct.
- **No re-run support.** The script creates a service; it does not update one.
  A second run against the same name refuses on the existing directory, which
  is the correct answer — reconciling a service someone has since edited is a
  merge, not a copy.
