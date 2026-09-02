# CLAUDE.md

Guidance for Claude Code when working in this repository.

**Six long sections of this file now live beside it under `docs/`.** Every
landed PR appended its findings here and nothing was ever consolidated, so a
file loaded into every session's context had grown past three thousand lines,
and then — after the first four moved out — most of the way back.

**Five of the six were extractions and the sixth was a fold, and the difference
decides what you can expect to find at the far end.** The extractions — the
decision log, the lessons, the harness boundaries, the repo map, the style
guide — moved out **verbatim in their arguments**: not one was shortened,
because a summary of an argument is how a rule gets "corrected" back, and each
of those headers lists what *was* edited on the way out. `docs/testing.md` is
the fold: it already carried nearly all of *The commands*, so moving that
section verbatim would have made a third copy of a list that already had two.
Only what was uniquely here went, the short form stayed, and that file's header
describes its split with §12 rather than an extraction — correctly, because
nothing was extracted into it.

| | |
|---|---|
| [`docs/pr-decision-log.md`](docs/pr-decision-log.md) | What each PR from PR-08 on decided — the long half of the phase section |
| [`docs/lessons.md`](docs/lessons.md) | The lessons that generalise past the PR that found them, and the measurement behind each |
| [`docs/harness-boundaries.md`](docs/harness-boundaries.md) | What the harness grants and refuses, and every grant wider than the operation it buys |
| [`docs/repo-map.md`](docs/repo-map.md) | What each entry in the tree is and why it is shaped that way — the gates, the workflows, the deploy trees and the scaffold |
| [`docs/style-guide.md`](docs/style-guide.md) | The prose, C# and SQL dialect both artefacts are written in, and which rules the build enforces |
| [`docs/testing.md`](docs/testing.md) | How to run every suite and every gate, and what each needs |

**Read the one that covers what you are about to touch**, before you touch it.
They are where the traps are recorded. What stayed here is what an agent needs
in order to *act* whatever it is working on: the repo's shape, the phase, the
one rule, and enough of the dialect that an edit made before opening the style
guide is still the right shape.

**Two of the six left something behind that is neither a pointer nor a copy,
and both are declared where they sit.** *The tree* was rewritten rather than
summarised — the section opens by refusing to hold an inventory and had become
one, so the annotations went to the map and what stayed was re-derived from
that opening rule. *Style* keeps a short list of the rules that have to be true
of an edit made before anyone opens the guide. Each is a rule in two places,
which the one rule below charges for explicitly: the change that moves one has
to reach both.

The rest of what left went for a different reason — duplication of Appendix D
and of `.claude/commands/*.md`, argued below. **No line count of this file
appears in this paragraph on purpose**: a file that states its own length
invalidates the claim with the next edit, including the edit that fixes it.

**Each of those six is outside the blueprint tree**, so each is in no index
and behind no nav footer, and `/check-links` reaches none of them.
`/validate-blueprint` reaches one — `testing.md`, which it names in its scope.
For the other five the one rule below is all that carries them, and it is all
that does.

**`roadmap.md` is in that command's scope too and is not one of the six**,
because it was never part of this file. The two claims meet only in
`/validate-blueprint`'s scope list and are otherwise about different sets —
running them together is how an except-clause ends up naming a file its own
sentence never introduced.

## What this repo is

`dotnet-ddd-blueprint` is a monorepo for an ASP.NET Core microservices platform
built with DDD, CQRS and TDD. It is two artefacts with one specification: the
blueprint under `docs/backend-architecture/`, and the C# solution that
blueprint specifies — thirty-three projects, and counting. **The phase section
below carries what has landed**; this sentence only names the shape.

**The blueprint is the specification for the solution.** Appendix C sequences
that code into a numbered plan, and then into the *After the plan* rows that
follow it. Treat every chapter as a commitment the implementation will have to
honour.

**No count here, and `docs/roadmap.md` dropped its own for the same reason.**
This sentence said 27 — the plan's numbered rows — and read as a total for a
document that had grown a section past them. The predicate a reader can check
is the table; a numeral in front of it only says how stale this file is.

Both READMEs read **"Reference blueprint — adapt, don't copy wholesale. The C#
solution it specifies will be built in this repository"** — the blueprint serves
a visiting reader and this repo's own build at once.

Keep two different things apart:

- **Solution shape** — the projects §4.1 lays out: Catalog, Ordering, Inventory,
  Payments, Shipping, Notifications, plus the building blocks, gateway and BFF.
- **Service build order** — Appendix C.1, which is *not* the §4.1 listing
  order: **Catalog → Ordering → Inventory and Payments → Shipping →
  Notifications**. Notifications goes **last**, and the reason is worth
  carrying: it publishes nothing and its whole contract is seven events owned
  by Ordering, Payments and Shipping (§3.2), so before those exist it is a
  consumer with no producers. C.1 used to say it went first, on the grounds
  that a service with no domain logic proves the pipeline end to end with
  nothing else to debug — appealing, and wrong, because end to end needs both
  ends. C.2 never built it first either: PR-10 is Catalog and PR-18 is the
  "second service".

One thing is genuinely **undecided**: the READMEs call the e-commerce domain
"illustrative only", while §4.1 and Appendix C name those six services
concretely. Whether the real solution keeps that domain or substitutes another
has not been settled. Until it is, build the structure the blueprint specifies
and raise the domain question rather than assuming.

### The tree

**One line per entry, because the inventory lives elsewhere.** What each
project *holds* is the job of `appendix-d-type-inventory.md` and §4.1; why each
gate, workflow and deploy tree is *shaped* the way it is belongs to
[`docs/repo-map.md`](docs/repo-map.md). This tree says where things are, not
what is in them.

**It says so again because it had stopped being true.** Every entry that ever
needed a caveat grew one here, until a section whose first sentence refuses an
inventory was carrying two hundred lines of one — the argument for a workflow's
trigger, the mechanism by which RabbitMQ seeds a default user, the page-size
guard inside the closure gate. Those went to the map verbatim; the lines below
were re-derived from the rule above them, which is why this is the one part of
this file that is shorter than what it replaced rather than a pointer at it. A
map you have to open in order to find a directory is not a map.

