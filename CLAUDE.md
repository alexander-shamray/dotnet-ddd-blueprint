# CLAUDE.md

Guidance for Claude Code when working in this repository.

**Four long sections of this file now live beside it under `docs/`.** Every
landed PR appended its findings here and nothing was ever consolidated, so a
file loaded into every session's context had grown past three thousand lines.
Those blocks moved out **verbatim in their arguments**: not one was shortened,
because a summary of an argument is how a rule gets "corrected" back. Each
destination's own header lists what *was* edited on the way out.

| | |
|---|---|
| [`docs/pr-decision-log.md`](docs/pr-decision-log.md) | What each PR from PR-08 on decided — the long half of the phase section |
| [`docs/lessons.md`](docs/lessons.md) | The lessons that generalise past the PR that found them, and the measurement behind each |
| [`docs/harness-boundaries.md`](docs/harness-boundaries.md) | What the harness grants and refuses, and every grant wider than the operation it buys |
| [`docs/testing.md`](docs/testing.md) | How to run every suite and every gate, and what each needs |

**Read the one that covers what you are about to touch**, before you touch it.
They are where the traps are recorded. What stayed here is what an agent needs
in order to *act* whatever it is working on: the repo's shape, the phase, the
one rule, and the style the prose and the code are written in.

The rest of what left went for a different reason — duplication of Appendix D
and of `.claude/commands/*.md`, argued below. **No line count of this file
appears in this paragraph on purpose**: a file that states its own length
invalidates the claim with the next edit, including the edit that fixes it.

**Each of those four is outside the blueprint tree**, so each is in no index
and behind no nav footer, and `/check-links` reaches none of them.
`/validate-blueprint` reaches one — `testing.md`, which it names in its scope.
For the other three the one rule below is all that carries them, and it is all
that does.

**`roadmap.md` is in that command's scope too and is not one of the four**,
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
project *holds* is the job of `appendix-d-type-inventory.md` and §4.1; a second
copy here would be a second thing to reconcile, which the one rule below
forbids. This tree says where things are, not what is in them.

