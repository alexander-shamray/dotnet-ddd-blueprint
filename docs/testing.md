# Testing

**How to run the tests, and what runs where.**
[§12](backend-architecture/12-test-strategy.md) is the strategy — the pyramid,
the TDD cycle, what each level is for and what not to test. This file is the
operational half: the commands, the categories, what needs Docker and what the
coverage figure is measured over.

The split is deliberate rather than tidy. §12 is a chapter of the blueprint and
is written for someone deciding *what to test*; this file is written for
someone with a checkout, and it goes stale on a different clock — a new runner
flag belongs here and changes nothing about the strategy. **Where the two
disagree, §12 wins**, and the disagreement is a bug report against one of them.

> **This file is outside the blueprint tree, so nothing structural catches its
> drift.** It is in no index and behind no nav footer, exactly like
> `roadmap.md` and `pr-decision-log.md`. `/check-links` does not reach it and
> `/validate-blueprint` reaches it only because it is named in that command's
> scope. The one rule in `CLAUDE.md` covers it, and that is all that does.

## The suites

Ten of them, three runners, and `dotnet test` says nothing about the other
nine:

```bash
dotnet tool restore                # dotnet-ef, pinned in .config/
dotnet restore Platform.slnx
dotnet build Platform.slnx
dotnet test  Platform.slnx         # needs a running Docker daemon

(cd tools/new-service && py -3.12 -m unittest)  # no Docker, no SDK

bash deploy/helm/smoke.sh                       # needs helm 3, no Docker, no SDK

py -3.12 deploy/observability/check.py          # no helm, no Docker, no SDK

(cd .github/licence-gate && py -3.12 -m unittest)        # ADR-019's register gate

py -3.12 -m unittest discover -s .github/pipeline-gate   # PR-25's quality gates
py -3.12 -m unittest discover -s .github/coverage        # the coverage merge
py -3.12 -m unittest discover -s deploy/canary           # §15.5's rollout
py -3.12 -m unittest discover -s .github/closure-gate    # what a PR closes
py -3.12 -m unittest discover -s .claude/scripts         # the review loop's helpers
```

**The licence gate is in that list because CI runs it on the same terms**, and
leaving it out is what made this count seven. `ci.yml` tests the gate and then
runs it — the pattern every gate here follows — so a suite that ships with the
repository, runs in CI, and is invisible to `dotnet test` is one of these
whatever directory it lives in.

**Only the first is a §12 suite, and the other nine are here anyway**, because
this file is written for someone with a checkout rather than for someone
deciding what to test. The scaffold's tests exercise a developer tool; the
chart gate renders `deploy/helm/` and asserts what comes out (§15.3); the
observability gate pairs §13.6's alerts with §13.9's runbooks both ways and
checks that every metric a loaded rule reads is one something publishes;
PR-25's three cover the pipeline's own inventories, the coverage merge, and
§15.5's rollout arithmetic; the closure gate compares what a pull request
says it closes against what GitHub will actually close; and the last covers
the review loop's own helpers under `.claude/scripts/`. None
is in `Platform.slnx`, so a green solution says nothing about any of them,
which is exactly why a person needs to be told they exist.

**The tenth needs `bash`, `grep`, `git` and `jq`, and no network** — the `gh` its
ledger cases call is a stub on `PATH`, and the `git` is real rather than
incidental: one case detaches a worktree at `HEAD` and removes it again, because
a helper's contract is its stdout and only a round trip tests what a caller
actually captures. It is the one suite whose
subject is agent tooling rather than the platform, and it is here on the licence
gate's terms: it ships with the repository, CI runs it, and `dotnet test` is
blind to it. **The judgements it covers, five of which shipped wrong at least
once and are each reproduced as a case that fails against the old behaviour** —
what counts as a
usage limit, what counts as a review that finished, whether the check ledger can
publish an answer on its own error path, whether every usage-limit skip happens
before a slot is reserved, whether the sweeps' worktree guard is the
direct-child check it claims, and whether the label helper leaves a free
parameter a finding could steer. That last is the one that never shipped
wrong — it is a grant closed by moving it into a helper, and the suite is what
keeps it closed. Since #56 and #33 it also covers what a Copilot feed helper
admits, what it reports about what it dropped, and that no command can reach
those feeds outside the fixed helpers; and which paths the harness denies
itself — both of that last kind.

