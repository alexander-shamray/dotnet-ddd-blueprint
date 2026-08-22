# CLAUDE.md

Guidance for Claude Code when working in this repository.

**The long half of this file's phase section now lives in
[`docs/pr-decision-log.md`](docs/pr-decision-log.md).**
Every landed PR from PR-08 on appended its findings here and nothing was ever
consolidated,
so a file loaded into every session's context had grown to 3,161 lines — of
which the per-PR changelog alone was 1,132. Those blocks moved out **verbatim
in their arguments**: not one was shortened, because a summary of an argument
is how a rule gets "corrected" back. The log's own header lists what *was*
edited on the way out. The rest of what left went for a different reason —
duplication of Appendix D and of `.claude/commands/*.md`, argued below — and
the changelog was the largest single part of it. **No line count of this file
appears in this paragraph on purpose**: a file that states its own length
invalidates the claim with the next edit, including the edit that fixes it.
What stayed is what an agent needs
in order to *act*: the repo's shape, the phase, the one rule, and the style the
prose and the code are written in.

**Read the log before working in an area it covers.** It is where the traps are
recorded; this file keeps only the lessons that generalise past the PR that
found them.

## What this repo is

`dotnet-ddd-blueprint` is a monorepo for an ASP.NET Core microservices platform
built with DDD, CQRS and TDD. It is two artefacts with one specification: the
blueprint under `docs/backend-architecture/`, and the C# solution that
blueprint specifies — thirty-three projects, and counting. **The phase section
below carries what has landed**; this sentence only names the shape.

**The blueprint is the specification for the solution.** Appendix C sequences
that code into 27 pull requests. Treat every chapter as a commitment the
implementation will have to honour.

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
project *holds* is the job of `appendix-d-type-inventory.md` and §4.1; a second
copy here would be a second thing to reconcile, which the one rule below
forbids. This tree says where things are, not what is in them.

```
docs/backend-architecture/   the blueprint — README index, 01-purpose ..
                             15-cicd-deployment, appendix A (ADR-001..023),
                             B (licences), C (delivery plan), D (type inventory)
docs/roadmap.md              estimates and a calendar laid over Appendix C
docs/pr-decision-log.md      what each PR from PR-08 on decided — the other
                             half of this file's phase section
docs/testing.md              how to run the suites — the commands, the
                             Category=Integration filter, which five projects
                             need Docker, what the coverage figure measures.
                             §12 keeps the strategy and wins any disagreement
docs/secrets.md              how a secret reaches a pod and how each kind is
                             rotated — the operational half of §15.4 on
                             testing.md's exact terms, and §15.4 keeps the
                             inventory and wins any disagreement
docs/runbooks/               twelve, one per §13.6 alert, since PR-24. Plus a
                             README that is EXCLUDED from the pairing by name —
                             one declared exception, so a second non-runbook
                             file has to be argued for
docs/superpowers/            one frozen spec + plan per PR, written before it

global.json                  SDK pin (§4.4)
.config/dotnet-tools.json    dotnet-ef, pinned to the EF Core version —
                             `dotnet tool restore` is the whole setup
Directory.Build.props        shared MSBuild settings, ADR-019's analyser policy
Directory.Packages.props     central package management, exact pins
Platform.slnx                thirty-three projects
coverage.runsettings         the report filtered to `.*\.Domain\.dll$` (§12.9)
.editorconfig                house style; a build input, not a hint
.github/workflows/ci.yml     licence gate and scaffold tests, then
                             restore/build/test/report-coverage
.github/workflows/compose.yml  path-filtered smoke on deploy/compose/** —
                             config -q, up --wait, down -v, and an image build
.github/workflows/helm.yml   path-filtered smoke on deploy/helm/** — and on
                             every input smoke.sh reads outside it. **That list
                             is not written down here on purpose.** It drifted
                             three times; it now lives once, as SOURCE_INPUTS
                             in smoke.sh beside the reads, and the gate asserts
                             both of the workflow's triggers cover it. The one
                             place a deploy/** workflow reaches outside its own
                             tree — and no longer the ONLY one; see below
.github/workflows/observability.yml  path-filtered check on
                             deploy/observability/** — and on src/** and
                             docs/runbooks/**, which check.py declares as its
                             own SOURCE_INPUTS beside the reads and asserts
                             both triggers cover. The second workflow to reach
                             outside its tree, and it adopted the Helm tree's
                             lesson before paying for it once
.github/workflows/deploy.yml §15.5's canary, `workflow_dispatch` ONLY — a
                             deploy on `push` would fail on every merge for
                             want of a cluster, and a pipeline red by design
                             trains everyone to ignore it. Its `check` job DOES
                             run on pull requests, and is the THIRD workflow to
                             reach outside its tree: deploy/helm/**, src/** and
                             deploy/observability/**, declared as SOURCE_INPUTS
                             in canary.py. It adopted the lesson and paid for
                             it anyway — the list shipped omitting the
                             observability entry that two of its own checks
                             read, and the trigger assertion stayed green,
                             because a gate cannot see a read it was never told
                             about. A test over the reads is what closes that,
                             not a more careful list
.github/licence-gate/        the gate, its allow-list and its tests
.github/pipeline-gate/       PR-25's quality gates, and all three are
                             inventories: every deployable under src/ is
                             matched by a path filter, every Dockerfile is
                             built by some matrix entry, and every test stage
                             ran, ran enough, and ran ONCE. Tested, and every
                             test is a negative case — a gate only ever
                             observed green is one nobody has established is
                             looking at anything
.github/coverage/            the domain-coverage reporter. Still a report and
                             not a gate: PR-25 was the PR entitled to add a
                             threshold and declined, on §12.9's own argument
                             that a diagnostic wired to a build failure stops
                             being read. It has a suite since PR-25 all the
                             same, because it now MERGES across stages, and
                             arithmetic that is quietly wrong is worse than
                             no figure
deploy/canary/               §15.5's rollout since PR-25 — the ladder as JSON,
                             the weight arithmetic and the promote/rollback
                             verdict as tested stdlib Python, and one file that
                             reads Prometheus. It reaches no cluster; the
                             deciding is tested and the acting is not
tools/new-service/           §4.5's scaffold — see the notes below
deploy/compose/              §14.1's infrastructure, plus one application pair
                             per service and the gateway on 5000, which has no
                             migrator beside it because the edge owns no database.
                             `rabbitmq/` is the one infrastructure image that is
                             BUILT — ADR-021's delayed-exchange plugin — so a
                             seventh build rides on the compose smoke
deploy/helm/                 §15.3's charts since PR-23. `common/` is a LIBRARY
                             chart holding every template once; Catalog,
                             Ordering and the BFF are values plus one-line
                             includes, the gateway adds `edge-config.yaml` for
                             the two keys no service has, and `platform/` is
                             the umbrella. `smoke.sh` renders all five and
                             asserts what comes out — it reaches no cluster,
                             and says so
deploy/observability/        §13.8's dashboards, §13.6's alert rules and
                             §13.7's k6 SLO run, since PR-24. TWO rule files,
                             and the split is the point: `platform-alerts.yaml`
                             is loaded, `awaiting-signal.yaml` holds the four
                             alerts whose instrument nothing publishes yet and
                             is NOT. `check.py` pairs alerts with runbooks both
                             ways, and asserts the awaiting file's metrics are
                             published by NOTHING — which is what makes that
                             list self-clearing instead of a list of alerts
                             nobody ever turned on. It reaches no Prometheus
                             and does not validate rule syntax, and says so

src/BuildingBlocks/          all five, and complete since PR-15
  Common.Domain/               Entity<TId>, AggregateRoot<TId>, IDomainEvent
                               and friends — no packages
  Common.Application/          Result and Error; §6.2's dispatcher; three of
                               §6.3's four behaviours; §6.5's CursorPage<T>;
                               §7.5's ports
  Common.Contracts/            §4.3's one assembly that crosses a service
                               boundary. No packages and no project references,
                               and both absences are the point — anything this
                               referenced would travel into every service
  Common.Infrastructure/       §8's Redis helpers, §9's outbox, inbox,
                               consumers and retention purge
  Common.Web/                  §10.4, §10.5, §11.3, §11.4, §13.2, §13.4 and
                               §13.5, and nothing else — the only building
                               block with a FrameworkReference
src/Gateway/Gateway.Api/     the edge, and the second host. One
                             ProjectReference (Common.Web), no Application and
                             no Infrastructure — §10.1 gives it no domain and
                             no database, so neither layer has anything to
                             hold. appsettings.json is the deliverable as much
                             as Program.cs is, and is under test
src/BFF/Web.Bff/             the third host, and the ONE that calls a peer
                             synchronously (§9.7, ADR-017) — which is what
                             makes it the only one holding client credentials
                             (§11.5). Same shape as the gateway
src/Services/Catalog/        §4.1's five projects — Domain, Application,
                             Infrastructure, Migrator, Api. The first real
                             service, the scaffold's template, and the
                             platform's one gRPC server
src/Services/Ordering/       the same five, rendered by the scaffold rather
                             than written, then given §5's Order aggregate —
                             and, since PR-20, §6.6's price projection with its
                             withdrawal watermark, behind the solution's first
                             receive endpoint. PR-21 gave it §9.6's saga, three
                             more receive endpoints and the solution's only
                             MassTransit EF persistence reference
tests/                       per service: .Domain.Tests, .Application.Tests,
                             .Api.Tests and .TestSupport — the last is NOT a
                             test project (§4.1). Plus Common.*.Tests,
                             Gateway.Api.Tests, Web.Bff.Tests,
                             Web.Bff.TestSupport and Platform.IntegrationTests
```

**A `.TestSupport` project is not a test project (§4.1), and it exists for a
reason rather than by convention.** The reason is two suites sharing a fixture
and unable to reference each other — which is why `Catalog.TestSupport` arrived
with PR-10's second consumer rather than with the service.