```
docs/backend-architecture/   the blueprint — README index, 01-purpose ..
                             15-cicd-deployment, appendix A (ADR-001..032),
                             B (licences), C (delivery plan), D (type inventory)
docs/roadmap.md              estimates and a calendar laid over Appendix C
docs/pr-decision-log.md      what each PR from PR-08 on decided — the other
                             half of this file's phase section
docs/lessons.md              the lessons that generalise past the PR that
                             found them, and the measurement behind each —
                             this file's other half again, one level up from
                             the log: the log says what a PR decided, this
                             says what the next PR should do about it
docs/harness-boundaries.md   what the harness grants these commands and what
                             it refuses them — the deny list, the sandbox and
                             its residuals, and every grant wider than the
                             operation it buys. Read it before touching
                             anything under .claude/, and state a new residual
                             there rather than in this file
docs/testing.md              how to run the suites — the commands, the
                             Category=Integration filter, which five projects
                             need Docker, what the coverage figure measures.
                             §12 keeps the strategy and wins any disagreement
docs/secrets.md              how a secret reaches a pod and how each kind is
                             rotated — the operational half of §15.4 on
                             testing.md's exact terms, and §15.4 keeps the
                             inventory and wins any disagreement
docs/runbooks/               thirteen since PR-24, and NOT one per alert — the
                             rule files declare fourteen, because §13.8's
                             ownership split makes error rate two rules over
                             one procedure. That is the only sharer and it is
                             declared, with its reason, in check.py's
                             SHARED_RUNBOOKS. Plus a README that is EXCLUDED
                             from the pairing by name — one declared exception,
                             so a second non-runbook file has to be argued for.
                             The number survives here because check 9 now reads
                             §13.6's and §13.9's tables and fails when they and
                             the directory disagree
docs/superpowers/            one frozen spec + plan per PR, written before it

global.json                  SDK pin (§4.4)
.config/dotnet-tools.json    dotnet-ef, pinned to the EF Core version —
                             `dotnet tool restore` is the whole setup
Directory.Build.props        shared MSBuild settings, ADR-019's analyser policy
Directory.Packages.props     central package management, exact pins
Platform.slnx                thirty-three projects
coverage.runsettings         the report filtered to `.*\.Domain\.dll$` (§12.9)
.editorconfig                house style; a build input, not a hint
.github/workflows/ci.yml     secret scan, licence gate and scaffold tests,
                             then restore/build/test/report-coverage — plus
                             `scaffold-build`, which renders a service and
                             COMPILES it, because the scaffold suite reads text
                             and an SDK in that job would cost every run
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
.github/workflows/closure-gate.yml  the ONE workflow with no path filter, and
                             that is the design rather than an omission: what
                             it judges is a property of every pull request, so
                             a filter could only make it skippable — and with
                             nothing read out of the checkout there is no
                             SOURCE_INPUTS list to drift. It is also the only
                             workflow taking `edited`, because the defect it
                             exists for was introduced by an edit to a PR body
                             with no push behind it. **Proposal and enforcement
                             are two executions**: the suite runs the branch's
                             gate, and the gate that JUDGES is read out of the
                             base commit with `git show`, so a pull request
                             cannot supply its own judge. A base carrying no
                             gate fails rather than falling back — the fallback
                             is the silent pass the split exists to refuse.
                             **The workflow file itself is still the branch's
                             copy**, because `pull_request` runs the head
                             definition; only `pull_request_target` reads the
                             base one, at a price this repo refuses. Closing
                             that needs a required status check, and `main` is
                             not protected today
.github/licence-gate/        the gate, its allow-list and its tests
.github/secret-scan/         §15.1's other half since #61 — twelve rules, an
                             allow-list of fingerprints, and its tests
.github/closure-gate/        what a pull request SAYS it closes, against what
                             merging it WILL close. Three statements — the
                             `| Closes |` row, GitHub's own
                             `closingIssuesReferences`, and the keywords in the
                             commit bodies — and **two comparisons, not
                             three**. The missing pairing is deliberate: an
                             issue the description closes and no commit
                             mentions is the ordinary case, so requiring the
                             commit to repeat it would make a commit keyword
                             mandatory, which no rule here states. A test pins
                             that absence, because the symmetry argument is
                             what produced the fourth comparison the first
                             time. Half of what is compared is GitHub's parse
                             and half is a regex, so a too-narrow regex is the
                             fail-open direction and the suite is mostly that
                             parser. A commit list at or above `gh`'s page size
                             is REFUSED rather than judged — a prefix of one
                             page and a complete list of one page read alike
                             from in there. `closingIssuesReferences` needs no
                             such guard and briefly had one: `gh` preloads that
                             collection to exhaustion and does not preload
                             commits, which is why exactly one of the two is
                             exposed. Both historical defects reproduce — PR
                             #112 red on three counts, PR #116 on the two it
                             disclaimed — and it went red on its own branch,
                             over test fixtures a commit body quoted
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
                             seventh build rides on the compose smoke. Since
                             ADR-036 it also carries `definitions.json`, the
                             per-service broker accounts, and
                             `check_permissions.py`, which derives what each
                             service may touch from that service's own source.
                             `guest` is NOT among the accounts, and the
                             mechanism matters: RabbitMQ seeds the default user
                             only on an empty database and skips it when
                             definitions are imported, so a stale volume keeps
                             it and `down -v` is what makes the removal true
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
  Common.Web/                  §10.4, §10.5, §10.6, §11.3, §11.4, §13.2, §13.4
                               and §13.5, and nothing else — the only building
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
  `Directory.Packages.props`, every `.csproj`, `.props` and `.targets`, and
  Appendix B, all as text, needs no restore — which is why §15.1 can put it
  ahead of the build. **Adding a package means adding its backticked identity
  to Appendix B in the same change**, or the gate fails the build before
  anything compiles. It reads the project files because central pinning is a
  convention rather than a constraint: a `PackageReference` naming its own
  `Version`, a `VersionOverride`, a `GlobalPackageReference` or
  `ManagePackageVersionsCentrally` set to `false` each restore a package no
  register row was asked about (#50).
- **The secret scan** sits beside it under `.github/` on the same argument and
  runs in the same job, first — §15.1 draws "SCA + secret scan" as one node.
  Twelve named rules, each with a positive case and a near miss; every
  exception is a `path | rule | fingerprint | reason` line in
  `allowed-secrets.txt`, never a glob and never an inline pragma, and **an
  entry matching nothing fails the build**. It reads the working tree and not
  the history, and it is a pattern scanner: the list of rules is the list of
  things it can find. Both limits are stated in `docs/secrets.md` rather than
  left to be discovered.
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
- **`docs/lessons.md` and `docs/harness-boundaries.md`** are the log's
  position exactly, and they arrived there the same way — sections of this file
  that grew past the point where every session should be paying for them. In
  the scope of neither command, so the one rule carries them too. **Their
  subjects differ in what falsifies them**, which is the thing to know before
  editing either: a lesson is falsified by a *measurement*, so it is amended
  when the code it describes changes and never because it reads oddly; a
  harness boundary is falsified by *running the offending form*, and every
  entry in that file was found that way rather than by reasoning. Neither
  takes a correction argued from the file alone.
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

**CI does this too since #72, in a `scaffold-build` job of its own**, so the
class is caught on the pull request rather than in six months. That does not
retire the block below: the job renders `Yankee` and builds it after the fact,
where running these four locally is how you find out before pushing — and the
cleanup half exists only here, because the runner discards its checkout.

```bash
python tools/new-service/new_service.py Yankee --port 5199
dotnet build tests/Yankee.Api.Tests/Yankee.Api.Tests.csproj
rm -rf src/Services/Yankee tests/Yankee.*
git checkout -- Platform.slnx deploy/compose/
```

**That `git checkout` reverts uncommitted work in `deploy/compose/`, including
work you did *during* the dogfood**, and the warning below about committing
first does not cover it. A fix made because the render exposed something — the
case that actually happens — is in that tree and is reverted by the cleanup,
leaving a commit whose message describes it and whose diff does not. It cost a
gate fix exactly that way.

**Commit the fix before the cleanup; do not copy the file aside and restore
it.** Copying back is the obvious repair and it is wrong, because by then the
file also holds what the scaffold wrote — restoring it returns the
`| Yankee API |` row along with the fix, and that is how probe output reaches a
commit. `git add -p` the intentional hunks and commit them, then run the
`git checkout` against what is left. Both halves were measured in one session:
the revert first, and then the restore bringing the render back while the
sentence recording the revert was being written.

**A rendered service also cannot be committed as it stands
([#161](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/161)).**
It carries seven credential-shaped literals and no
`.github/secret-scan/allowed-secrets.txt` entries, so the mandatory secret scan
refuses it. Five of the seven predate ADR-036 — measured by rendering `Yankee`
at both commits — so this is the scaffold's gap rather than the broker's, and
the dogfood above is unaffected because it deletes what it rendered.

The scaffold edits six tracked files as well as creating its own, so the
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
exist — plus the runbooks, `docs/secrets.md`, the dashboards and the k6
SLO run. It also found that **four of §13.6's alerts read an instrument
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
suites, exactly as `pricing.proto` is.
PR-31 is **the security control the plan never rowed**, and it is PR-28's shape
rather than PR-30's: nothing here corrects a specification, because §9.4
described `CommandOrigin` accurately — including its own callout that queue
arrival "is only as restrictive as the broker's authorisation" and that "this
chapter does not specify one". The chapter was right, the code matched it, and
the control it named as absent stayed absent. The broker now has a per-service
identity ([ADR-036](docs/backend-architecture/appendix-a-adrs.md#adr-036--the-broker-has-a-per-service-identity)),
`guest` is gone, and a gate derives each account's permissions from that
service's own source. **Three residuals are stated rather than closed** —
`configure` cannot be exclusive, `read` on a peer's command endpoint grants the
consume along with the bind, and provisioning a deployed broker is an
obligation nothing here checks.

PR-28 is **not in the original plan and had to be added to it**, which is a
different thing from a PR being late: §8.5 specified six types, four chapters
cited them, and `grep -in "idempotenc"` over Appendix C's twenty-seven rows
returned nothing — so five source files deferred to "§8.5's PR", a row that did
not exist. It built the behaviour, the Redis store and the fourth pipeline
seat, and found that **`AddRedisConnections` had no caller anywhere in `src/`**:
PR-12 built §8's whole stack and wired it into no host, so §8's deployment
wiring — Compose, both charts, both API fixtures — came with it.

**Every mandatory PR in the plan has landed, the one optional PR has landed,
and Appendix C now carries an "After the plan" section.** So "there is no next
PR" is no longer the right sentence: the plan being finished is not the same as
the blueprint being built, and a deferral to a complete plan is a dead
reference rather than a schedule.

**That section now holds more than one kind of row, and the third kind is the
one to read before adding a fourth.** PR-28 was a mechanism the plan never
rowed and PR-29 was half a node it split without saying who owned the other
half — both gaps in coverage. PR-30 is neither: §9.8 printed
`e.UseInMemoryOutbox(context)` on the saga endpoint, PR-21 built exactly that,
and the plan delivered what it specified. **The specification was wrong.** So a
row can now record a correction to a landed row, and what earns it one is not
the size of the diff but whether a rule moved — ADR-032 took an exception to
§9.3's prohibition on a second outbox table set, which is a rule four chapters
rest on. A fix that moves no rule is a commit body, not a row.

`Platform.slnx` holds thirty-three projects, thirteen of them test projects,
and `dotnet test` runs 1,052 tests — so the build rules and the drift rules
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
where the current set lives, and which no longer states a count either — this
sentence used to give the figure two sections above the one that owned it, and
the two disagreed the moment a ninth suite landed; the owner has since dropped
its own figure for the same reason. **The owner is now a different file,
which makes the point rather than weakening it**: a count restated across a
file boundary goes stale on the far side's clock and reads as authoritative on
this one. That first one: `py -3.12 -m unittest` in
`tools/new-service` runs 84, and CI has a `scaffold` job for them beside
`licence-gate` — plus `scaffold-build`, which compiles what they only read.

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
dotnet test  Platform.slnx --filter "Category!=Integration"   # 847 of 1,052, no daemon
```