**No count opens that list any more**, here or in `.github/workflows/ci.yml` or
`CLAUDE.md`, and the three enumerations are what a reader compares instead. The
numeral said four, then five, then six, and was stale again inside the pull
request that added two subjects — a figure restated in three files goes stale in
all three at once. The regression
negatives are paired with positive controls, and those are not decoration: a
negative that passes because the pattern matches *nothing* is indistinguishable
from one that works, so the accepted values and the limit pattern's status
anchor carry controls of their own.

**The closure gate's suite is the whole of what runs here.** The gate itself
needs a pull request and a `gh` token, so the live invocation belongs in CI
and in `/pr` rather than in this block:

```bash
gh pr view <n> --json number,url,body,commits,closingIssuesReferences,headRefOid |
    py -3.12 .github/closure-gate/closure_gate.py
```

Each has its own reference for what it asserts and — more usefully — what it
does not: `deploy/helm/README.md`, since that gate reaches no cluster, and
`deploy/observability/README.md`, since that one reaches no Prometheus and does
not validate rule syntax.

**`py -3.12`, not `python`.** Every CI job that runs Python pins 3.12, and a
newer interpreter is the hazard — it accepts APIs 3.12 does not, so the local
suite goes green on code the runner cannot execute. The scaffold *script* is a
different matter: running it is not a test of the floor, so plain `python` is
fine there.

## Docker is not optional

**Five projects need a daemon** — `Catalog.Api.Tests`,
`Catalog.Application.Tests`, `Common.Infrastructure.Tests`,
`Ordering.Api.Tests` and `Web.Bff.Tests` — each with its own collection and
therefore its own container set ([§12.4](backend-architecture/12-test-strategy.md)'s
stated price). Without one they fail on `Failed to connect to Docker
endpoint`, which is a true statement about the machine rather than a defect in
the branch.

**They are not skipped when the daemon is absent, and that is a decision.** A
skip on a missing daemon **fails open**: CI goes green on a runner whose Docker
broke, and nobody finds out until the thing those tests were guarding breaks in
production. [ADR-010](backend-architecture/appendix-a-adrs.md) already made
real infrastructure non-optional, and this is the same rule one layer down.

**A category is the opposite of a skip, which is why one exists and the other
does not.** Selecting a test *out* by category runs a smaller suite and says
so; skipping it runs the suite and reports a pass. The next section is about
the first.

## Categories

Every test that needs a container carries `Category=Integration`. Everything
else carries no category at all, so the two filters partition the suite:

```bash
dotnet test Platform.slnx --filter "Category!=Integration"   # no daemon needed
dotnet test Platform.slnx --filter "Category=Integration"    # the container half
```

**The trait is declared on the collection definition, not on each test class,
and that is the whole design.** xUnit v3 applies a collection's traits to every
test in it, so *joining the container collection is carrying the category*.
There is no per-class attribute for a new test class to forget, and no
reflection gate needed to check that nobody did — the thing that decides the
category is the same thing that decides whether the test gets a container.

```csharp
[CollectionDefinition(nameof(IntegrationCollection))]
[Trait("Category", "Integration")]
public sealed class IntegrationCollection : ICollectionFixture<ServiceFixture>;
```

