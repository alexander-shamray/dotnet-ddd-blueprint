# Repo map

**What every entry in `CLAUDE.md`'s tree is, and why it is shaped the way it
is.** This was that file's *The tree*, *Edges between building blocks*, *Files
outside the blueprint tree, and why* and *The scaffold* — gate mechanisms,
workflow triggers, a broker's user-seeding behaviour and a dogfooding
procedure, sitting under a heading whose own first sentence promises one line
per entry. **It is load-bearing whenever a fact it records changes** — an
entry added or removed anywhere in the tree, a gate's shape, a service's
project set, what a `.TestSupport` library is for — and inert for a change
that touches none of them, which is what earns it a file rather than a
permanent seat in every session's context. **The trigger is deliberately
not a directory list**: this sentence named `.github/`, `deploy/` and
`tools/` while the tree below also records `src/`, `tests/` and the root
files, so an agent adding a service or changing the BFF's shape was told
this file was inert and could leave it stale. A scope that lists the
directories somebody remembered is the same defect as a gate whose
selector stops matching the newest surface.

**The content is verbatim in its arguments**, on
[`pr-decision-log.md`](pr-decision-log.md)'s terms and for its reason: a summary
of an argument is how a rule gets "corrected" back. The edits made on the way
out are named rather than counted, because naming them is cheaper than a
claim that does not survive a grep — and because a count of them is the one
thing in this paragraph that can go stale. It said *three* while listing two,
for exactly as long as nobody read the number against the list.

The four `###` headings became `##`, so this file has one title above them.
**Self-references were rebased**: sentences saying "the one rule below" and
"this file" meant `CLAUDE.md`'s one rule and `CLAUDE.md` itself, and each
would otherwise point at nothing or — worse — at this file, which states
neither. Not one argument was shortened and no paragraph was dropped.

**Entries were then added by review, which is a departure from the verbatim
move and belongs in the same list.** The tree arrived here missing
`docs/repo-map.md` and `docs/style-guide.md` — the two files this change
creates — and missing `broker-permissions.yml`, which `CLAUDE.md` had never
listed either and which this file inherited rather than dropped. All three
are in now.

**The annotated tree came across whole, and `CLAUDE.md` now carries a
re-derived locator in its place.** That is the one thing here with a *shorter
twin* rather than a pointer, and it is declared in both files because a reader
who greps the two will find them disagreeing in length and needs to know which
is authoritative. **This one is.** Where a line here says more than the line
there, that is the split working rather than drift; where the two disagree
about *where a thing lives*, one of them is a bug report against the other.

**Some triggers were deliberately left behind rather than moved**, under
*What the map is load-bearing for* in `CLAUDE.md` — the unused-reference rule,
the two about Catalog breaking the scaffold, the licence register, and what a
`.TestSupport` project is. Each fires before anyone would think to open this
file, which is the only reason this repository holds a rule in two places at
all. Each is argued in full below, and this file is the master copy of every
one of them.

## The tree

**One line per entry, because the inventory lives elsewhere.** What each
project *holds* is the job of `appendix-d-type-inventory.md` and §4.1; a second
copy here would be a second thing to reconcile, which `CLAUDE.md`'s one
rule forbids. This tree says where things are, not what is in them.