```
docs/backend-architecture/   the blueprint — README index, 01-purpose ..
                             15-cicd-deployment, appendix A (ADR-001..042),
                             B (licences), C (delivery plan), D (type inventory)
docs/roadmap.md              estimates and a calendar laid over Appendix C
docs/pr-decision-log.md      what each PR from PR-08 on decided
docs/lessons.md              what the next PR should do about it
docs/harness-boundaries.md   what the harness grants these commands, and refuses
docs/repo-map.md             what every entry below is, and why
docs/style-guide.md          the prose, C# and SQL dialect
docs/testing.md              how to run every suite and every gate
docs/secrets.md              how a secret reaches a pod, and how each is rotated
docs/runbooks/               NOT one per alert — the one sharer is declared
docs/superpowers/            one frozen spec + plan per PR, written before it

global.json                  SDK pin (§4.4)
.config/dotnet-tools.json    dotnet-ef, pinned to the EF Core version
Directory.Build.props        shared MSBuild settings, ADR-019's analyser policy
Directory.Packages.props     central package management, exact pins
Platform.slnx                thirty-three projects
coverage.runsettings         the report filtered to `.*\.Domain\.dll$` (§12.9)
.editorconfig                house style; a build input, not a hint
.github/workflows/           ci, compose, helm, observability, deploy,
                             closure-gate, broker-permissions, realm
.github/licence-gate/        Appendix B against every package actually pinned
.github/secret-scan/         §15.1's twelve rules — and a library since #161
.github/closure-gate/        what a PR says it closes against what merging will
.github/pipeline-gate/       PR-25's three inventories over §15.1's stages
.github/coverage/            the domain-coverage reporter — a report, not a gate
deploy/canary/               §15.5's ladder, its arithmetic and its verdict
deploy/keycloak/             §11's realm obligations, over any realm — the one
                             gate whose subject is not a file this repo holds
deploy/compose/              §14.1's infrastructure, one pair per service
deploy/helm/                 §15.3's charts — one library chart, four users
deploy/observability/        §13.8's dashboards, §13.6's rules, §13.7's k6 run
tools/new-service/           §4.5's scaffold, with Catalog as its template

src/BuildingBlocks/          all five, and complete since PR-15 — Common.Domain,
                             .Application, .Contracts, .Infrastructure, .Web
src/Gateway/Gateway.Api/     the edge, and the second host
src/BFF/Web.Bff/             the third host, and the one synchronous caller
src/Services/Catalog/        §4.1's five projects; the one gRPC server
src/Services/Ordering/       the same five, plus §5's aggregate and §9.6's saga
tests/                       per service .Domain.Tests, .Application.Tests,
                             .Api.Tests and .TestSupport — the last is NOT a
                             test project (§4.1). Plus Common.*.Tests,
                             Gateway.Api.Tests, Web.Bff.Tests,
                             Web.Bff.TestSupport and Platform.IntegrationTests
```

Planned, per §4.1 — do not invent a different shape for it. `src/Services/`
gains Inventory and Payments with the same five projects, Shipping with a
Worker in place of the Api, and Notifications with four (no Domain, and a
Worker). `deploy/` still owes `k8s/` — `helm/` landed with PR-23, and the two
are not alternatives: §4.1 gives `k8s/` the raw manifests "where Helm is
overkill", which is a decision no chapter has yet taken about any particular
object.

**Aspire is not adopted** — Compose is the baseline (§14.1), nothing references
an `Aspire.*` package, and §4.4 therefore pins none. `src/AppHost` is §14.2's
optional host, and the map carries what adopting it would cost: a line per
resource per service to back out again, not one deletion.

### What the map is load-bearing for

The arguments are in [`docs/repo-map.md`](docs/repo-map.md). These are the
triggers that fire before anyone thinks to open it.

- **An unused project reference is a claim about the dependency graph that
  nothing makes true.** All three edges between building blocks were drawn
  late and deliberately, each by the PR that first had a member it could not
  write otherwise — and **the reference existing is not permission to start
  using it**: §6.3's `TransactionBehavior` still counts through a port rather
  than reach across §4.2 for the `is IAggregateRoot` test it derives from.
- **Changing Catalog can break the scaffold, and the failure is loud.**
  Catalog is the template, read at run time, and the script refuses a file it
  has never seen — so a new file there is a decision it forces. If
  `py -3.12 -m unittest` in `tools/new-service` goes red after a Catalog
  change, reconcile the script in the same change.
- **A change touching `tests/Catalog.*` is not verified until a scaffolded
  service has been built.** That suite reads text and never compiles one, so a
  test using a helper the scaffold removes renders a service that does not
  build with every test green. The map carries the four-command dogfood, the
  cleanup that follows it, and why the cleanup must not be undone by copying a
  file back.
- **Adding a package means adding its backticked identity to Appendix B in the
  same change**, or the licence gate fails the build before anything compiles.
  It reads the project files as well as the central pins, because a
  `PackageReference` naming its own `Version` restores a package no register
  row was asked about.
- **A `.TestSupport` project is not a test project (§4.1)**, and it exists for
  two suites sharing a fixture and unable to reference each other. A host
  usually gets none — `Gateway.Api.Tests` carries a second copy of Catalog's
  `TestAuthHandler` deliberately, because §4.3 permits exactly one assembly
  to cross a service boundary and a test helper is not it. **`Web.Bff` is
  the exception and the tree above lists it**: `StubCatalog` compiles the
  server half of a `.proto` whose client half `Web.Bff` already compiles,
  and both in one assembly makes every message type a CS0436. Saying a host
  gets none full stop contradicts this file's own tests line.

**Precedence where two documents disagree**, each already settled: Appendix C
beats `docs/roadmap.md`, §12 beats `docs/testing.md`, §15.4 beats
`docs/secrets.md`, and the blueprint beats `docs/superpowers/` — whose specs
are a frozen record and are **never** edited to match the code that followed
them. `.remember/` is session state, not content; never edit it as part of a
change.

## Which phase are you in