**That is why the project type exists; it is not when a service gets one.**
Since PR-11 the scaffold emits the library with the service (§4.5), so
`Ordering.TestSupport` arrived with Ordering and has exactly one consumer
today: `Ordering.Api.Tests`. `Ordering.Application.Tests` deliberately does not
reference it and says so in its csproj, because §12.1 homes handler tests at
that level and Ordering's live in the API suite instead. A **host** is the case
that gets none, so `Gateway.Api.Tests` carries its own `TestAuthHandler`
as a **second copy of Catalog's, deliberately**: §4.3 permits exactly one
assembly to cross a service boundary and a test helper is not it.
`Web.Bff.TestSupport` is the exception that proves the shape — one BFF suite,
but `StubCatalog` must compile the *server* half of a `.proto` whose *client*
half `Web.Bff` already compiles, and both in one assembly makes every message
type a CS0436, which ADR-019 turns into an error.

**Since PR-26 it holds the second thing in this repository shared as a linked
file rather than as an assembly**, and the two are the same relationship one
level apart. `pricing.proto` is Catalog's, because Catalog serves the RPC;
`PricingContract.cs` is Web.Bff's, because only a consumer can say what it
needs — and `Catalog.Api.Tests` compiles it through a `<Compile Link>` so the
provider can be held to it. A file crosses no boundary §4.3 draws, which is
what keeps a test helper from becoming the second assembly that does. **A third
copy of this pattern owes the scaffold an entry**: `tools/new-service` drops
both the link and the suite that uses it, because a contract copied to a
service no consumer calls is an expectation nobody holds.

Planned, per §4.1 — do not invent a different shape for it. `src/Services/`
gains Inventory and Payments with the same five projects, Shipping with a
Worker in place of the Api, and Notifications with four (no Domain, and a
Worker). `deploy/` still owes `k8s/` — `helm/` landed with PR-23, and the two
are not alternatives: §4.1 gives `k8s/` the raw manifests "where Helm is
overkill", which is a decision no chapter has yet taken about any particular
object.

Three things sit outside that tree because §4.1 does not draw them:
`global.json`, whose SDK pin §4.1's prose relies on for the `.slnx` floor;
`.config/dotnet-tools.json`, which pins `dotnet-ef` to the EF Core version —
the machine that built PR-08 had the 8.0.11 tool against a 10.0.0 runtime and
the error names neither; and `src/AppHost`, the optional Aspire host of §14.2.
**Aspire is not adopted** — Compose is the baseline (§14.1), nothing references
an `Aspire.*` package, and §4.4 therefore pins none. If it is adopted,
`src/AppHost` is the only project taking `Aspire.Hosting.*`, but each service
picks up the client integrations for the resources it consumes — so backing it
out again costs a line per resource per service, not one deletion (§14.2).

### Edges between building blocks

Three exist, and every one waited for a type that could not be written without
it. **An unused project reference is a claim about the dependency graph that
nothing makes true**, which is why each was drawn late and deliberately:

- `Common.Application → Common.Domain`, drawn by PR-14. §4.2 permitted it from
  the start; what it lacked was a member naming a domain type. §7.5's
  `IDomainEventCollector` returns `IReadOnlyList<IDomainEvent>` and settled it.
- `Common.Infrastructure → Common.Application`, `Common.Domain` and
  `Common.Contracts`, all three drawn by PR-14's outbox — `MessageTypeMap`
  selects on `IDomainEvent` **or** `IIntegrationEvent`, which is why the last
  two arrive together.
- `Common.Web → Common.Application`, the oldest, because `ToHttpResult` maps an
  `Error` and cannot be written without one.

**The reference existing is not permission to start using it.** §6.3's
`TransactionBehavior` still reads `ModifiedAggregateCount` as an `int`, because
the `is IAggregateRoot` test it derives from lives in `EfUnitOfWork`, on
Infrastructure's side of §4.2 — counting behind the port is what keeps it
there. `IUnitOfWork.ExecuteRawAsync` takes `string` and `object` for the same
reason.

### Files outside the blueprint tree, and why

- **The licence gate** lives under `.github/` rather than a `build/` directory
  because it is CI-only and §4.1 draws no such tree. Stdlib Python, reads
  `Directory.Packages.props` and Appendix B as text, needs no restore — which
  is why §15.1 can put it ahead of the build. **Adding a package means adding
  its backticked identity to Appendix B in the same change**, or the gate fails
  the build before anything compiles.
- **`docs/roadmap.md`** is a schedule, not a specification, and goes stale on a
  different clock. Nothing in it states a requirement. **Where it and Appendix C
  disagree, Appendix C wins**, always. Being outside the tree, no nav footer or
  index row catches its drift — `/validate-blueprint` check 10 is the only
  thing that does, which is why it is named in that command's scope.
- **`docs/pr-decision-log.md`** is beside the roadmap for the same reason —
  outside the blueprint tree, so in no index and behind no nav footer. The
  consequence is its own and is one step further: the roadmap at least has
  check 10, where the log is in the scope of **neither**
  `/validate-blueprint` nor `/check-links`. The one rule below is the only
  thing carrying it.
- **`docs/testing.md`** is outside the tree on the same terms and lands
  between the two: named in `/validate-blueprint`'s scope like the roadmap,
  reached by no link check like the log. It needs **no check of its own** —
  it is the operational half of §12, so every claim in it is a claim about a
  chapter or about the code, and checks 1–9 reach all of them. **§12 wins
  where they disagree**, exactly as Appendix C wins over the roadmap. The
  split is deliberate: a runner flag goes stale on a different clock than a
  strategy does, and a chapter that carried both would be edited for the
  wrong half.
- **`docs/superpowers/`** is a **frozen historical record**. Each pair — a
  design spec and the plan derived from it — records how one PR was thought
  through *before* it was built. **Where one disagrees with the blueprint, the
  blueprint wins**, and the disagreement is not a defect: it is the record
  showing where the design moved during implementation. PR-05's plan still
  carries a `SourceRevisionId, §4.4` citation the shipped code corrected, left
  standing deliberately. So these are **outside `/validate-blueprint`'s scope**
  and, unlike the roadmap, not named in it either — a drift check on a document
  whose whole value is being stale would fail on every entry by design. **Do
  not edit a spec or a plan to match the code that followed it**; amend the
  chapter instead.
- **`.remember/`** is session state, not content. Never edit it as part of a
  change.

### The scaffold

`tools/new-service/` took the opposite decision to the licence gate's, and the
difference is what each thing is. The gate is CI-only, so it lives under the CI
provider's directory. The scaffold is a developer tool that happens to be
tested in CI, so filing it under `.github/` would file it by its least
important property — **§4.1's tree gained a `tools/` entry and §4.5 documents
the script instead**, the honest fix for "the blueprint draws no such tree"
being to draw it.

**Catalog is the template, read at run time.** There is no template directory,
so there is one copy of the wiring rather than two that drift — and the
scaffold's tests render *this* repository.

**Changing Catalog can therefore break the scaffold, and the failure is loud.**
The script names exact text inside `src/Services/Catalog` and `tests/Catalog.*`
and every anchor must match exactly once. It also classifies **every** file
under those roots as template or slice and refuses to run on one it has never
seen — so a new file in Catalog is a decision the scaffold forces. If
`py -3.12 -m unittest` in `tools/new-service` goes red after a Catalog change,
reconcile the script in the same change.

**One class of breakage is silent, and only compiling the output catches it.**
The suite runs on stdlib Python with no SDK, so it renders a service and
inspects the text — it never builds one. A Catalog test using a helper the
scaffold *removes* renders into a service that does not compile with every test
green: PR-14 wrote a dispatcher test over `OutboxRows.Broker`, which leaves
with the first contract, and nothing said a word. **A change touching
`tests/Catalog.*` is not verified until a scaffolded service has been built**,
which is four commands and a cleanup:

```bash
python tools/new-service/new_service.py Yankee --port 5199
dotnet build tests/Yankee.Api.Tests/Yankee.Api.Tests.csproj
rm -rf src/Services/Yankee tests/Yankee.*
git checkout -- Platform.slnx deploy/compose/
```

The scaffold edits five tracked files as well as creating its own, so the
`git checkout` is part of the procedure rather than tidying after it. **Commit
before dogfooding**, though, if the PR itself changes `deploy/compose/` — that
cleanup reverts the tree's own changes, and it has cost work once.

**The probe is `Yankee` at 5199, and must not be a real service's name.** It
used to be `Ordering` at 5103; PR-18 made Ordering real, so the create refuses
the taken name and port — and the `rm -rf`, followed literally, deletes the
service. `Yankee` is one of the probes the scaffold's own suite uses, chosen
because a probe cannot quietly become a service later.

## Which phase are you in