**[`docs/testing.md`](docs/testing.md) is the operational reference and this is
the short form.** It carries every other runner — the scaffold's, the chart
gate's and the Python gates' — what each one needs, how to run a gate as
opposed to its suite, which five projects need Docker, the three CI stages and
what the coverage figure is measured over. **§12 wins where the two disagree**,
exactly as Appendix C wins over the roadmap.

**Three runners, and only one of them is `dotnet test`.** The scaffold's tests
are Python, the chart gate is bash over `helm template`, and the licence gate,
the secret scan, the observability gate, the pipeline gate, the coverage
reporter, the canary, the closure gate and the review helpers are Python again;
none is in `Platform.slnx`, so a green solution says nothing about any of them.
**Each of them is tested and then run**, which is the pattern every gate here
follows — the licence gate was once left out of this list on the reasoning that
a gate is not a suite, and it is both.

**No count opens that sentence, and its removal is the fix rather than a
recount.** It said seven, then ten, and #61's secret scan made it eleven inside
the pull request that was correcting the sentence around it. What a reader can
check is whether that enumeration matches `docs/testing.md`'s block and the
jobs in `ci.yml`; that check needs no numeral in front of it.

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
category runs a smaller suite and says which. `Category!=Integration` is 847 of
the 1,052 and starts no container — measured with `docker events`, not
inferred — and `Category=Integration` is the other 205, needing the daemon
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
- **This file and the three it delegates to are inside the rule too** —
  `docs/pr-decision-log.md`, `docs/lessons.md` and
  `docs/harness-boundaries.md`. None is reached by `/validate-blueprint` or
  `/check-links`, so nothing structural will catch their drift, and the
  delegation makes that sharper rather than softer: a rule that used to sit in
  one file now sits in two, so **the change that moves it has to reach both**.
  A pointer here and an argument there is one claim in two places, which is the
  shape this rule exists for.

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
  admonition syntax. Two forms are named and recurring — `**Trap — …**`
  (21) for a mistake worth naming, and `**Decision — …**` (10), which always
  points at the ADR that records it:

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
  `Trap` was the first test of that; a seventeenth has since landed and was
  reconciled the same way. This paragraph used to say *what never
  drifted is 15 and 8*; `Trap` has now moved twice, and the lesson survives the
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
  `admin/admin`. Those defaults are what make `docker compose up` work with
  no prior setup; the environment variable in front of each is the seam that
  keeps them out of anything deployed.

  **The broker is the one that is NOT fronted by a variable, and since
  ADR-036 it is not `guest/guest` either.** Each service carries a literal
  `{service}-svc` / `local-dev-{service}`, because those credentials are
  imported into the image from `definitions.json` and a `${...}` in front of
  the connection string would front only one half of a pair — the broker
  would still expect the compiled-in password. Rotating them locally is an
  edit to that file and a `docker compose down -v`, which is stated in
  `deploy/compose/README.md` rather than implied by a seam that is not
  there.