```
docs/backend-architecture/   the blueprint — README index, 01-purpose ..
                             15-cicd-deployment, appendix A (ADR-001..042),
                             B (licences), C (delivery plan), D (type inventory)
docs/roadmap.md              estimates and a calendar laid over Appendix C
docs/pr-decision-log.md      what each PR from PR-08 on decided — the other
                             half of `CLAUDE.md`'s phase section
docs/lessons.md              the lessons that generalise past the PR that
                             found them, and the measurement behind each —
                             `CLAUDE.md`'s other half again, one level up from
                             the log: the log says what a PR decided, this
                             says what the next PR should do about it
docs/harness-boundaries.md   what the harness grants these commands and what
                             it refuses them — the deny list, the sandbox and
                             its residuals, and every grant wider than the
                             operation it buys. Read it before touching
                             anything under .claude/, and state a new residual
                             there rather than in `CLAUDE.md`
docs/repo-map.md             this file — what every entry here is and why
                             it is shaped that way. It lists itself because
                             the locator in `CLAUDE.md` does, and a map
                             missing an entry its own shorter twin carries
                             has falsified the header above rather than
                             merely gone short
docs/style-guide.md          the prose, C# and SQL dialect both artefacts
                             are written in, and which rules the build
                             enforces rather than a reviewer
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
.github/workflows/broker-permissions.yml  ADR-036's broker ACL, and the
                             FOURTH workflow to reach outside its own
                             tree: src/Services/**, Common.Contracts and
                             Catalog.TestSupport, declared as
                             SOURCE_INPUTS in check_permissions.py. Split
                             from compose.yml rather than added to it,
                             because that smoke pulls ~2 GB and builds
                             seven images while this gate reads text — and
                             path filtering is per-workflow, not per-job,
                             so sharing one would either drag the smoke
                             onto every messaging change or leave the gate
                             blind to the code it checks
.github/workflows/realm.yml  §11.3's token obligations on deploy/keycloak/**,
                             and the FIFTH workflow to reach outside its own
                             tree: deploy/compose/keycloak/realm-export.json,
                             which is the subject, and Common.Web's
                             AuthenticationExtensions.cs, out of which the 300
                             a realm owes is READ rather than restated — and,
                             since ADR-043, deploy/canary/**, whose plan the
                             scheduled job loops over — all declared as
                             SOURCE_INPUTS in realm_check.py,
                             which asserts the triggers cover every entry and,
                             in the other direction, that no path the gate
                             reads is missing from the list. Split from
                             compose.yml on broker-permissions.yml's
                             reasoning: path filtering is per-workflow, so
                             sharing one would drag that smoke's pulls onto
                             every identity change or leave this gate blind to
                             the realm it reads. Two jobs since ADR-043:
                             `check` is the local half, on the two triggers
                             that carry a diff, and `deployed` is the third
                             moment — the rollout's own derive-fetch-judge
                             calls over every release the canary plan names,
                             hourly and on dispatch, under the production
                             Environment, opted in by the repository variable
                             REALM_CHECK_SCHEDULED and filing a tracker issue
                             when red. The realm a rollout is about to land
                             on is still judged from deploy.yml; this is the
                             moment between rollouts
.github/licence-gate/        the gate, its allow-list and its tests
.github/secret-scan/         §15.1's other half since #61 — twelve rules, an
                             allow-list of fingerprints, and its tests. Since
                             #161 it has a second caller: §4.5's scaffold
                             imports `secret_scan.py`, runs it over what it
                             rendered, and appends the accepted-finding lines
                             that render needs — so this gate is a library as
                             well as a job, and its matching has exactly one
                             implementation
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
deploy/keycloak/             §11's token obligations against a Keycloak realm
                             representation, since PR-36. `realm_check.py`
                             decides, `read_admin.py` is the one file that
                             talks to anything, and the suite is the mutations
                             the decision has to refuse. ADR-042's one
                             predicate has TWO subjects and they reach
                             differently: `realm.yml`'s `check` job judges
                             §14.1's Compose export and reaches only files —
                             that export and the `AccessTokenLifetime` the 300
                             is READ out of rather than restated — while
                             `deploy.yml`'s rollout job judges the realm a
                             deployment points at, and since ADR-043
                             `realm.yml`'s own `deployed` job judges the realm
                             every release points at between rollouts, hourly
                             and on dispatch, under production, opted in by
                             REALM_CHECK_SCHEDULED and filing an issue when
                             red. Those two reach a live realm and have never
                             run. `--kind` has no default because one
                             obligation inverts between the two: §11.2's
                             password grant is on locally and off in a
                             deployed realm

src/BuildingBlocks/          all five, and complete since PR-15
  Common.Domain/               Entity<TId>, AggregateRoot<TId>, IDomainEvent
                               and friends — no packages
  Common.Application/          Result and Error; §6.2's dispatcher; all
                               of §6.3's behaviours; §6.5's CursorPage<T>;
                               §7.5's ports; and §8.5's marker port and its
                               scoped key carrier, added by PR-32
  Common.Contracts/            §4.3's one assembly that crosses a service
                               boundary. No packages and no project references,
                               and both absences are the point — anything this
                               referenced would travel into every service
  Common.Infrastructure/       §8's Redis helpers and §8.5's durable
                               idempotency marker, §9's outbox, inbox,
                               consumers and the retention purge that now
                               covers all three tables
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

## Edges between building blocks

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

## Files outside the blueprint tree, and why

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
  `/validate-blueprint` nor `/check-links`. The one rule in `CLAUDE.md` is
  the only thing carrying it.
- **`docs/lessons.md` and `docs/harness-boundaries.md`** are the log's
  position exactly, and they arrived there the same way — sections of
  `CLAUDE.md` that grew past the point where every session should be paying
  for them. In the scope of neither command, so the one rule carries them
  too. **Their
  subjects differ in what falsifies them**, which is the thing to know before
  editing either: a lesson is falsified by a *measurement*, so it is amended
  when the code it describes changes and never because it reads oddly; a
  harness boundary is falsified by *running the offending form*, and every
  entry in that file was found that way rather than by reasoning. Neither
  takes a correction argued from the file alone.
- **`docs/repo-map.md` and `docs/style-guide.md`** are that position again
  and arrived the same way, so **this file is one of them** and the rule
  above is not hypothetical here. What falsifies them differs once more: a
  map entry is falsified by the tree it describes — read the directory, not
  the paragraph — and a style rule is falsified by the corpus, which is why
  `/style-pass` changes every site before it changes the rule.
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

## The scaffold

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
git checkout -- Platform.slnx deploy/compose/ .github/secret-scan/allowed-secrets.txt
```

**That `git checkout` reverts uncommitted work in `deploy/compose/` and in the
allow-list, including work you did *during* the dogfood**, and the warning
below about committing first does not cover it. A fix made because the render
exposed something — the
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

**A rendered service carries credential-shaped literals, and since
[#161](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/161)
the scaffold writes their allow-list entries itself.** §15.1's secret scan
reads the working tree, so a render with no
`.github/secret-scan/allowed-secrets.txt` entries beside it could not be
committed at all — it used to leave seven findings and no reasons. The scaffold
now **loads the real scanner**, runs it over what it has just rendered, and
appends one `path | rule | fingerprint | reason` line per distinct finding. The
fingerprints are the gate's own: a scaffold computing them would be a second
implementation of which substring each rule matches, and a fingerprint matching
nothing is a stale entry the scanner fails the build on. **It writes nothing
where `.github/secret-scan/` is absent**, which is the scaffold suite's
synthetic root — a degraded path that suite therefore cannot assert.

The scaffold edits seven tracked files as well as creating its own — the
allow-list is the seventh, which is why the `git checkout` above names a third
path — so that cleanup is part of the procedure rather than tidying after it.
**Commit before dogfooding**, though, if the PR itself changes
`deploy/compose/` or that allow-list — the cleanup reverts the tree's own
changes, and it has cost work once.

**The probe is `Yankee` at 5199, and must not be a real service's name.** It
used to be `Ordering` at 5103; PR-18 made Ordering real, so the create refuses
the taken name and port — and the `rm -rf`, followed literally, deletes the
service. `Yankee` is one of the probes the scaffold's own suite uses, chosen
because a probe cannot quietly become a service later.