> **Measured rather than assumed, because the propagation is the load-bearing
> half.** On `Common.Infrastructure.Tests`, `Category=Integration` selects the
> ten tests of the two classes in the collection and `Category!=Integration`
> selects the other seventy-one — 81 in total, with no third state and nothing
> counted twice.
>
> **Those are the runner's numbers, and `--list-tests` gives different ones.**
> Discovery reports 82 for that project where execution reports 81, so a
> partition quoted from `--list-tests` does not reconcile against anything else
> here — the 915 is summed from `dotnet test` output, and mixing the two is how
> this callout first came to claim 72 and 82. Quote what ran.
>
> Across the solution the split is **725 and 190 of 915**, and the fast half
> runs in about 76 seconds.
>
> **No container starts in that run**, which is the half worth proving rather
> than inferring: `docker events --filter event=create` over the window
> reported nothing, using the same probe that captured a control container
> started beside it. The mechanism is that xUnit constructs a collection
> fixture only when a test in that collection runs, so filtering the collection
> out means the container is never asked for rather than started and left
> unused.
>
> An earlier attempt to prove the same thing by pointing `DOCKER_HOST` at a
> dead endpoint proved nothing at all: Testcontainers ignored the variable on
> this host and the integration half passed against the real daemon anyway.
> Recorded because a green run under a broken override reads exactly like a
> green run under a working one.

The five declarations are in `Catalog.Api.Tests`,
`Catalog.Application.Tests`, `Common.Infrastructure.Tests`,
`Ordering.Api.Tests` and — as `KeycloakCollection` — `Web.Bff.Tests`. That last
is the clearest case for categorising a **collection** rather than a project:
73 of its 77 tests never needed a container and 4 need an identity provider, so
a project-level split would have had nothing to split. What it buys there is a
container start rather than a fast suite — the BFF's fast half still takes
about a minute, because §9.7's resilience tests wait on real timeouts.

**A test class that needs a container and forgets the collection fails loudly
rather than quietly**, which is the direction this has to fail in. It has no
fixture, so it does not run against one; it also carries no category, so it
runs in the fast half and fails there. What it cannot do is report a pass.

**CI runs the two *category* halves separately since PR-25**, which is
[§15.1](backend-architecture/15-cicd-deployment.md)'s `UT → IT`. Three
`dotnet test` invocations, not two, and the seams answer different questions:
the first is architecture gates versus everything else, for the instrumentation
reason under Coverage below, and the second is `Category=Integration`. Measured
on this repository they are **18**, **707** and **190**, summing to the 915 the
whole suite runs — which is the arithmetic the callout below asks for.

```bash
dotnet test Platform.slnx --filter "FullyQualifiedName~ArchitectureTests" \
    --logger trx --results-directory ./TestResults/architecture
dotnet test Platform.slnx --filter "FullyQualifiedName!~ArchitectureTests&Category!=Integration" \
    --logger trx --results-directory ./TestResults/unit
dotnet test Platform.slnx --filter "Category=Integration" \
    --logger trx --results-directory ./TestResults/integration

py -3.12 .github/pipeline-gate/pipeline_gate.py stages \
    ./TestResults/architecture ./TestResults/unit ./TestResults/integration
```

**The logger and the directories are not decoration here either.** The gate
counts from TRX and looks for those three directory names, so a bare
`dotnet test` runs the stages and leaves it nothing to read — following this
file without them produced three green runs and a gate that could not be run
at all. Both halves of the split are stated because the gate is what makes the
counts above a check rather than a claim.

**Separate steps in one job, not separate jobs**, and both halves of that are
deliberate: a job boundary would mean shipping the build output between runners
to keep `--no-build` honest, and the coverage figure is the union of the last
two, which wants one place to be merged.