**PR-01 through PR-24 have landed, and PR-27 with them** — out of numerical
order and in sequence, because Appendix C numbers it last and makes it depend
on PR-17 alone, so it may land at any point after the gateway. The blueprint,
the foundation, all five building blocks, §14.1's Compose infrastructure,
Catalog and Ordering as the first two services, the scaffold, §8's Redis, all
three of §9's instalments, PR-16's security, the gateway and the BFF are
therefore live. PR-20 filled the `ordering.ProductPrices` table PR-18 shipped
with its reader and no producer, and gave the platform its first receive
endpoint and its first broker-fed read model. PR-21 closed the roadmap's M5
with §9.6's fulfilment saga — the platform's first state machine, its first
scheduled message ([ADR-021](docs/backend-architecture/appendix-a-adrs.md#adr-021--saga-timeouts-are-scheduled-by-the-broker)),
its first command endpoint and the first thing Ordering publishes. PR-22 put
the rest of §4.2's dependency table behind gates, split the suite on
`Category=Integration` and started reporting domain-layer coverage. PR-23 gave
the platform its Helm charts and, with them, the first artefact in this
repository that **cannot be verified by anything the solution builds** — no
project references it, `dotnet test` says nothing about it, and its gate is a
shell script over `helm template`. PR-24 gave §13.6's alerts their signals —
`OutboxMetrics` and the `MetricsInitialiser` that forces every metrics type to
exist — plus the twelve runbooks, `docs/secrets.md`, the dashboards and the k6
SLO run. It also found that **four of §13.6's twelve alerts read an instrument
nothing publishes**, which is that section's own callout coming true the moment
the alerts stopped being a table and became files; those four ship unloaded and
a gate keeps the list honest.
PR-25 closed the roadmap's M6 with §15.1's staged pipeline — the path filter,
the per-service image build, three test stages where there was one run, and the
quality gate that says each of them actually ran — plus §15.5's canary, which
needed a mechanism no chapter had chosen ([ADR-022](docs/backend-architecture/appendix-a-adrs.md#adr-022--the-canary-is-a-second-release-weighted-by-replicas)).
It is the platform's **third artefact that no cluster has ever seen**, after
the charts and the alert rules, and the honest half of it is that the *deciding*
is tested and the *acting* is four commands nobody has run.
PR-26 was optional and conditional (Appendix C) and **has landed**, because the
condition was already met: §9.7's hop is the platform's one synchronous
dependency, and the consumer's model of it — `StubCatalog` — had drifted from
the provider in four places. It is the platform's first **consumer-driven
contract**, and the first artefact deliberately *not* built with the tool the
plan named: PactNet cannot express gRPC at all
([ADR-023](docs/backend-architecture/appendix-a-adrs.md#adr-023--the-consumer-driven-contract-is-a-linked-file-not-pact)),
so the property is taken and the machinery is not — one file, linked into both
suites, exactly as `pricing.proto` is. **Every mandatory PR in the plan has now
landed and so has the one optional PR, so there is no next PR.**

`Platform.slnx` holds thirty-three projects, thirteen of them test projects,
and `dotnet test` runs 812 tests — so the build rules and the drift rules below
are live and a green run means something.

**That number is a claim to reconcile rather than a fact to read**, exactly
as the compose timeout is: it is restated here, and nothing recomputes it. The
cheap check is a CI run that has already happened — `gh run view <id> --log`,
summed over the thirteen per-project totals — which beats the arithmetic that
would otherwise have to guess. PR-20 found the figure eight low against
`main`'s own run and needed that command to tell which side was wrong; PR-21
summed a local `dotnet test Platform.slnx` the same way, which is the same
arithmetic over an artefact one machine older.

**PR-11 was where a second suite and a second runner first appeared**, and
there are eight suites now — see *The commands* below, which is where the
current set lives, and which is the only place a count of them belongs. That one: `py -3.12 -m unittest` in `tools/new-service` runs
81, and CI has a `scaffold` job for them beside `licence-gate`.

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

**`Common.Application`'s pipeline is three behaviours of four.**
`IdempotencyBehavior` (§8.5) does not exist, and its seat is between Validation
and Transaction; `PublishProductCommand` carries no `CommandId` for the same
reason, since §6.4 warns the field without the interface is unprotected. Both
join with §8.5's PR. `PluggableInterfaces.All` is **complete at five** —
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

These generalise past the PR that found them.
[`docs/pr-decision-log.md`](docs/pr-decision-log.md) carries the full argument
and the measurement behind all but one of them; the exception says so on its
own line rather than sending a reader to a file that does not hold it.

- **A gate that silently stops covering the newest surface is this
  repository's most-repeated failure.** The only defence is a test whose
  subject is *what the gate is looking at*, not what it found.
- **Do not write down an ordering claim a test is not making.** Middleware- and
  behaviour-order rules are testable case by case, and which ones are has to be
  measured: `UseAuthentication` deleted breaks nothing a test catches, while
  `UseForwardedHeaders` moved does. Keep the line; do not believe a test is
  watching it.
- **A middleware acting on the *response* decides below everything under it.**
  Reasoning about what it "sees" from the position of its `Use` call is
  reasoning about the wrong moment.
- **The fix that lands in code and not in the sample it came from re-arms the
  defect for the next reader.** After fixing a line that came from a sample,
  grep the blueprint for *the line you replaced*, not for the topic.
- **A `ProjectReference` — and a linked `.proto` — is a `COPY` line in a
  Dockerfile.** Forgetting it breaks the images silently until someone runs
  one; `dotnet build Platform.slnx` cannot see it and neither can the
  path-filtered compose smoke.
- **Blank counts as missing.** An environment variable set to the empty string
  reaches `Configuration` as `""`, not null, so `GetRequiredSection` proves
  only that a section exists. Guard on the bound values.
- **A premise about who calls a method is falsified by the next PR that calls
  it.** Both `EfUnitOfWork`'s tracker and the `No_client_secret_is_committed`
  rule were correct until a second caller existed.
- **"A test that would pass" is a claim about a run nobody performed.** Measure
  the counterfactual — three such claims in one PR were all the opposite of
  what happens.
- **A measurement says what the code *does*; it never says what it *may* do.**
  Fetch the specification section and paste the sentence.
- **A rule whose stated test is a string match will be enforced as one**, by a
  reviewer or by whoever greps next. State the rule, not the grep. This one's
  argument is the `.ToArray()` narrowing in the C# style section below, not the
  log.
- **`ASPNETCORE_ENVIRONMENT=Development` leads every host-run block naming an
  authority.** No project ships a `launchSettings.json`, so `dotnet run`
  selects Production, where `RequireHttpsMetadata` is on and a plain-HTTP local
  authority is never reached. Containers set it, which is why the Compose path
  never shows it.
- **A registration nothing resolves at startup fails at the first message, and
  the suite that catches it reports the symptom.** `ValidateOnBuild` never
  constructs an open generic and no host resolves a scheduler while it boots, so
  the service connects, declares and reports ready. The test then times out
  saying what did not happen, never why. Assert the registration itself where
  the container can still be enumerated.
- **Drive `TestServer` for what the *application* decides and a real server for
  what the *server* decides.** `ConfigureKestrel` is a silent no-op under
  `TestServer`, and the two are indistinguishable from the test.
- **A measurement taken through a tool that normalises reports the absence of
  the defect it was taken to find.** A CRLF Helm template renders a `\r` onto
  every line, and `deploy/helm/smoke.sh` went green against it here — MSYS
  `grep` strips the CR before matching, where the Linux runner does not. The
  first run said the hazard did not exist. Reading the bytes said it did. When
  a measurement clears a platform-specific hazard, check what the measuring
  tool did to the evidence.
- **A gate cannot fail on a file that is not there.** "The gateway renders no
  migration Job" passed against a gateway declaring `image.migrator`, because
  that chart carries no migration template — so the values key the comment
  credited was never consulted. This is the gate-coverage lesson at its
  earliest point: not a gate that stopped covering a surface, but one whose
  subject never existed. Assert the **agreement** between the two halves, which
  fails from either side.
- **A path the shell created is not necessarily a path the built-in readers
  resolve.** Under MSYS the two halves of this session disagree: `mktemp -d`
  prints a POSIX path, `Read`/`Grep`/`Glob` and every subagent resolve
  host-native ones, and on this host `/tmp` is `D:\tmp\alexa` for the shell and
  `C:\tmp` for the readers — different directories, both populated. Ask a
  tool that already knows the host spelling rather than converting one:
  `git worktree list --porcelain` prints `worktree D:/tmp/alexa/secsweep-nlPuf1`
  for a root the shell called `/tmp/secsweep-nlPuf1`, is usually already
  granted, and has no flag that takes a path. It is a **labelled record**, so
  compare the `worktree `-prefixed lines and strip that prefix before anything
  uses the value — and select the line that *appeared*, never the first one,
  which is the caller's own worktree and would pass a named-file proof against
  a snapshot nobody pinned. Take `--porcelain` and not the default: the aligned
  form repads every row when a longer path joins, so a set difference over it
  reports them all as new. `cygpath -m` is the obvious answer and the
  wrong one — a prefix grant of it also buys `cygpath -f <file>`, which prints
  an arbitrary file, so the
  translation smuggles in a shell reader. Keep both spellings in named variables
  and never leave the reader-side one unset: unbound, it turns the next absolute
  path into a workspace-relative one, which is how a check passes against the
  caller. The failure is silent in the direction that matters: a
  `Glob` **pattern** under an unresolvable root returns `No files found`, which
  is exactly what a clean scope returns. Only the `path=` form errors. The
  divergence was already diagnosed twice in-tree for *subprocesses*
  (`git-worktree-drop.sh`, `grok-review.sh`'s `host_path()`) and never for the
  readers.
- **One tool's "valid" is not the next tool's, and the gap is where a value
  crosses between them.** PR-23 hit this three times in one file: `Release_1`
  is a legal OCI tag and an illegal Job name; `https://shop.example.com:443` is
  a legal URI and never matches `WithOrigins`; and `010.0.0.0/8` is legal to
  `IPNetwork.Parse`, which reads it as **octal** and silently yields
  `8.0.0.0/8`. A value validated against the alphabet of the system it comes
  from, and then handed to a system with a narrower one, fails at the far end —
  after the deploy has started. Validate against the **intersection**, and say
  in the guard which system each rejection is for.
- **A validator must check the value it emits.** A CORS origin was trimmed,
  validated, and then written out untrimmed, so a trailing space passed every
  check and failed the host's own comparison. A check on one string and a write
  of another is worse than no check, because the check reads as authoritative.
- **A list drifts exactly as a number does.** PR-23 lost count of the same
  inventory three times — the source files its chart gate reads — and each fix
  was a copy that went stale again. What ended it was declaring the list once,
  beside the code that reads it, and asserting the other copy matches. The
  prose then carries the argument for each entry rather than the entries.
- **A registered name is not a live signal, and the registration is what makes
  the absence invisible.** §13.2 registers the HybridCache meter and
  `Microsoft.Extensions.Caching.Hybrid` 10.0.0 **publishes no meter at all** —
  it reports through `HybridCacheEventSource` with `PollingCounter`, which is
  EventCounters. A reviewer asking "is the meter registered" gets a yes, the
  dashboard is empty, and empty reads as healthy. The same shape reaches
  further than metrics — a declared dependency, a mounted config key, a bound
  endpoint. **Ask what writes to it, not whether it exists.**
- **"What is it owed" is a question to answer by reading, not by inferring
  from what is absent.** The same alert was first diagnosed as owed a
  *consumer*, because nothing calls `AddRedisConnections` — true, visible from
  this repository, and not the reason. A gate was written against that
  premise, observed red and removed once the package was read: gating on a
  consumer would have gone green→red the day Redis was wired and moved a
  silent alert into the loaded file. **A plausible cause you can see beats an
  actual cause you have not looked for, which is what makes it dangerous.**
- **A list of things known to be missing needs a gate asserting they are still
  missing.** PR-24's four unloaded alerts would otherwise become four alerts
  nobody ever turned on: the gate that says "these metrics are published by
  nothing" is what turns the list into a red build on the day one of them
  lands. A TODO nothing re-checks is a decision, not a deferral.
- **A filter that names a state the system does not have excludes nothing and
  therefore matches everything.** §13.6 excludes a saga "awaiting despatch";
  the state is called `Confirmed`, so `state!="AwaitingDespatch"` would have
  paged on every healthy confirmed order. Prose describing a state and code
  selecting one are different acts — **read the enum, not the sentence.**
- **A test that asserts on which exception wins a race is a test that fails on
  a loaded runner and nowhere else.** `WebApplicationFactory` drives a
  top-level-statements `Program` through `DeferredHostBuilder`, so when
  `ValidateOnStart` throws, `app.Run()` disposes the host while the deferred
  host is still resolving from it — and the loser gets `ObjectDisposedException`
  with the real exception **destroyed rather than wrapped**. No assertion can
  recover it afterwards. Split the claim instead: the host-level test asserts
  that it refused, and a second test asserts *why*, against the options
  pipeline where nothing races. Measured on this repository: intermittent on a
  two-core CI runner, never once locally.
- **A declared-inputs list checks itself against the workflow, never against
  the reads — so an omission is invisible from inside the gate.** The Helm
  tree's `SOURCE_INPUTS` pattern was adopted twice more and then failed a third
  time in the obvious way: `canary.py` declared two paths and opened three, and
  its "both triggers cover every entry" assertion stayed green throughout,
  because a list can only be compared for the entries it already contains. **A
  gate cannot see a read it was never told about.** The fix is a test whose
  subject is the *reads* — the same shape as asserting a parser found anything
  at all — and it was owed by every copy of this pattern rather than by the one
  that was caught. **All three have it now**: `canary.py`'s in its suite,
  `check.py`'s as a check of its own, and `smoke.sh`'s over its own `$ROOT/…`
  literals. Each was observed red against a removed entry. A fourth copy of
  `SOURCE_INPUTS` arrives owing the same test.
- **A `helm upgrade --install` of a new release inherits nothing.** A second
  release of the same chart takes the chart's defaults for every value the
  environment overlay would have supplied — authority, OTLP endpoint, database
  — unless it is given them. Only one chart here failed loudly (the gateway
  refuses an empty `ingress.trustedNetworks`); the other three would have
  installed and been quietly wrong. Drive a sibling release from
  `helm get values` of the one it is a sibling of.
- **A tool that changes where it writes when you ask it for something else is
  a premise you did not know you had.** `domain_coverage.py` asserted exactly
  one Cobertura file per run, correctly, until `--logger trx` was added for an
  unrelated gate — and the TRX logger makes the collector leave one partial
  attachment per test project beside the merged one. Nothing about the flag
  says so. **When a step's output feeds another step, adding a flag to the
  first is a change to the second.**
- **Floating point is wrong at the input a ladder starts from, not at the
  exotic ones.** The canary's weight arithmetic read
  `ceil(stable * f / (1 - f))`, and at 5% against 19 replicas `19 * 0.05 / 0.95`
  is `1.0000000000000002` — so it bought two pods and served 9.5% under a label
  reading 5%. Every quantity was a count of pods or a whole percentage, so the
  exact answer was available the whole time. **Where the inputs are integers,
  the float route is not merely imprecise, it is available to be wrong.**
- **Two functions deriving one number will disagree, and the test that pairs
  them is cheaper than the one that finds out later.** `required_stable` named
  the replica count the step needs and `plan` refused it — not because of the
  arithmetic above, but because the two had been written to different rules,
  one taking the smallest canary at or above the weight and the other the
  largest at or below. The bug was the rule, and only asserting the round trip
  said so.
- **A pattern that is one token too strict silently covers less than it
  claims.** The observability gate's instrument reader required `Create…<T>(`
  and found every histogram and counter while missing all three observable
  gauges, because `CreateObservableGauge` infers its type argument. It reported
  four correct alerts as having no signal — loudly, this time. The quiet
  version of the same bug is what the gate-coverage lesson at the top of this
  list is about.
- **A hand-written double is a second specification, and only the real provider
  can falsify it.** `StubCatalog` had drifted from Catalog in four places and
  the suite it serves stayed green throughout, because a double cannot disagree
  with itself. The consequence is sharper than a stale stub: a guard written
  *for* the provider's real behaviour becomes untestable, since the double never
  produces the input the guard exists for. Measured here — the BFF's
  case-insensitive currency comparison could be tightened to `Ordinal` with all
  62 of that suite's container-free tests still passing — the fast half of the
  66 it ran before this PR, not the 77 it runs now — over a change that
  answers 500 to every lower-case currency in production. **Ask what would
  falsify the double, not whether its suite is green.**
- **A tool a plan names may not reach the case the plan made it conditional
  on.** Appendix C made Pact conditional on a consumer relationship becoming
  contentious; the relationship that did is gRPC, and PactNet ships HTTP and
  message pacts only — gRPC is a plugin whose .NET binding has been an open pull
  request since September 2025. The plan was written against the tool's
  reputation rather than its surface. **Check the binding, not the ecosystem**:
  a capability present in a project's Rust core, its JVM binding and its
  marketing is not thereby present in the one language this repository compiles.

### The commands

These are the ones the target solution uses:

```bash
dotnet tool restore                # dotnet-ef, pinned in .config/
dotnet restore Platform.slnx
dotnet build Platform.slnx
dotnet test  Platform.slnx         # needs a running Docker daemon
dotnet test  Platform.slnx --filter "Category!=Integration"   # 635 of 812, no daemon
```

`docs/testing.md` is the operational reference — the filters, what needs
Docker, the coverage run. This block is the short form.

**Eight suites, three runners, and only one of them is `dotnet test`.** The
scaffold's tests are Python, the chart gate is bash over `helm template`, and
the licence gate, the observability gate, the pipeline gate, the coverage
reporter's suite and the canary's are Python again; none is in `Platform.slnx`,
so a green solution says nothing about any of them. **The licence gate belongs
in that count** — CI tests it and then runs it, which is the pattern every gate
here follows — and leaving it out is what made this seven:

```bash
(cd tools/new-service && py -3.12 -m unittest)  # 81 tests, no Docker, no SDK
python tools/new-service/new_service.py <Name> --port <51xx>

bash deploy/helm/smoke.sh                       # needs helm 3, no Docker, no SDK
HELM=/path/to/helm bash deploy/helm/smoke.sh    # when it is not on PATH

py -3.12 deploy/observability/check.py          # no helm, no Docker, no SDK

(cd .github/licence-gate && py -3.12 -m unittest)  # then licence_gate.py

py -3.12 -m unittest discover -s .github/pipeline-gate
py -3.12 .github/pipeline-gate/pipeline_gate.py filters
py -3.12 .github/pipeline-gate/pipeline_gate.py images
py -3.12 -m unittest discover -s .github/coverage
py -3.12 -m unittest discover -s deploy/canary
py -3.12 deploy/canary/canary.py check
```

**`pipeline_gate.py stages` is the one that cannot be run on its own**: it
reads what the three test steps wrote, so it needs a `dotnet test` per stage
into `./TestResults/{architecture,unit,integration}` first. `docs/testing.md`
carries those three commands.

`deploy/canary/README.md` is that tree's operational reference, on
`deploy/observability/README.md`'s terms: what the gate asserts, and — more
usefully — the things it does not, of which the load-bearing one is that
**nothing has established a replica ratio is a traffic ratio**. kube-proxy
spreads connections rather than requests, and no render-time check reaches
that.

The chart gate needs `helm dependency update` before it can render anything,
and runs it itself — `file://` dependencies resolve from disk, so there is no
network step and no chart repository. `deploy/helm/README.md` is its
operational reference.

The observability gate needs nothing at all — it reads text, like the licence
gate, which is why it can run before a restore. `deploy/observability/README.md`
lists what it asserts and, more usefully, the two things it does not: it
reaches no Prometheus and does not validate rule syntax.

**`py -3.12`, not `python`, and the block above is written that way on
purpose.** Every CI job that runs Python pins 3.12 — **every one, without
counting them here**, which is `docs/testing.md`'s form and is the fix for a
number that had already gone stale twice: PR-22 made it three and PR-24's
observability gate made it four. The predicate is checkable and the count was
not. The default interpreter here is 3.14.
A newer one is the hazard, not an older one — it accepts APIs 3.12 does not, so
the local suite goes green on code the runner cannot execute.
`Path.read_text(newline=…)` is 3.13 and cost a CI round exactly that way. The
scaffold *script* is a different matter: running it is not a test of the floor,
so plain `python` is fine there. 3.12 is installed here, so **every** Python
suite can be run against it — the set is the one *The commands* lists, plus
`.github/licence-gate`, and it is not enumerated a second time here for the
reason that sentence gives.

**`dotnet test` requires Docker from PR-08**, and the container tests are still
never *skipped* when it is absent: a skip on a missing daemon **fails open**, so
CI would go green on a runner whose Docker broke. ADR-010 already made real
infrastructure non-optional. Without a daemon they fail on `Failed to connect
to Docker endpoint`, which is a true statement about the machine and not a
defect in the branch.

**Since PR-22 they are *categorised*, which is the opposite of a skip and used
to be refused alongside it.** A skip runs the suite and reports a pass; a
category runs a smaller suite and says which. `Category!=Integration` is 635 of
the 812 and starts no container — measured with `docker events`, not inferred —
and `Category=Integration` is the other 177, needing the daemon exactly as
before.

**Since PR-25 CI runs three stages rather than one pass**: architecture gates
(18), unit (617) and integration (177), which is the 635 above split at the
seam §15.1 draws. Separate *steps* in one job, not separate jobs — a job
boundary would mean shipping the build output between runners to keep
`--no-build` honest, and the coverage figure is the union of the last two.
**Three stages are three new ways to select nothing**, since `dotnet test`
exits zero on a filter that matches no test, which is what
`.github/pipeline-gate/` exists for.

**The trait is declared on the `[CollectionDefinition]`, not per test class**,
so joining the container collection *is* carrying the category — there is
nothing to forget and therefore no reflection gate guarding it. xUnit v3's
propagation was measured before the design was trusted.

**Five projects need Docker**: `Catalog.Api.Tests`, `Catalog.Application.Tests`,
`Common.Infrastructure.Tests`, `Ordering.Api.Tests` and `Web.Bff.Tests` — each
with its own collection and therefore its own container set (§12.4's stated
price). The last is the odd one: most of its tests need no container, one class
needs a Keycloak, so the suite is fast and then pays for an identity provider
once — 73 tests of 77 on the fast side, which is the clearest case in the repo
for categorising a collection rather than a project. (Measured from the stage
TRX, not counted by eye: `docs/testing.md` had it right and this line was three
short on both halves of the same split.)
`Ordering.Application.Tests` is deliberately not among them — its handler
tests moved to `Ordering.Api.Tests`, because `ICurrentUser` is
`HttpContextCurrentUser` and a handler resolved in a bare scope has no
principal to bind a subject from. Since PR-21 it holds §12.5's saga suite
instead, which is the same property from the other side: a state machine driven
over the in-memory harness needs no infrastructure at all, and homing it in the
API suite would have bought it a container set for nothing.

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

**The blueprint must not contradict itself.** It is ~14,900 lines that describe
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
- **This file and `docs/pr-decision-log.md` are inside the rule too.** Neither
  is reached by `/validate-blueprint` or `/check-links`, so nothing structural
  will catch their drift.

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
  `## ADR-001 — <title>`; `docs/pr-decision-log.md` follows the ADR form.
- **Cross-references use the section sign**: `§9.3`, and link the first mention
  in a passage — `[§9.3](09-messaging.md)`. Within a chapter, `(§6.5)` bare is
  fine. Cite the section that actually states the claim; a reference to a
  section that only mentions the topic is a defect.
- **Callouts are blockquotes whose opening sentence is bold**, no emoji, no
  admonition syntax. Two forms are named and recurring — `**Trap — …**` (16)
  for a mistake worth naming, and `**Decision — …**` (8), which always points
  at the ADR that records it:

  ```markdown
  > **Trap — projecting everything by default.** Each projection is a second
  > copy of the truth, with its own bugs and its own rebuild procedure.

  > **Decision — no mediator library.** See [ADR-004](appendix-a-adrs.md#adr-004--no-mediator-library).
  ```

  The rest are a bold assertion followed by its argument —
  `> **Unregistered, this fails silently and completely.** …`. That is the
  default; reach for `Trap` or `Decision` only when the callout genuinely is
  one. `**Decision.** / **Why.** / **Consequences.**` are the ADR body form,
  not callouts.

  **The total used to be written here and no longer is, which is the third
  fix and the only one that holds.** It said 120 for eight PRs, then 161, then
  186; PR-24 recounted it to 189 mid-branch and its own later commits made
  that 194 before the pull request merged. **A number that goes stale twice
  inside one PR is not a number that recounting fixes.**

  What holds is the two named counts, and the branch that added a sixteenth
  `Trap` is the first test of that. This paragraph used to say *what never
  drifted is 15 and 8*; `Trap` has now moved once, and the lesson survives the
  movement rather than being refuted by it — those are the figures a reader
  checks, so the change was caught and reconciled inside the PR that caused it,
  where the residual nobody looks at is the one that rots unnoticed. Keep them
  current when a callout is added; do not bring the total back —
  `CLAUDE.md` makes the same argument about its own line count at the top of
  this file, and `deploy/observability/README.md` about its rule counts.

  If you do need the figure, it is `grep -h '^> \*\*' *.md | wc -l` over the
  twenty blueprint files, minus the three definitional entries below — and
  **not** `grep -c`, which over many files prints a count *per file* and has
  to be summed, which is one more place to be wrong.

  **Three callouts spell the dash outside the bold and are not counted above**:
  §1.3's two glossary entries, which *define* `Trap` and `Decision` rather than
  being instances of them, and one `**Decision** —` in §14. Counting a
  definition as an instance is what made an earlier revision of this sentence
  say 16 and 10 — an arithmetic that added to 120 only by including two entries
  that fail the predicates beside them, since a glossary line is not "a mistake
  worth naming" and does not "point at the ADR that records it".
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

**One dialect, both phases.** The rules below govern the fenced samples and the
compiled solution alike, so a sample and its implementation read identically.
The repo `.editorconfig` is not a documentation convenience — it is the file
PR-01 ships. Change it deliberately, and reconcile any change with the samples
already written against it.

Each rule below says whether the build enforces it. **The ones marked
review-carried are why this section exists**: no analyser reaches them, so this
file and a reviewer are the only things that do.

### Layout and naming

- Four-space indent, spaces not tabs. CRLF line endings. Newline at end of file.
- Pascal case for types, properties, methods and events; `I` prefix on
  interfaces; namespace matches folder.
- **`using` directives outside the namespace**, no blank lines between
  consecutive usings (IDE0065, enforced). This binds source only — a sample is
  an excerpt rather than a compilable unit, so **do not "complete" one by
  adding the block it would need to build.** The blueprint carries exactly two
  `using` lines, both `using static` (§9.6's saga imports `Endpoints`, §12.4's
  subject suite imports `Principals`), because an unqualified
  `Authenticated(caller)` reads as a member of the type being shown unless
  something says otherwise. A third is fine on those terms; a plain `using` is
  not.
- **No unused `using` directives** — *review-carried*. A file that stops needing
  one drops it in the change that stopped needing it: a stale using is a claim
  that the file depends on something it does not, and the reader who trusts it
  looks in the wrong assembly first. **Nothing catches this.** IDE0005 is not
  reported by the build at all unless `GenerateDocumentationFile` is on, so
  `dotnet build` is silent even with `TreatWarningsAsErrors`, and so is
  `dotnet format style --diagnostics IDE0005` — both checked against a
  deliberately injected using. Turning it on costs a fourth entry in
  `Directory.Build.props`, because it also enables CS1591 against **1,178**
  public members with no XML comment, across `src/` and the test projects
  alike. That figure said 62 until this branch measured it — `dotnet build
  Platform.slnx -p:GenerateDocumentationFile=true -p:TreatWarningsAsErrors=false
  --no-incremental`, counting unique diagnostic sites, run twice for the same
  answer. It has grown roughly nineteenfold since it was written, which makes
  the fourth-entry argument stronger rather than weaker. **`--no-incremental`
  is load-bearing in that command**: an ordinary build recompiles nothing and
  reports zero, which reads exactly like a clean result. The greyed-out using
  in the IDE is the only live signal there is.
- **A blank line always follows the namespace declaration** (IDE0055,
  enforced). `namespace X;` is a statement about the whole file, not the first
  line of the type below it:

  ```csharp
  namespace Common.Application;

  public interface IDispatcher
  ```

### Statements and types

- **A single statement may omit braces; two or more always take them — and so
  does one that wraps.** The statement goes on the following line, never beside
  the condition, and if it does not finish on that line the body takes braces:

  ```csharp
  // Wrong — the body wraps, so the reader has to count indentation to find
  // where the condition's reach ends.
  if (!Known.Contains(message.Reason))
      throw new ContractMappingException(
          $"Unknown review reason '{message.Reason}'.");

  // Right.
  if (!Known.Contains(message.Reason))
  {
      throw new ContractMappingException(
          $"Unknown review reason '{message.Reason}'.");
  }
  ```

  **This rule tightened in PR-21 and cost a 49-site sweep**, because the
  sentence used to end "and it may wrap". It is *review-carried*: no analyser
  reaches it — IDE0055 governs the indentation of whatever shape is there and
  has no opinion on which shape it is — so the corpus is the only enforcement,
  and a new braceless wrapped body is a review comment rather than a failed
  build.

  A single statement that fits on its line still omits them:

  ```csharp
  if (amount < 0)
      throw new DomainException("Money cannot be negative.");

  foreach (IDomainEvent domainEvent in events.Where(projections.HasHandler))
      await publisher.StageAsync(domainEvent, OutboxLane.Local, ct);
  ```

  This holds across all 49 braceless bodies in the blueprint, counted over
  ` ```csharp ` fences after PR-21 braced the 18 that wrapped. **The figure it
  replaces was 53 and does not reconcile**: the same count run before that sweep
  gives 67, so 53 was neither the old value nor the new one under this
  definition. Which definition produced it is not recoverable, and inventing one
  would be worse than saying so — the number below is the one a rerun of
  `count-braceless` reproduces.
  `csharp_preserve_single_line_statements = false` keeps a format run from
  pulling any of them back up onto the condition's line. The one exception is a
  **wrapped** condition, which takes braces — see below.
- **Explicit types for locals**, except where the right-hand side names the
  type. A reader of a fenced code block has no hover and no go-to-definition,
  and this blueprint's job is to teach types and contracts. Code blocks may run
  past 80 columns, so length alone is never a reason to hide a type — if a
  declaration turns unwieldy, split the expression.

  ```csharp
  IReadOnlyDictionary<ProductId, Money> priceList =
      await prices.GetAsync(productIds, command.Currency, ct);
  ProductId[] missing = [.. productIds.Where(id => !priceList.ContainsKey(id))];
  ```

  Four cases keep `var`, and only these four:

  | | Example |
  |---|---|
  | The RHS names the type | `var order = Order.Place(…)`, `var id = Guid.CreateVersion7()` |
  | Anonymous types | `var args = new { OrderId = orderId.Value };` |
  | Tuple deconstruction | `foreach (var (product, qty, price) in items)` |
  | Fluent resource DSLs | The whole Aspire AppHost block in §14.2 — eleven of its thirteen locals are an `IResourceBuilder<T>` whose name only repeats what the `Add*` call already said. Explicit types are possible there and read worse; keep the block uniform |

- **Target-typed `new()` only where the type is already named beside it** —
  half enforced, half *review-carried*. This is the `var` rule applied to the
  right-hand side. Where the declaration names the type, `new()` repeats
  nothing and `.editorconfig` asks for it:

  ```csharp
  ServiceCollection services = new();
  private static readonly ConcurrentDictionary<Type, Invoker> Cache = new();
  public static Result Success() => new(null);
  ```

  Where nothing beside it names the type, spell the type out. The positions
  that hide it are **an argument**, **a collection expression in argument
  position**, and **the target of an indexer or property assignment**:

  ```csharp
  // Wrong — the reader cannot see what is being constructed in any of these.
  .MapHealthChecks("/health/live", new() { Predicate = _ => false })
  .AddAttributes([new("deployment.environment", builder.Environment.EnvironmentName)])
  scrubbed[i] = new(attribute.Key, "[redacted]");

  // Right.
  .MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
  .AddAttributes([new KeyValuePair<string, object>("deployment.environment", builder.Environment.EnvironmentName)])
  scrubbed[i] = new KeyValuePair<string, object?>(attribute.Key, "[redacted]");
  ```

  A collection expression assigned to a typed declaration is **not** one of
  them — the element type is on the left, so the elements stay bare:

  ```csharp
  KeyValuePair<string, object?>[] state =
  [
      new("NewPassword", "a"),      // fine — the array's type is on the left
      new("card_number", "b")
  ];
  ```

  **No analyser reaches the banned half**: IDE0090 fires only when the type *is*
  apparent, so it polices the first block and has nothing to say about the
  second. **Where naming the type breaks the 120-column budget, name it in a
  local — do not fall back to `new()`.** A local declaration carries the type,
  so the `new` beside it needs none and both lines fit.
- Prefer collection expressions, `is null` over `ReferenceEquals`, null
  propagation, compound assignment, simplified interpolation, primary
  constructors.
- **Materialise with a spread, not a terminal `.ToArray()` or `.ToList()`** —
  *review-carried*. The spread states the target and the terminal call states a
  conversion, and only one of those is what the line is for:

  ```csharp
  ProductId[] missing = [.. productIds.Where(id => !priceList.ContainsKey(id))];
  ```

  Two consequences: dropping `.ToArray()` often leaves a **single** call, and a
  single call is not a broken chain — join it. And a spread frequently brings
  the statement back under 120, in which case the one-line rule applies and the
  `[` does not get its own line.

  **A `ToArray` that is not a sequence materialisation is outside this rule.**
  `MemoryStream.ToArray()` is a stream accessor — the type implements no
  `IEnumerable`, so `[.. buffer]` does not compile (CS9212). The rule is about
  the *terminal LINQ operator*, where the spread and the call are two spellings
  of one thing. This was narrowed after a reviewer read "there are no
  `.ToArray()` calls left in the corpus" as the grep it literally is — **a rule
  whose stated test is a string match will be enforced as one.**

### Wrapping

- **A list is on one line, or one element per line. Never a ragged middle.**
  "List" means anything comma-separated inside brackets — parameters,
  arguments, collection expressions, initialisers, tuple members. The budget is
  **120 columns** (`max_line_length`); within it the list stays on one line,
  past it every element gets its own.
- **`[` and `{` each take a line of their own**, at the column of the construct
  they open, and their closers do too. **`(` is the single exception**: it ends
  the line it opens, and `)` trails the last element — `);`, not a line of its
  own:

  ```csharp
  string[] forbidden =
  [
      "Microsoft.EntityFrameworkCore",
      "MassTransit",
      "StackExchange.Redis",
      "Microsoft.AspNetCore"
  ];

  _deliveryLag = meter.CreateHistogram<double>(
      "messaging.delivery.lag",
      unit: "s",
      description: "OccurredAt to consumer start.");
  ```

  The exception is not arbitrary. A parenthesised argument list is *part of* the
  invocation, so it hugs the call at both ends; a braced or bracketed body is a
  *container* of elements, and giving it its own opening and closing line puts
  its extent in a column the eye can scan.

  **The two halves are enforced very differently.** `{` is not a review rule at
  all: the C# default for `csharp_new_line_before_open_brace` is `all`, IDE0055
  reports a trailing one, and ADR-019 turns that into a failed build. `[` has no
  such backing — Roslyn has no opinion on bracket placement and `dotnet format`
  neither introduces nor removes the break — so that half is *review-carried*.
- **A trailing lambda is an argument like any other**, and does not hang off the
  opening line. An argument list holding a lambda has exactly two legal shapes,
  tried in this order:

  ```csharp
  // 1. One line, if it fits inside 120. Always preferred.
  Publish(payload, type, c => { … }, ct);

  // 2. Otherwise one argument per line — the lambda included, braces and all.
  Publish(
      payload,
      type,
      c => { … },
      ct);

  // And if the lambda itself will not fit on its line, its braces open under
  // the rule that governs braces, at the argument's own column.
  Publish(
      payload,
      type,
      c =>
      {
          c.MessageId = message.MessageId;
          c.CorrelationId = message.CorrelationId;
      },
      ct);
  ```

  **There is no carve-out, and a braced body does not earn one.** One was tried
  — leading arguments stayed up whenever the trailing lambda had a braced body —
  and removed, because it made the rule undecidable from the call site: whether
  a leading argument could stay depended on the *last* argument's body kind and
  on whether anything followed it, and a reviewer performing only the first
  lookahead got `Publish(payload, type, c => { … }, ct)` wrong. The cost is
  real and accepted: every builder DSL in the blueprint now breaks across four
  or five lines. It buys a rule with no lookahead.

  A **single**-argument call is untouched — `AddRateLimiter(options => { … })`
  keeps its shape. Nor is a lambda the only thing that can hang:
  `WriteAsJsonAsync(new ProblemDetails { … }, ct)` is the same shape with an
  object initialiser in the lambda's place.

  **Two greps narrow this down and neither closes it.** The arrow —
  `\(.+,\s*\w+\s*=>\s*$` — catches a lambda hanging off a call with a leading
  argument. The closer — `^\s*[]})],\s*\S` — catches a bracket closing at the
  head of a line with an element still after it. **Neither sees the plain
  one**: a continuation line carrying two ordinary arguments has no arrow and
  no leading bracket. Treat them as a sieve, not a proof.

  **Write the closer for the tool you are running it in.** Ripgrep reads `\]`
  as an escaped bracket and builds the class `}` `]` `)`; POSIX `grep` treats a
  backslash inside a bracket expression as literal, so the class closes at the
  first `]` and the pattern matches nothing, ever — reporting zero and exiting
  1, which reads exactly like a clean sweep. Put the `]` first — `[]})]` — and
  it means the same thing in both.
- Continuations indent **four**, never to a bracket column. A list wrapped under
  its opening bracket is a leftover from an older style. Two things keep this
  from being mechanical: a line comment on an element forces the broken form
  regardless of width, and the four `var` cases still apply inside the elements.
- **A broken fluent chain puts every call on its own line**, at head + 4, never
  aligned under the receiver's dot — including the *first* call. If the chain
  breaks at all, nothing stays on the head's line:

  ```csharp
  builder.Services
      .AddReverseProxy()
      .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
  ```

  Unlike a list, a chain is **never joined back up to fit** — it is broken for
  reading order, not for width. One exception: a short qualifier and its
  subject count as one call (`.That().ResideInNamespaceContaining(…)` —
  NetArchTest's idiom reads as pairs).

  **The head is the line the chain starts on**, which is not always the
  statement's first line. When a declaration's initialiser wraps, that wrapped
  line is the head and the chain indents four past *it* — eight from the
  declaration. A spread element is a head the same way, so `.. x` takes the
  chain at + 4. A `})` closing a lambda mid-chain is a continuation, not a head.

  **"Contains no invocation" means no *dotted* call, and a receiver never
  outranks that.** `Types`, `app`, `_lines` and `Enumerable` all sit alone as
  heads, static classes and fields alike. What stays on the head is whatever has
  nothing to strand in front of it: object creation (`new MsSqlBuilder()`), a
  call with no receiver (`When(OrderPlaced)`), and a parenthesised expression
  (`(await Task.WhenAll(…))`).
- **A lambda body that is itself a call goes on its own line**, at + 4, rather
  than trailing after the `=>`. A bare parameter re-mention is not a call and
  stays — `p => p` heading its own chain is the fluent-DSL idiom:

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
  statement. This applies only when the construct is already broken across
  lines; inside 120 columns it stays on one.

  An **expression-bodied member** is not a lambda for this purpose:
  `public Money Total => _lines.Aggregate(…)` keeps its body on the `=>` line,
  because there is no nesting for the break to clarify. When such a member's
  body *does* need its own line, `=>` still trails the signature, and the body
  sits at the **declaration's** indent + 4 — not necessarily the `=>` line's.
- **Break at the outermost bracket, never a nested one.** Reaching past the
  outer parenthesis leaves the outer call glued to its argument:

  ```csharp
  // Wrong — the break is inside OrderPlacedDomainEvent, and Raise( is stranded.
  order.Raise(new OrderPlacedDomainEvent(
      order.Id, customerId, order.Total, order.SnapshotLines(), now));

  // Right.
  order.Raise(
      new OrderPlacedDomainEvent(order.Id, customerId, order.Total, order.SnapshotLines(), now));
  ```

  The argument moves to the next line **whole**; break it further only if it
  still does not fit. This composes with the lambda rule rather than competing:
  a call whose last argument is a lambda breaks at its **own** parenthesis,
  which puts the lambda on a line of its own, and never after the `=>`.
- **A multi-line condition takes trailing operators, four-space continuations,
  and braces on its body.** The braces are what make four safe: without them
  the last `&&` line and the body sit in the same column.

  ```csharp
  if (!command.IsSystemInitiated &&
      (!currentUser.IsAuthenticated ||
          (order.CustomerId.Value != currentUser.Id &&
              !currentUser.HasPermission("orders:admin"))))
  {
      return Result.Failure(OrderErrors.NotFound);
  }
  ```

  This is the **one exception to the braces rule above**. Prefer not to wrap at
  all; joining is the better fix and has been applied everywhere it fits. The
  block above is the corpus: one wrapped header, in §11.4, kept because an
  ownership check that fails closed does not join inside 120. A second one
  appearing is a signal to join, not a precedent. **A parenthesised group that
  breaks indents a further four**, so nesting depth is visible. The check above
  gained its second level when the guard was rewritten to fail closed; the
  earlier form led with `currentUser.IsAuthenticated &&`, which read as a guard
  and behaved as an exemption, admitting every caller arriving with no
  principal at all.
- **Operators go at the end of the line they continue from**
  (`dotnet_style_operator_placement_when_wrapping = end_of_line`). Each line
  then ends by announcing that more is coming. This holds for `&&`, `||`, `??`
  and `+`, in conditions and expressions alike; a leading `&&` or `??` is a
  leftover. It governs wrapped lambda predicates as much as `if` headers.
- A base-type list is the one continuation covered by none of the above: no
  bracket to hang off, no operator, already one entry per line. Leave it
  aligned under the `:`.

### Whitespace, suppressions and secrets

- **One space before `=`, `=>` and `{` — never a column of them.** Padding a
  token out to line up with the one above fails the build: IDE0055 reports it
  and ADR-019 makes that an error.

  ```csharp
  // Fails the build. Every line but the longest carries the diagnostic.
  public required Guid MessageId       { get; init; }
  public required decimal TotalAmount  { get; init; }

  // Correct.
  public required Guid MessageId { get; init; }
  public required decimal TotalAmount { get; init; }
  ```

  **A trailing `//` comment is the carve-out, and it is a real one.** IDE0055
  does not govern whitespace in front of a comment, so a comment may sit in
  whatever column the block reads best in. The carve-out stops at the end of
  the line: a trailing comment too long for one line cannot wrap into a second
  `//` line aligned under the first, because a line *starting* with `//` is a
  whole-line comment whose leading whitespace is indentation, and indentation
  IDE0055 does govern. Shorten it, or move it above the statement whole.

  `dotnet format` agrees with both halves — it collapses the code padding and
  leaves the comment column untouched — so a format run neither introduces this
  nor undoes it.

  **Two places the analyser does not reach, and they are opposites.** Padding
  between a type and its identifier —
  `private readonly Counter<long>   _placed;` —
  is *not* reported, and is swept anyway — *review-carried*, because one
  dialect beats a rule that stops halfway. **SQL is the other way round and
  keeps its alignment**: it lives inside raw string literals, which no analyser
  or formatter reads, and the SQL section argues its columns on their own terms.
- **No `#pragma` suppressions** — there are none in the corpus, and a sample
  that needs one is a sample whose design is wrong. A genuinely warranted
  suppression belongs in `Directory.Build.props` with a comment, never inline.
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

### Settled choices — do not "fix" them

| | |
|---|---|
| Namespaces | File-scoped (`namespace X;`), never block-scoped |
| Extension declarations | C# 14 `extension(T receiver)` blocks where a class groups several extensions on one receiver — `Common.Application.DependencyInjection` is the worked example. **The corpus is currently split**: `Common.Web`'s **eight** extension classes still use the classic `this`-parameter form. **Five** extend a receiver nothing else does — `CorrelationIdExtensions` (`IApplicationBuilder`), `ProblemDetailsExtensions` (`IServiceCollection`), `HealthCheckExtensions` (`IEndpointRouteBuilder`), `AuthorizationPolicyExtensions` (`AuthorizationPolicyBuilder`) and `ResultExtensions` (`Result`) — while **three** share `IHostApplicationBuilder`: `ObservabilityExtensions`, `AuthenticationExtensions` and the `CommonWebDefaultsExtensions` that composes them. Whether to group those three is open and deliberately unsettled — they are separate files because one composes the other two, and merging would put a caller-facing entry point in the same block as the pieces it calls. The receivers are listed rather than counted because a bare count is what went stale here: this cell said six and four from before PR-16 added `AuthenticationExtensions`. Converting anything is a decision about the whole corpus |
| Expression-bodied members | Used for one-line members, not for constructors |
| Braces | Optional for a single statement, required for two or more |
| Target framework | .NET 10 (LTS), C# 14 |

Each of these is a house style a reviewer might otherwise read as an oversight
and "correct". They are consistent across all ~150 existing C# blocks, and the
solution is written the same way. Changing one is a decision about the whole
corpus, not about the file in front of you.

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

The `=` signs line up in a column here, and that alignment is deliberate. It is
**not** justified by parity with the C# initialisers — IDE0055 forbids the C#
form and the section above says so. SQL keeps the column on its own merits: a
statement inside a raw string literal is invisible to every analyser and
formatter in the toolchain, so nothing will fight it, and one assignment per
line with the names in a column is what makes `SET` read as the row shape it
produces rather than as a wrapped list.

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
  (currently ADR-024) and keep the
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
- **Follow the delivery plan's order.** Appendix C sequences 27 PRs with
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
| `/validate-blueprint` | Multi-pass self-consistency audit across the blueprint and `docs/roadmap.md`; also code ↔ docs drift once `src/` exists |
| `/check-links` | Link, cross-reference and nav-footer integrity |
| `/new-chapter` | Scaffold a chapter and rewire its neighbours |
| `/new-adr` | Append an ADR in the established form |
| `/style-pass` | Apply one corrected code form corpus-wide, then record it in `CLAUDE.md` and `.editorconfig` |

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

**File permission rules take `Edit(...)`, never `Write(...)`.** `Edit(path)`
covers every file-editing tool, `Write` included; a `Write(path)` rule matches
nothing and Claude Code **refuses to start** until it is removed:

```
Permission deny rule (.claude\settings.json): Write(.remember/**) is not matched
by file permission checks — only Edit(path) rules are.
```

So `Edit(.remember/**)` and `Edit(./.remember/**)` are the whole of the
`.remember/` protection, and the absence of a `Write` twin is correct rather
than a gap. This has been "fixed" twice by adding the twin back, and both times
it broke startup. **A reviewer who has not run the harness cannot see this;
check a permission claim against the harness before acting on it.**

**`Edit(.claude/scripts/**)` and `Edit(.claude/sandbox/**)` bind the agent's own
tooling.** The review loops grant those helpers by name, so a session that
could rewrite one before invoking it would make every fixed endpoint a fiction.
The sandbox `Dockerfile` is on the list for the same reason at one remove: it
is a *build input to the security boundary*, so a session able to edit it could
add an entrypoint reading the credentials the following `docker run` mounts in.
Changing either is a human's edit, made with the deny lifted. Like the push
denies it is defence in depth — `Bash` redirection can still write a file —
but it removes the quiet path.

**The external review runs in a container over a disposable clone — not a
worktree — and its one residual is egress.** The boundary is
`.claude/sandbox/Dockerfile`; a worktree could not be the thing mounted,
because a worktree's `.git` is a file pointing back into this checkout, which
is the one path the container must not reach. So the credential half is closed:
no `gh` token, no SSH keys, no host filesystem beyond the clone, non-root
inside, and `bypassPermissions` is no longer the risk it was because the blast
radius is the box. **Egress is not restricted** — the container reaches the
network, and confining it to `api.x.ai` needs an allow-list proxy Docker cannot
supply alone. Stated here as well as in the script because `/ship` and both
sweeps cite this file as where the boundary and its residual are recorded. The
reviewer also has **no .NET SDK**, so `dotnet test` is this host's gate and
never the review's.

**A grant is not a whitelist, and this is the trap under every row below.**
`allowed-tools` is an **auto-approval list**: the harness documents that it
"does not restrict which tools are available: every tool remains callable, and
your permission settings still govern tools that are not listed". So *omitting*
a tool from a command's frontmatter withholds nothing — it only decides
whether the call prompts. Refusing a capability takes a **deny**:
`permissions.deny` in `.claude/settings.json` for the repository, or the
`disallowed-tools` frontmatter key for one command, which removes the named
tools from the pool while it runs. Precedence is **deny → ask → allow**,
first
match wins, so a deny beats every allow including a frontmatter one.
Measured, not read: a `general-purpose` subagent spawned fine under
`--allowedTools "Agent(Explore)"`, and was refused under
`--disallowedTools "Agent(general-purpose)"` with `Agent type 'general-purpose'
has been denied by permission rule 'Agent(general-purpose)' from cliArg` — and
again, from a command's own frontmatter, `from command`.

**A helper is the answer whenever a git grant is wider than the operation it
buys**, because **an allow rule is a prefix and cannot exclude a flag**. Each
of these was confirmed by running the offending form rather than reasoning
about it:

| Raw grant | What it also bought |
|---|---|
| `Bash(git switch:*)` | `--discard-changes` and `-C` — and the flags **combine**, so `git switch -fC <name> <start>` defeats any `Bash(git switch -C:*)` deny |
| `Bash(git worktree add:*)` | `-B`, which resets an existing branch rather than creating one |
| `Bash(git checkout -b:*)` | the trailing flag — `git checkout -b <name> -f origin/main` discards tracked modifications |
| `Bash(git branch:*)` | `git branch -fd <name>` — force and delete behind a spelling the `-d`/`-D`/`--delete` denies do not match |
| `Bash(git reset HEAD:*)` | `git reset HEAD --hard` — the `--hard` deny matches the other word order |
| `Bash(git log:*)`, `Bash(git diff:*)`, `Bash(git show:*)` | `--output=<path>`, which is an arbitrary file write with `--format=` choosing the bytes. Reproduced: `git log -1 --format=%s --output=<scratch>` wrote the commit subject, silently |
| `Bash(git fetch:*)`, `Bash(git pull --ff-only:*)` | a URL in the repository position, and `ext::<cmd>` is a git transport that **runs its argument as a command** |

**The reset grant does not narrow, and the attempt is the sharpest lesson
here.** It was "fixed" to `Bash(git reset HEAD --:*)` on the reasoning that
`--` turns a later flag into a pathspec. True of *git*, irrelevant to the
*rule*: an allow rule is a prefix match, and `git reset HEAD --hard` starts
with `git reset HEAD --`, so the narrowed grant admitted the exact command it
excluded — while the commit message said the hole was closed. **The git
behaviour was verified and the matching was not.** Anything whose safety
depends on what follows a token needs a helper, or a **deny**, not a cleverer
allow.

**A deny is the thing an allow cannot be: `*` matches at any position in it,
including the middle.** `Bash(git *--output*)` refuses `git log`, `git diff`
and `git show` carrying `--output` anywhere in the argument list, and leaves
plain `git log` alone — both halves measured, because a deny that blocked the
command outright would read the same from the failing side. That is what
closes the three read grants above without removing them, and removing them
would have bought nothing anyway: the harness treats read-only forms of `git`
as promptless built-ins whatever the allow list says, and its own note is that
"to require a prompt for one of these commands, add an `ask` or `deny` rule".

**The `::` in a value collides with the `:*` suffix syntax, and the collision
fails silent in one direction and loud in the other.** `Bash(git *ext::*)`
passes settings validation and then matches nothing, because the trailing `:*`
is consumed as the prefix-wildcard form and the literal becomes `git *ext:` —
a probe of `git log -1 ext::foo` ran clean under it. Writing `Bash(git
*ext::**)` to dodge that is rejected at startup: *"The `:*` pattern must be at
the end."* So **`ext::` cannot be expressed in a Bash rule at all**, and the
transport is closed on the allow side instead — **and the two grants do it by
two different mechanisms, which an earlier revision of this paragraph ran
together.**

`Bash(git fetch origin:*)` **pins the remote**: a literal remote name occupies
the repository position, so a URL never reaches it. That is the control the
sentence describes, and it is real.

`Bash(git pull --ff-only)` pins **nothing** — no remote name appears in it at
all. What it does is drop the `:*`, so the grant is the documented
no-argument invocation and nothing else. Whether that closes
`git pull --ff-only ext::<cmd>` turns on the prefix-match rule stated in the
reset-grant paragraph above — **an allow rule is a prefix match** — which has
never been measured for the no-wildcard case. (Named rather than counted: a
numbered offset inside a section still being edited is how this repository's
callout totals used to rot.) If that holds for a grant without `:*` as well, the pull side
is still open and belongs in the residual inventory rather than in this
paragraph. Nobody has run the probe. Until someone does, treat the pull grant
as *narrowed, not proven* — the two words this repository keeps having to tell
apart.

**A command's frontmatter is a grant like any other, and it is the one nobody
reads twice.** The first five rows above were all found in command frontmatter,
and for a while that supported a second claim — that the global file had it
right all along. **It did not, and the sixth row is where that broke**: the
`--output` write primitive sat in `.claude/settings.json`'s own allow list,
reachable from every command in the repository, and it had been read past for
as long as the frontmatter rows had. The lesson survives with its converse
attached: frontmatter is the grant nobody reads twice, and the global file is
the one everybody assumes somebody else already read.

**Five grants remain wider than the operations they buy**, and all five are
known residuals rather than oversights. **This paragraph is the inventory and
no command file keeps a second total** — `ship.md`'s callout carried one, and
it went stale the moment a branch pinned that file's fetch grant. Two are
`/ship`'s:
`Bash(git worktree remove:*)` admits the `-f` that discards work, and
`Bash(gh pr merge --merge:*)` admits a trailing `--admin` that merges past
failing checks. Helpers are owed for both; until someone with the
`Edit(.claude/scripts/**)` deny lifted writes them, `/ship` carries them by
reporting its literal invocations, flags and all.

The third is `Bash(git fetch origin:*)`, which no longer admits a URL but still
admits a trailing flag; `--upload-pack`, `--receive-pack` and `--exec` are
denied by name, so what is left is the flag nobody has enumerated yet. The
honest fix is the helper the transport issue asked for — a
`git-fetch-origin.sh` taking a branch name and nothing else.

The fourth is not a git grant and is the one a reader is most likely to miss:
**`/review-copilot` triages three comment feeds that no filter narrows by
author.** `pr-review-comments.sh` returns every inline comment on the PR, and
both `gh pr view --json` feeds are unfiltered by construction, while `ship.md`
filters the same data by author. **Not "on `Copilot` authorship", which is the
shorthand this very change set retired one file over**: step 6 filters two
feeds by two *different* logins — inline comments on `Copilot`, review bodies
on `copilot-pull-request-reviewer` — and issue comments on neither, because
step 6 does not read that feed at all. Treating the two it does read as one
identity is exactly what let a two-string allow-list look complete.

**`copilot-pull-request-reviewer[bot]` is REST's spelling and belongs to no
feed either command uses**, which is measured rather than reasoned: it comes
from `/pulls/{n}/reviews`, while the one REST endpoint in play —
`/pulls/{n}/comments`, behind `pr-review-comments.sh` — reports `Copilot`, and
both `gh pr view --json` feeds are GraphQL and report
`copilot-pull-request-reviewer`. An earlier revision of this paragraph called
the issue-comment login the REST spelling; `gh pr view` loads `reviews` and
`comments` through one exporter, so that could never have been true. The command now states the
filter and reports the count it dropped, but that is prose — the enforceable
version is an author filter inside the helper, which is a human's edit made
with the `Edit(.claude/scripts/**)` deny lifted. Until it lands, do not run
`/ship`'s Copilot loop unattended on a PR that outside contributors can comment
on.

The fifth is **`git push` under the two sweeps**, and it is the one that looks
closed and is not. Both commands state a read-only boundary, and both used to
close it with "no `git push` is granted either, so the branch cannot move" —
which reads an *absence* as a control, the exact rule the sentence beside it
had just retired. `.claude/settings.json` **allows** `Bash(git push origin:*)`
and `Bash(git push -u origin:*)` globally, so a push of the current branch does
not prompt at all; only force-pushes and pushes to `main` are denied. Naming
`git push` in each sweep's `disallowed-tools` is the obvious fix and is
**unverified**: that key's `Bash(...)` form has never been measured here — the
`Agent(...)` form is what was — and a nested `claude -p` probe could not
separate a rejected pattern from a command that failed to load. Both files now
state the residual instead of claiming the control.

**A sixth thing is a gap in the mechanism rather than in a grant.** Pinning a
command to one subagent type is a **deny list of every other type**, because
the harness has no "only this type" allow — so `security-sweep.md` and
`bug-sweep.md` each enumerate the registered types that hold a shell, an editor
or the network, and **a newly added agent under `.claude/agents/` is admitted
by default** until someone adds it to both lists. That is the shape this
repository already knows rots; it is taken here because the alternative on
offer is prose.

**The two sweeps are one shape asking two questions**, split by what makes a
finding rather than by where they look. `/security-sweep` files what an
attacker can reach; `/bug-sweep` files what is wrong on its own terms. Both
fork a detached worktree, verify every subagent claim before filing,
de-duplicate against the whole issue set, never fail open, and file without
fixing. Three things differ: the threshold, the fan-out cut, and what
confirmation can mean — **`/bug-sweep` executes none of the snapshot it
audits**, because building a tree executes it (MSBuild targets, source
generators, analysers, and under `dotnet test` the tree's own test code) and
the audited repository is prompt-injection input. So a defect claim there is
confirmed by reading, and the class of bug only execution catches is named as
the residual.

**Both sweeps' worktrees carry the `secsweep-` prefix**, and the second is
borrowing. `git-worktree-detach.sh` and `git-worktree-drop.sh` refuse any path
that is not `secsweep-` plus six characters under the canonical temp root —
the shape check that stops a poisoned finding from naming a sibling PR worktree
and having it deleted. Renaming the prefix would have to move in both helpers
and both callers at once, so it stands; what is lost is attribution.