### Settled choices — do not "fix" them

| | |
|---|---|
| Namespaces | File-scoped (`namespace X;`), never block-scoped |
| Extension declarations | C# 14 `extension(T receiver)` blocks where a class groups several extensions on one receiver — `Common.Application.DependencyInjection` is the worked example. **The corpus is currently split**: every extension class in `Common.Web` still uses the classic `this`-parameter form. Four extend a receiver nothing else does — `ProblemDetailsExtensions` (`IServiceCollection`), `HealthCheckExtensions` (`IEndpointRouteBuilder`), `AuthorizationPolicyExtensions` (`AuthorizationPolicyBuilder`) and `ResultExtensions` (`Result`) — while two groups share one: `CorrelationIdExtensions` and `SecurityHeadersExtensions` on `IApplicationBuilder`, and `ObservabilityExtensions`, `AuthenticationExtensions` and the `CommonWebDefaultsExtensions` that composes them on `IHostApplicationBuilder`. Whether to group those three is open and deliberately unsettled — they are separate files because one composes the other two, and merging would put a caller-facing entry point in the same block as the pieces it calls. The receivers are listed rather than counted because a bare count is what went stale here — twice now: this cell said six and four from before PR-16 added `AuthenticationExtensions`, then eight and five until `SecurityHeadersExtensions` landed on a receiver `CorrelationIdExtensions` already had, which is the split that makes "extends a receiver nothing else does" the wrong shape for a tally. Converting anything is a decision about the whole corpus |
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

**Column aliases are assignments, not `AS`** — `Total = SUM(…)`, never
`SUM(…) AS Total`. The name being defined then starts the line, so a
projection reads as the row shape it produces, and the `=` column lines up the
way `SET`'s does. The running example is §6.5's history query, whose total is
summed from the order's lines: `ordering.Orders` stores no total, because the
aggregate computes one and §7.2 declines to keep a second copy of it. This is
the SELECT list only: `MERGE … AS target`,
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
  (currently ADR-037) and keep the
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
  it.** `security` or `bug`, plus one of `critical`, `high`, `medium`, `low` —
  the six `.claude/scripts/gh-label-ensure.sh` will create, and no others. Both
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