> **A filter is a new way for a suite to not run, and that is
> [§12.1](backend-architecture/12-test-strategy.md)'s oldest trap wearing
> different clothes.** A missing test adapter makes `dotnet test` report no
> tests and exit **zero**; a mistyped `--filter` does exactly the same. The
> counts above are what makes the difference visible — 725 and 190 summing to
> 915 — so whoever writes the staged pipeline should assert a floor on each
> stage's count rather than trusting a green exit. That assertion is PR-25's
> quality gate and is named here because this PR is what created the way to
> get it wrong.
>
> **It shipped, as `.github/pipeline-gate/pipeline_gate.py stages`, and it is
> more than the floor this callout asked for.** A floor is a number in a file
> and numbers in files go stale, so it carries the weaker half: the floors sit
> well under the measurements above, because what they grope for is an
> order-of-magnitude miss rather than ordinary churn. The half with no number
> in it does the work — **every test project in `Platform.slnx` ran in some
> stage, no stage was empty, and no test ran in two.** The last of those turns
> `ci.yml`'s "exhaustive and disjoint by construction" from a claim into a
> check, and on the integration stage an overlap is a container set paid for
> twice.

## Coverage

**Reported, not gated** — [§12.9](backend-architecture/12-test-strategy.md)
calls coverage a diagnostic rather than a target, and a diagnostic wired to a
build failure stops being read and starts being satisfied. **PR-25 declined the
threshold on that argument** rather than leaving it owed; its quality gate is
the stage check above, whose subject is whether a suite ran at all. What ships
here is the number, measured over the layer where it means something.

```bash
dotnet test Platform.slnx --filter "FullyQualifiedName!~ArchitectureTests&Category!=Integration" \
    --collect:"Code Coverage" --settings coverage.runsettings \
    --results-directory ./TestResults/unit
dotnet test Platform.slnx --filter "Category=Integration" \
    --collect:"Code Coverage" --settings coverage.runsettings \
    --results-directory ./TestResults/integration
python .github/coverage/domain_coverage.py ./TestResults/unit ./TestResults/integration
```

**Both stages, because the figure is the union and not either half.** §12.9
asks for the domain assemblies "over the whole run", and the domain is
exercised on both sides of the category: measured here, the unit stage covers
253 of 308 method lines, the integration stage 192, and the union **257** —
four lines reached only by a test that needs a container.

The reporter merges rather than reading one file, and it has to. Adding
`--logger trx`, which the stage gate counts from, changes where the collector
writes: each stage then leaves the run's merged attachment **and** one partial
per test project — eight files for one stage here, three of them empty. Hits
are merged with `max` over a key that reproduces the collector's own
`lines-valid` exactly, so reading the same attachment twice, which that layout
guarantees, cannot inflate the figure.

**`--results-directory` is not decoration, and leaving it off is why this
command is written in full.** Without it the collector writes under each *test
project's* own `TestResults/` — measured, one file at
`tests/Catalog.Domain.Tests/TestResults/<guid>/` — and the reporter, which
defaults to `./TestResults` at the repo root, then finds nothing and exits
non-zero. The filter keeps §4.2's architecture gates out of the instrumented
run, for the reason the callout at the end of this section gives.

`coverage.runsettings` filters the report to `.*\.Domain\.dll$` and emits
Cobertura. That is §12.9's "watch coverage of the domain layer specifically —
that is where it should be near-total, and where it is cheapest to achieve",
made into an artefact instead of an instruction.

Three things about that filter are deliberate:

- **It is a pattern, not a list.** `Catalog.Domain`, `Ordering.Domain` and
  `Common.Domain` match it today and every later service's Domain matches it
  the day it exists. A list would have to be edited by whoever adds a service,
  which is exactly the edit that gets missed.
- **It measures the domain assemblies over the *whole* run**, not the domain
  test projects. Domain types are exercised by application and API tests too,
  and a figure taken from `*.Domain.Tests` alone would under-report the thing
  it is named after.
- **The collector is `Microsoft.CodeCoverage`**, which arrives with
  `Microsoft.NET.Test.Sdk` and is therefore already in every test project. No
  package was added and no licence-register entry was needed
  ([Appendix B](backend-architecture/appendix-b-licences.md)); a coverage
  figure is not worth a new dependency.