**Every mandatory PR in Appendix C's plan has landed, the one optional PR has
landed, and Appendix C now carries an "After the plan" section.** So "there is
no next PR" is not the right sentence: the plan being finished is not the same
as the blueprint being built, and a deferral to a complete plan is a dead
reference rather than a schedule. Live today are the blueprint, the foundation,
all five building blocks, §14.1's Compose infrastructure, Catalog and Ordering
as the first two services, §4.5's scaffold, §8's Redis with §8.5's idempotency,
all three of §9's instalments, PR-16's security, the gateway, the BFF, §15.3's
Helm charts, §13's signals, §15.1's staged pipeline, §15.5's canary, and §11's
realm obligations checked against the local realm today and against a deployed
one at the first rollout and hourly between rollouts.

**What each of those PRs decided is
[`docs/pr-decision-log.md`](docs/pr-decision-log.md)'s job, and this section no
longer retells it.** It had grown back into a second changelog — a paragraph
per PR summarising an entry the log already carried in full — which is the
duplication the extraction was for, re-arriving in the half that stayed. Read
the log's `## PR-NN` heading for the one you are about to build on. What stays
here is the state, and the rules under it that are still live.

**That section holds several kinds of row, and the kinds — not their number —
are what to read before adding another.** PR-28 was a mechanism the plan never
rowed, PR-29 was half a node it split without saying who owned the other half,
and PR-31 was a control the plan never claimed to close — all three gaps in
coverage. PR-30 is neither: §9.8 printed `e.UseInMemoryOutbox(context)` on the
saga endpoint, PR-21 built exactly that, and the plan delivered what it
specified. **The specification was wrong.** So a row can record a correction to
a landed row, and what earns it one is not the size of the diff but whether a
rule moved — ADR-032 took an exception to §9.3's prohibition on a second outbox
table set, which is a rule four chapters rest on. A fix that moves no rule is a
commit body, not a row. **PR-32 is a kind again**: neither a gap nor a
correction, but a residual PR-28's own row *names as owed*, so the debt was
declared in the section and the only thing missing was a row saying who would
pay it. It passes the same test — ADR-037 retires the exception in §8.5's
opening guarantee — which is what a row records and a commit body cannot.

**PR-33 is PR-30's kind, arriving against a row two rows old**: PR-32
delivered what ADR-037 specified and filed two issues against its own margin
on the way past, and closing them moved the rule ADR-037 had just set —
`RetentionPolicy`'s floor is the claim's window exactly, where ADR-037 made it
the claim's window plus an allowance (ADR-038). A row records that, and the
size of the diff is not what earned it — which is why no inventory of that diff
appears here: the sentence's whole point is that the count is irrelevant, and a
count written beside it is one more figure nothing recomputes.

**PR-34 is PR-33's kind against PR-33's own row, and it also carries a gap**:
ADR-038 stated the clock term it could not close, filed #171 rather than
folding the fix into a PR about something else, and closing it moved that rule
again — the floor now bounds how long §8.5's guarantee lasts rather than
whether it holds (ADR-039). The gap travelling with it is ADR-033's, a control
the plan never claimed to close: the token lifetime was verified in one realm
and stated everywhere else, and no host now accepts a token with more than the
revocation bound left to live (ADR-040). **That contains the gap and does not
close it** — the guard gates *remaining* life, so a five-hour token is refused
for most of its life and admitted in its last window, and #157 stays open in
full for the deploy-time check it actually asks for. **Two kinds in one row is
not a fourth kind** — one pull request is one row, which is the rule PR-32's
and PR-33's rows already state.

**PR-35 is PR-32's kind and the third consecutive row to close the residual the
row above it named**: ADR-039 bought back the atomicity its own split cost by
making the delete name the rows the select returned, and said in the same
consequences that `(Key, CommittedAt)` names them *by construction and not by
constraint* — nothing enforces uniqueness on a `datetimeoffset(7)`, so a
replacement stamped at the selected row's exact tick was deleted with its claim
live (#173). Closing it moved that rule to a constraint of the schema: the
delete joins a `rowversion`, which is unique and monotonic per database and
reads no clock (ADR-041). **It is a fifth clock fault of a different kind from
the four before it** — those need a drift of sufficient magnitude and were each
closed at the source, where this one needs an exact coincidence and cannot be
out-predicated at all. **Three rows have now each closed what the row above
named, and this is the first with nothing to hand on**: #127 is §8.5's, it is
unchanged, and it is the only residual left on this mechanism.

**PR-36 is PR-32's kind against a row two rows old, which is PR-33's arrival
and not PR-35's**: PR-34's row named #157 as a gap it narrowed and did not
close — a host can see how much life a token has left, but not the lifetime
that issued it, nor which grant minted it, nor whether a refresh token went out
beside it. `deploy/keycloak/` reads the realm instead of the token, and the
same predicate judges §14.1's Compose export in CI and the realm a deployment
points at from `deploy.yml`'s rollout job
([ADR-042](docs/backend-architecture/appendix-a-adrs.md#adr-042--the-deployed-realm-is-checked-at-deploy-time)).
**The rule that moved is ADR-033's division** — verified where the platform
provisions its own infrastructure, stated as an obligation where it does not —
which turns out to be two claims in one clause: a repository can **observe**
what it does not **own**, and only the second half was load-bearing. What made
them look inseparable was where a check would sit, and ADR-040's refusal of a
pipeline check is true of CI and says nothing about a rollout job under the
`production` environment. **ADR-036's broker is the case that proves the rule
rather than an oversight**: nothing reads a deployed broker either, because a
broker's permissions are not legible in a document a rollout can fetch. The
residual is the window between rollouts (#176), and #157 closes.

**PR-37 is PR-32's kind against the row directly above it**: PR-36's row named
#176 as owed — the realm was read at one moment, the moment was a rollout, and
an edit made afterwards was unobserved until the next one, which on a
stabilised service is never — and PR-37 pays it. **The rule that moved is
ADR-042's "a rollout is the only moment a deployed realm is read"**, amended
rather than rewritten by
[ADR-043](docs/backend-architecture/appendix-a-adrs.md#adr-043--the-deployed-realm-is-checked-between-rollouts):
`realm.yml`'s `deployed` job runs the rollout's own three calls hourly, over
every release the canary plan names, and a red run files an issue rather than
a notification, because a notification is read once and a drift persists
across the hours. **The residual is the schedule's own silence, stated rather
than filed**: GitHub suspends a `schedule` after sixty days without a commit
and does so without a red run, nothing in this repository can observe its own
absence, and what closes it is a monitor outside GitHub — an operating
decision the README names and does not take.

`Platform.slnx` holds thirty-three projects, thirteen of them test projects,
and `dotnet test` runs 1,119 tests — so the build rules and the drift rules
below are live and a green run means something.

**That number is a claim to reconcile rather than a fact to read**, exactly
as the compose timeout is: it is restated here, and nothing recomputes it. The
cheap check is a CI run that has already happened — `gh run view <id> --log`,
summed over the thirteen per-project totals — which beats the arithmetic that
would otherwise have to guess. PR-20 found the figure eight low against
`main`'s own run and needed that command to tell which side was wrong; PR-21
summed a local `dotnet test Platform.slnx` the same way, which is the same
arithmetic over an artefact one machine older.

**PR-11 was where a second suite and a second runner first appeared**, and
there are several more now — see [`docs/testing.md`](docs/testing.md), which is
where the current set lives, and which no longer states a count either. **The
owner is now a different file, which makes the point rather than weakening
it**: a count restated across a file boundary goes stale on the far side's
clock and reads as authoritative on this one.

**§4.2's architecture rules are a build failure, not a review comment.** Each
gate was observed red against a deliberately added forbidden reference before
it was trusted. Since PR-10 the endpoints gate judges real types rather than
passing vacuously. **The composition-root gate has no selector at all**, and
that is the fourth shape rather than the first: a namespace, then a namespace
*pattern*, then two ways of excluding compiler-generated types, each of them
selecting less than it claimed. It now judges the assembly whole and subtracts
the composition root from the **failures**, so there is no candidate set to be
narrow and no empty selection to pass vacuously — and its companion test's
subject is the exemption, not the selection.

**Since PR-22 all five rows of the table are gated, in two shapes.** A row
saying what a project *may* reference gets an allow-list over
`GetReferencedAssemblies` (Domain, Application, Migrator); a row saying it may
reference any package gets a deny (Infrastructure, Api). The cross-service rule
is one test over all five assemblies and names **no service**: it asks whether
a referenced assembly is strong-named, because every package this platform pins
is and none of its own projects is — `Dapper` alone excepted, and named. That
predicate exists because §4.5's scaffold renames the template's own name inside
whatever it renders, so a list of service names reaches a new service with the
one name it most needs *replaced* rather than joined. **A gate the scaffold
copies cannot be keyed on a name the scaffold rewrites.**

**A third gate says nothing references the `*.Migrator`**, and it exists
because the cross-service one cannot: that test subtracts every assembly under
this service's own name, so an `Api → Migrator` edge is invisible to it while
being forbidden all the same — no row names the migrator as something a project
*may* reference. Observed red against a deliberate `Catalog.Api →
Catalog.Migrator` reference **that a line of `Program.cs` actually used**, which
is the only way to observe this family red at all.

**All five read *emitted* references, which is narrower than the word the table
uses.** `GetReferencedAssemblies` reports the `AssemblyRef` table, so a
forbidden reference nothing *uses* emits nothing and the gate stays green until
code names a type across that edge — **late rather than absent**, and the
escape needs the reference to be both forbidden and entirely unused. Closing it
means reading the declared graph, which is a repo-wide build change whose own
failure mode is the vacuous pass named above. §4.2 states the reach and the
cost; the instrument is **owed**.

**`Common.Application`'s pipeline is complete at four since PR-28**, and
`IdempotencyBehavior` (§8.5) sits between Validation and Transaction. Both
neighbours are load-bearing: inside Validation, so a malformed command is
refused without burning the caller's `CommandId` for a day; outside
Transaction, so the claim is held before any work starts.

**Since PR-32 those two behaviours are one mechanism split across the seat
boundary, and neither half is complete alone.** §8.5's claim is the fast,
atomic exclusion that makes a concurrent duplicate fail early; §6.3 writes a
durable marker under the same key *inside* the transaction and reads it at the
top of it, which is what makes the ambiguous case — a commit whose
acknowledgement was lost — decidable at all
([ADR-037](docs/backend-architecture/appendix-a-adrs.md#adr-037--the-idempotency-marker-is-a-row-in-the-commands-own-transaction)).
The key crosses on `IdempotencyContext`, a scoped carrier, because §6.3 is
constrained to neither `IIdempotentCommand` nor `ICurrentUser` and rebuilding
it there would be a second specification of a value §8.5 spends three callouts
arguing. **§6.3 reads that carrier once, before anything runs** — a nested
dispatch would overwrite it mid-transaction — and the marker's own retention
window is the only one of the three with a floor, because purging it early
re-opens the duplicate at a boundary set by a housekeeping setting.

**Since PR-33 that floor is the claim's window exactly, and the ordering of
the two *start* events holds by construction rather than by a margin.** It
carried a five-minute allowance for two terms nothing bounded — the claim was
re-armed after a commit the marker was stamped before, and one row was aged
across two pods' clocks — and both are closed at the source
([ADR-038](docs/backend-architecture/appendix-a-adrs.md#adr-038--the-marker-and-its-claim-are-ordered-by-construction-not-a-margin)):
§8.5's completion preserves what the claim had left, and `CommittedAt` is
written and compared on the database's clock. **The marker is the one retention
table aged that way**, and §9.5 keeps the outbox and the inbox on the registered
`TimeProvider` deliberately, so a change moving all three to one clock is a
change to a decision rather than a tidy-up.

**Since PR-34 the purge does not out-count the claim at all — it asks it — and
that is the third margin this mechanism has refused**
([ADR-039](docs/backend-architecture/appendix-a-adrs.md#adr-039--the-markers-purge-asks-the-claim-rather-than-out-counting-it)).
The two windows were still counted by Redis's clock and the database's with
nothing coupling the rates, so a forward step of the database's purged a marker
whose claim was live — worst at the floor, which is the value §8.5 recommends
narrowing towards. `RetentionPurgeService` now **selects** markers with the
same server-clock cutoff, asks `IIdempotencyStore.UnheldAsync` which of those
keys the claim store has let go, and deletes only those. **So the floor's job
changed and the code says so in three places**: it bounds how long the
guarantee lasts, not whether it holds, and the refusal message on
`RetentionPolicy.IdempotencyWindow` had to be rewritten because its stated
reason had become false. A shorter window no longer re-opens the duplicate; it
asks for a guarantee shorter than the claim already gives. **#127 is now the
only residual of the three**, and no value of that floor reaches it.

**Two commands opt in and the third is a decision, not an omission.**
`PlaceOrderCommand` and `PublishProductCommand` carry a `CommandId` and
`IIdempotentCommand`; `CancelOrderCommand` carries neither, because
`Order.Cancel` is already idempotent, its broker ingress has no principal to
claim under, and §9.5's inbox absorbs the duplicate one layer down. That
argument lives on the record itself — §8.5's rule is that opting in is a
decision and forgetting to is not meant to look like one, so a reflection gate
per service reads the *shape* of every command rather than trusting the author.

**`IIdempotentCommand` has two members, and the second is why a rename is
safe.** `static abstract OperationName` is the key's operation segment,
declared rather than read off `typeof(TCommand).Name` — the compiler refuses a
command that supplies none, and a gate refuses one that supplies its own type
name back. `PluggableInterfaces.All` is **complete at five** —
adding an interface there and nowhere else is the design, and adding one before
its PR is inventing a project early by another route.

**`AddCommonWebDefaults` is complete at all five of §13.2's pieces since
PR-16** — the gap it used to carry was JWT validation, and closing it brought
§11.4's port with it for want of anywhere else a `FrameworkReference` lives.

**`Common.Contracts` is complete but not closed.** The rule governing the next
addition is the one PR-15 suspended exactly once: a record belongs in the PR
whose code publishes or consumes it. A sixth service's contracts arrive with
that service.

**The same rule governs a *member*, and PR-21 is where that first mattered.**
`ShippingAddressV1` had carried four fields since PR-15 and the domain's
`Address` has five; nothing noticed, because nothing had ever populated the
contract. The PR that becomes a contract's first producer is the PR that finds
out what it is missing — and it is the last cheap moment to fix it, since the
same edit one release later is a §9.2 version bump with consumers on the other
side. **Read a contract against the type that will fill it before writing the
mapper, not after.**

### Lessons that travel

**These moved to [`docs/lessons.md`](docs/lessons.md), verbatim.** Each entry
generalises past the PR that found it and carries the measurement it was bought
with; together they were a quarter of this file, and each one is load-bearing
only while you are working in the area it covers.
[`docs/pr-decision-log.md`](docs/pr-decision-log.md) carries the full argument
behind all but one of them, and is worth reading beside it.

**Read it before working in an area an entry covers.** The entries are bold
lead-ins, so grep the file for the subject rather than the PR.

One is kept here, because its subject is every other rule in this repository
and it is the failure this repository repeats most:

> **A gate that silently stops covering the newest surface is this
> repository's most-repeated failure.** The only defence is a test whose
> subject is *what the gate is looking at*, not what it found.

### The commands

These are the ones the target solution uses:

```bash
dotnet tool restore                # dotnet-ef, pinned in .config/
dotnet restore Platform.slnx
dotnet build Platform.slnx
dotnet test  Platform.slnx         # needs a running Docker daemon
dotnet test  Platform.slnx --filter "Category!=Integration"   # 874 of 1,119, no daemon
```

**[`docs/testing.md`](docs/testing.md) is the operational reference and this is
the short form.** It carries every other runner — the scaffold's, the chart
gate's and the Python gates' — what each one needs, how to run a gate as
opposed to its suite, which five projects need Docker, the three CI stages and
what the coverage figure is measured over. **§12 wins where the two disagree**,
exactly as Appendix C wins over the roadmap.

**Three runners, and only one of them is `dotnet test`.** The scaffold's tests
are Python, the chart gate is bash over `helm template`, and the licence gate,
the secret scan, the observability gate, ADR-036's broker ACL, the realm gate,
the pipeline gate, the coverage reporter, the canary, the closure gate and the
review helpers are Python again;
none is in `Platform.slnx`, so a green solution says nothing about any of them.
**Each of them is tested and then run**, which is the pattern every gate here
follows — the licence gate was once left out of this list on the reasoning that
a gate is not a suite, and it is both.

**No count opens that sentence, and its removal is the fix rather than a
recount.** It said seven, then ten, and #61's secret scan made it eleven inside
the pull request that was correcting the sentence around it. What a reader can
check is whether that enumeration matches `docs/testing.md`'s block and the
workflows that run them; that check needs no numeral in front of it.

**"The workflows", not "`ci.yml`", and the correction is the enumeration's own
lesson arriving one entry late.** ADR-036's broker ACL runs in
`broker-permissions.yml` and `ci.yml` names no `rabbitmq` job at all, so a
check written against that one file failed on both legs: the suite was missing
from the enumeration *and* absent from the workflow the check pointed at. It is
the licence gate's omission again — a gate left out on the reasoning that a
gate is not a suite, and it is both — this time with an explicit "compare
these" instruction aimed straight at the gap.

**`py -3.12`, not `python`.** Every CI job that runs Python pins 3.12 and the
default interpreter here is 3.14 — a *newer* one is the hazard, because it
accepts APIs 3.12 does not, so the local suite goes green on code the runner
cannot execute. `Path.read_text(newline=…)` is 3.13 and cost a CI round exactly
that way. The scaffold *script* is a different matter: running it is not a test
of the floor, so plain `python` is fine there.

**`dotnet test` requires Docker from PR-08, and the container tests are never
*skipped* when it is absent**: a skip on a missing daemon **fails open**, so CI
would go green on a runner whose Docker broke. ADR-010 already made real
infrastructure non-optional. Without a daemon they fail on `Failed to connect
to Docker endpoint`, which is a true statement about the machine and not a
defect in the branch.

**Since PR-22 they are *categorised*, which is the opposite of a skip and used
to be refused alongside it.** A skip runs the suite and reports a pass; a
category runs a smaller suite and says which. `Category!=Integration` is 874 of
the 1,119 and starts no container — measured with `docker events`, not
inferred — and `Category=Integration` is the other 245, needing the daemon
exactly as before.

Adding a migration needs the pinned tool and a startup project:

```bash
dotnet ef migrations add <Name> \
    --project src/Services/Catalog/Catalog.Infrastructure \
    --startup-project src/Services/Catalog/Catalog.Migrator \
    --output-dir Persistence/Migrations
```

### Build policy

Central package management means versions live in `Directory.Packages.props`
with **exact** pins — never add a `Version=` attribute to a `PackageReference`.

`Directory.Build.props` carries the analyser policy of **ADR-019**:
`TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`, `AnalysisLevel
latest-Recommended`, and no StyleCop. A warning stops the build, so a change
that provokes one is not done until the warning is gone — and `#pragma` is not
the way out. A genuinely warranted suppression goes in `Directory.Build.props`
with a comment.

Three live there, each arguing its case in the file:

- **CA1707** off for projects whose name ends `Tests`. §12's test names are
  sentences written with underscores, and the rule forbids them. Scoped by
  name — `EndsWith('Tests')`, not `'.Tests'`, because
  `Platform.IntegrationTests` ends with the word and not the dotted suffix.
- **CA1716** off repo-wide. It flags a type whose name is a reserved word in
  another .NET language, and `Error` (§10.5) is one in VB. Nothing here is a
  published library — §4.3 lets exactly one assembly cross a service boundary —
  so the scenario the rule protects does not exist.
- **CA1711** off repo-wide. It bans a reserved suffix and fires on
  `NextDelegate` (§6.2), where the suffix is not incorrect at all — the type is
  a delegate. Admitted on CA1716's terms. It costs the rule everywhere, so a
  later `OrderCollection` that is not a collection stops being caught.

**A fourth is a decision about the policy, not about the file in front of
you.** Argue it in the comment or do not add it — and prefer changing the code.
PR-04 met CA1848 by moving `LoggingBehavior` onto `LoggerMessage.Define` rather
than waiving a rule whose whole subject is the hot path that behaviour sits on;
PR-08 met CA1725, CA1863, CA1305 and NU1903 the same way and added no fourth
suppression.

`EnforceCodeStyleInBuild` only bites on rules set to `warning` or above, and
exactly three are: **IDE0055** (formatting), **IDE0065** (`using` placement)
and **IDE0161** (file-scoped namespaces). The rest of `.editorconfig` is
documented and unenforced on purpose — the four `var` carve-outs below are the
reason, and raising a rule whose exception lives in prose would fail builds
that are correct. Verified end to end: each of the three fails a build, and a
compliant file is clean.

`Common.Web.Tests` also carries an `AssemblyInfo.cs` disabling xUnit's
parallelisation for the project, and the reason belongs beside the analyser
policy because it is the same kind of decision — a rule scoped to the whole
assembly rather than argued file by file. OpenTelemetry's ASP.NET Core
instrumentation subscribes a **process-wide** `DiagnosticListener` the moment
any test builds a host through `AddObservability`, and while it is live the
hosting layer starts a server `Activity` for every request in the process —
including one an unrelated test class sends through its own `TestServer`. That
is exactly the ambient state §10.4's correlation-ID fallback test sets
`Activity.Current` to null to rule out, and it failed about half the time. A
shared xUnit collection was rejected for failing open: the next class that
builds an observability host and forgets to join it would silently reintroduce
the flake, where the assembly-wide attribute leaves nothing to forget.

## The one rule that matters

**The blueprint must not contradict itself.** It is ~24,600 lines that describe
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
- **This file and the five it delegates to are inside the rule too** —
  `docs/pr-decision-log.md`, `docs/lessons.md`, `docs/harness-boundaries.md`,
  `docs/repo-map.md` and `docs/style-guide.md`. None is reached by
  `/validate-blueprint` or `/check-links`, so nothing structural will catch
  their drift, and the delegation makes that sharper rather than softer: a
  rule that used to sit in one file now sits in two, so **the change that
  moves it has to reach both**. A pointer here and an argument there is one
  claim in two places, which is the shape this rule exists for — and *The
  tree* and *Style* each hold a deliberate short twin of what a sibling
  states in full, which is that shape charged for on purpose rather than
  arrived at by accident.

Run `/validate-blueprint` after any substantive edit.

## Style

**The dialect moved to [`docs/style-guide.md`](docs/style-guide.md),
verbatim.** It holds the prose conventions, the C# style — layout and naming,
statements and types, wrapping, whitespace and suppressions — and the SQL
style, with each rule marked for whether the build enforces it or a reviewer
carries it. **One dialect, both phases**: the fenced samples in the blueprint
and the compiled solution are written the same way, so a sample and its
implementation read identically.

**Read it before writing or reviewing prose, C# or SQL**, and before
"correcting" anything — a large part of that file is the record of house forms
a reviewer read as oversights, including a *Settled choices* table naming the
forms that are decisions about the whole corpus rather than about the file in
front of you. `/style-pass` is how a corrected form reaches the corpus,
`.editorconfig` and the guide in one change.

**The rules below stay here because they have to be true of an edit made
before anyone opens the guide.** They are the same rule in two files, which is
the cost the one rule above charges for by name: a change to one of these has
to reach both, and the guide is the master copy.

- **Wrap prose at 80 columns**; tables, links and code blocks may exceed it.
  Code is budgeted at **120**.
- **File-scoped namespaces** (`namespace X;`), never block-scoped, with a blank
  line after the declaration. This is the house form an external reviewer flags
  most often and the guide's *Settled choices* table exists to refuse; the
  others it names are extension declarations, expression-bodied members, the
  braces rule below, and .NET 10 with C# 14.
- **British spelling in prose** — `behaviour`, `serialisation`, `licence`.
  **Identifiers keep their real spelling**: `IPipelineBehavior`,
  `AddAuthorization`, `[Authorize]`. Never "correct" a type name into British
  spelling, and never Americanise the prose around one to match a type name.
- **Em dashes** (`—`) for asides, **en dashes** (`–`) for ranges, both literal
  Unicode rather than `--`. Cross-references take the section sign — `§9.3`,
  linked on first mention in a passage.
- **Chapter headings are numbered** (`# 9. Messaging`, `## 9.1 Integration
  events`) and third-level ones are unnumbered prose titles; **every chapter
  ends with a rule and a nav footer**, separated by ` · `, with one `---`
  and never two. The exact footer shape is in the guide, and `/new-chapter`
  scaffolds it. This bullet is here because that command sends a chapter
  author to *this* file for house style, and the conventions it means moved
  — a pointer that lands on nothing is worse than one hop too many.
- **Callouts are blockquotes whose opening sentence is bold**, no emoji and no
  admonition syntax. `**Trap — …**` and `**Decision — …**` are the two named
  forms; the guide keeps their counts and the argument for not keeping a total.
- **Explicit types for locals**, with four carve-outs and only four: the
  right-hand side names the type, anonymous types, tuple deconstruction, and
  fluent resource DSLs. A reader of a fenced block has no hover and no
  go-to-definition.
- **A list is on one line, or one element per line — never a ragged middle**,
  and a broken fluent chain puts every call on its own line, including the
  first.
- **A single statement may omit braces; two or more always take them — and so
  does one that wraps.** The one exception is a multi-line condition, whose
  body takes braces precisely so the four-space continuation is safe.
- **One space before `=`, `=>` and `{`, never a column of them.** IDE0055
  makes a padded column a failed build — measured on a member's `{`, an
  object initialiser's `=` and an expression-bodied member's `=>`. The one
  exception is a padded `=` in a *local* declaration, which
  `.editorconfig` declines to police, and the only aligned columns in the
  corpus are SQL inside a raw string literal, which no analyser reads.
- **No `#pragma` suppressions and no real credentials**, in a sample or in
  source. A warranted suppression goes in `Directory.Build.props` with a
  comment; §14.1's local-development defaults are the one stated exception to
  the second half.

## Working in this repo

- **Read before you edit.** Chapters run to 2,000 lines; the claim you are about
  to change is usually stated more than once.
- **Changing the chapter set** means updating four places: the file itself, the
  chapter table in `docs/backend-architecture/README.md`, the nav footers of
  both neighbours, and any `§n` cross-references that shift.
- **New ADRs** append to `appendix-a-adrs.md` with the next free number
  (currently ADR-044) and keep the
  `**Decision.** / **Why.** / **Consequences.**` three-part form. ADRs are
  never renumbered; supersede rather than rewrite.
- **New dependencies** — whether mentioned in a chapter or added to
  `Directory.Packages.props` — must reach the licence register in
  `appendix-b-licences.md` with their licence and role. Versions live in
  `Directory.Packages.props`, not the register; state one there only where the
  version *is* the point, as with MassTransit 8.x. A package in a sample or a
  csproj but not the register fails the CI licence gate.
- **Commit messages** are semantic and present-tense: `docs:`, `feat(<scope>):`,
  `fix:`, `chore:` — the delivery plan in Appendix C already names each PR in
  this form, so use its title verbatim when you implement one.
- **A `Closes #n` in a commit body fires on merge whatever the PR description
  says, and a keyword in a table cell fires for nothing.** Those are two
  different mechanisms and they have failed in opposite directions here. The
  house body form keeps `| Closes | #88 (high) |` as the human-readable
  summary and adds a bare `Closes #88` line below it, because a cell boundary
  between the keyword and the reference means GitHub is handed no
  keyword-reference pair at all — PR #112 linked nothing and three issues were
  closed by hand. In the other direction a commit keyword is permanent where a
  description is editable, so **the description is reconciled to the commits,
  never the reverse**: PR #116 withdrew two closures from its body and closed
  them anyway. `.github/closure-gate/` compares what the pull request *says*
  against what the merge *will do*, on every push and on every description
  edit. **It does not require a commit to repeat a closure the description
  makes** — that pairing is deliberately absent and a test pins the absence.
- **Every issue carries a kind label and a severity label, whatever filed
  it.** The severity is exactly one of `critical`, `high`, `medium`, `low`, and
  that half is already true of the whole tracker — measured, 93 issues and 93
  severities. The kind is `security`, `bug` or `documentation`, and **the
  vocabulary is wider than the helper**:
  `.claude/scripts/gh-label-ensure.sh` creates six labels and `documentation`
  is not among them, because it is one of GitHub's own defaults and was already
  in use — 11 issues carry it. So "the six the helper will create, and no
  others" is the rule this bullet first stated and it was **false against the
  tracker on the day it was written**, which is the restated-inventory failure
  arriving inside a rule about keeping two statements reconciled. Count the
  labels before writing down which ones exist. Both
  sweeps make this step 4 of their loop, so the route that has been getting it
  wrong is the other one: an issue filed by hand out of a review triage or a
  measurement taken mid-PR, where no command's checklist is being followed.
  #161 is that gap — its body argues **Medium** under a `## Severity` heading
  of its own and it shipped labelled `bug` and nothing else, with the severity
  stated in prose nothing can filter on. **A severity in the body and a
  severity on the issue are two statements, and the label is the one every
  consumer reads**: `/pr`'s `| Closes | #88 (high) |` row quotes it, both
  sweeps enumerate the tracker to de-duplicate against it, and a triage sorted
  by severity sees only the label. So the two are reconciled in the same act as
  everything else here — if the body's `## Severity` section and the label
  disagree, one of them is a bug report against the other, and the fix is not
  finished until both say it.
- **A reply is not a resolution, and the thread stays open until you resolve
  it.** `/review-copilot` carries the mechanics — the helper, the GraphQL
  mutation, and the rule that an `Ask` is left open on purpose — and what
  belongs here is that the obligation does not come from the command. It comes
  from having acted. **Triage by hand skips no step**: a session that reads the
  comments itself, fixes them and answers each one has done everything except
  the part a reviewer can see, and a PR whose threads all read "unresolved"
  looks exactly like a PR nobody answered. Measured, and this is why the bullet
  exists: seven threads across two review rounds were replied to and fixed, and
  not one of them was resolved until the repository owner asked. **Resolve in
  the same act as the reply** — the reply says what you did, the resolution
  says it is finished, and only the second one is legible from the pull request
  list.
- **A CodeQL alert is a defect in the pull request that raised it, and it is
  fixed there.** Not deferred to an issue, not dismissed as a false positive
  without reading the flow it reports: the alert names a source, a sink and a
  path between them, and the answer is either a change that breaks the path or
  a stated reason the path cannot be taken. **Prefer breaking it structurally
  over policing the sink** — the alert that produced this rule was a realm
  document flowing into a `print`, and the fix was to redact the credential at
  the door rather than to remember, at every future message, not to format a
  client. A dismissal is a decision like any other here: it goes in the commit
  body with its argument, and a PR that leaves an alert neither fixed nor
  argued is not finished.
- **Uncommitted work in the tree belongs in the PR being worked on.** When a
  change appears that nobody in the current task wrote — an edit made directly
  by the repo owner, most often — it is not stray churn to be reverted or left
  behind for someone else to notice. Commit it as part of the current PR, in
  its own commit, with a body that argues it like any other. **Never revert it
  to clean the tree**: that has happened once, and only a saved diff kept the
  work. If it genuinely does not belong in this PR, say so and ask — do not
  decide by deleting. The same reconciliation rule applies to it as to
  everything else.
- **TDD is the stated method** (§12), not a preference. Tests ship in the same
  PR as the code they cover — the convention starts at PR-02 and there is no
  PR in the plan that adds tests afterwards.
- **Follow the delivery plan's order.** Appendix C sequences its numbered PRs
  with
  explicit dependencies, and the service order is deliberate. Building out of
  order is a design decision, not a shortcut — raise it rather than taking it.
- **The architecture tests are the enforcement mechanism**, not review. If a
  change needs one of those gates relaxed, the gate is probably right and the
  design is probably wrong.
- **`Program.cs` in each `*.Api` is the only composition root** (§4.2). Wiring
  belongs in `AddXApplication()` / `AddXInfrastructure(config)`, not scattered.
- **`main` stays green.** Every PR in the plan is specified to leave it building
  and passing.

## Available commands

Each command's own file under `.claude/commands/` is the authoritative
description of what it does. **Do not restate their rules here** — a second
copy is exactly the drift the one rule exists to close, and this section has
been the source of it before. What follows is the index plus the facts that
cut across more than one command.

Content:

| | |
|---|---|
| `/validate-blueprint` | Multi-pass self-consistency audit across the blueprint, `docs/roadmap.md` and `docs/testing.md`; also code ↔ docs drift once `src/` exists |
| `/check-links` | Link, cross-reference and nav-footer integrity |
| `/new-chapter` | Scaffold a chapter and rewire its neighbours |
| `/new-adr` | Append an ADR in the established form |
| `/style-pass` | Apply one corrected code form corpus-wide, then record it in `docs/style-guide.md` and `.editorconfig` |

Delivery:

| | |
|---|---|
| `/ship` | The whole chain: clean `main` → `/branch` → checks → `/commit` → `/pr` → the two review loops → merge → teardown. **It stops for nothing that is a judgement** — check findings, `Needs a decision` rows and `Ask` threads are decided, recorded and carried past |
| `/branch` | Start a correctly named branch — **in a sibling worktree** the session moves into, from a clean `main`; in place when the tree is dirty or the parent is not writable |
| `/commit` | Split the working tree into semantic commits with arguing bodies |
| `/pr` | Open a PR in the house body form |
| `/review-grok` | Triage an external review into a resolution record |
| `/review-copilot` | Triage Copilot's PR comments — verify each before acting |
| `/review-branch` | Review the branch against `main` for contradictions; writes `suggestions.md` |
| `/security-sweep` | Loop a defensive security audit in a throwaway worktree, filing an issue per confirmed medium-or-above finding |
| `/bug-sweep` | The same loop aimed at defects — filed at **critical or high**, confirmed by reading because the grant runs no build |

### What cuts across them

**This moved to [`docs/harness-boundaries.md`](docs/harness-boundaries.md),
verbatim.** It is the inventory of what the harness grants these commands and
what it refuses them: the `Edit`/`Write` rule, the `.claude/` deny list and its
self-lock, the review sandbox and its two residuals, the numbered inventory of
grants wider than the operation they buy, the argv hook that closed the ones a
pattern could not, and what separates the two sweeps.

**Read it before touching anything under `.claude/`** — a command's
frontmatter, a helper script, `settings.json`, a hook or a subagent profile.
**State a new residual there rather than here**, and read the file before
claiming a grant is narrow: every entry in it was found by running the
offending form, not by reasoning about it.

Four of its rules reach every session whatever it is doing, so they stay:

- **File permission rules take `Edit(...)`, never `Write(...)`.** `Edit(path)`
  covers every file-editing tool, `Write` included; a `Write(path)` rule
  matches nothing and Claude Code **refuses to start** until it is removed.
  This has been "fixed" twice by adding the twin back, and both times it broke
  startup. **A reviewer who has not run the harness cannot see this** — check a
  permission claim against the harness before acting on it.
- **A grant is not a whitelist.** `allowed-tools` is an **auto-approval list**,
  so omitting a tool from a command's frontmatter withholds nothing — it only
  decides whether the call prompts. Refusing a capability takes a **deny**, in
  `permissions.deny` or the `disallowed-tools` frontmatter key. Precedence is
  **deny → ask → allow**, first match wins.
- **An allow rule is a prefix match and cannot exclude a flag.** Anything whose
  safety depends on what follows a token needs a **helper** or a **deny**,
  never a cleverer allow — `Bash(git reset HEAD --:*)` admitted the exact
  `--hard` it was narrowed to exclude, while its commit message said the hole
  was closed.
- **`.claude/settings.json` self-locks, and not instantaneously.** Once it
  denies itself the session cannot edit it again, so a change to it is one edit
  that lands complete and goes **last** in any PR that also touches
  `commands/` or `scripts/`. The lock takes effect on the harness's own
  schedule, so **verify a restore by reading the file, never by trying the
  thing it forbids** — a probe taken right after the write reports the deny as
  inert and is simply early.