**A run without `--logger trx` writes a single `*.cobertura.xml` under
`TestResults/<guid>/`**, because the collector merges every test project's
data into one attachment. That is the layout the reporter used to assume, and
the commands above are not it: the stage gate needs TRX, and the TRX logger
makes each test project write its own partial attachment beside the merged one.
So the reporter unions whatever it finds across both stage directories rather
than reading a file, and the figure is `lines-covered / lines-valid` over that
union — with `<package name="…">` naming each assembly, as before.

**Do not reason from the single-file layout.** Both are real and which one you
get depends on a flag several paragraphs away; the union is correct under
either, which is why it is what ships.

> **CI runs this as a second step over the *complement* of the architecture
> gates, and that seam is instrumentation rather than preference.** §4.2's
> gates read `GetReferencedAssemblies` on the Domain assemblies;
> `coverage.runsettings` instruments exactly those and nothing else; and on
> the Linux runner an instrumented Domain assembly reports a `netstandard`
> reference its source cannot have. Both Domain gates went red on the first CI
> run that collected coverage.
>
> **It does not reproduce on Windows**, where the same collector leaves
> `Ordering.Domain.dll` byte-identical — checked by hashing it either side of a
> run — so a green local suite says nothing about this and the runner is the
> only thing that can.
>
> Adding `netstandard` to the Domain allow-list was the one-line alternative
> and is the wrong one: it relaxes an architecture rule everywhere, for ever,
> and in every service the scaffold renders, to accommodate a test tool. The
> gates run first and uninstrumented instead. The two filters are exhaustive
> and disjoint, so the counts still sum to the whole suite.
>
> **The pair is deliberately not restated here, and its absence is the fix
> rather than an omission.** It was corrected twice and went stale twice: 16
> and 760 summing to 776, which matched neither the 794 before PR-25's branch
> nor the 795 after it; then 18 and 794, which held only until the suite passed
> 812. Each repair was a fresh copy beside a total it is not derived from, so
> each went stale on the total's clock rather than its own — and the third
> repair would have too. **A number nothing recomputes is a claim waiting to be
> wrong**, and this one had no reader that could notice.
>
> What derives the split instead is `pipeline_gate.py stages`, which asserts
> the three seams still partition the suite — this callout describes the
> *first* of them. That is a check rather than a copy, and it cannot go stale
> without going red.

## Where a test goes

[§12.1](backend-architecture/12-test-strategy.md)'s table is the answer and
every row names a project. Two things about it are easy to get wrong from
inside a checkout:

- **`*.TestSupport` is not a test project**
  ([§4.1](backend-architecture/04-solution-structure.md)). It exists so that
  two suites can share a fixture without referencing each other, and it holds
  no `[Fact]`.
- **Ordering's handler tests live in `Ordering.Api.Tests`**, not in
  `Ordering.Application.Tests` where §12.1's row would put them: `ICurrentUser`
  is `HttpContextCurrentUser`, and a handler resolved in a bare scope has no
  principal to bind a subject from. `Ordering.Application.Tests` holds
  §12.5's saga suite instead, which needs no infrastructure at all.

## Architecture rules are tests

[§4.2](backend-architecture/04-solution-structure.md)'s dependency table is
enforced by `ArchitectureTests` in each service's Domain, Application and Api
suites, and a violation is a **build failure rather than a review comment**.
They read *emitted* assembly references, so a forbidden reference nothing uses
passes until code crosses it — §4.2 states that reach and what closing it would
cost. They need no container, so they are in the fast half:

```bash
dotnet test Platform.slnx --filter "FullyQualifiedName~ArchitectureTests"
```

If a change needs one of those gates relaxed, the gate is probably right and
the design is probably wrong.

## The scaffold's suite

`tools/new-service/` renders a new service from `src/Services/Catalog` at run
time, so **changing Catalog can break the scaffold** and its tests render this
repository. They run on stdlib Python with no SDK, which means they inspect
text and never build what they render — so a Catalog test using a helper the
scaffold removes renders into a service that does not compile with every test
green. A change touching `tests/Catalog.*` is not verified until a scaffolded
service has been built; `CLAUDE.md` carries the four commands and the cleanup.
