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
> drift.** `/check-links` checks `docs/backend-architecture/` and the root
> README's entry point into it, so a file anywhere else is outside its scope
> whatever links it. `/validate-blueprint` reaches this one only because it is
> named in that command's scope — which makes this file and `roadmap.md` the
> two exceptions among those siblings rather than one of a set. The one rule in
> `CLAUDE.md` covers the rest, and that is all that does.
>
> **The siblings are described rather than listed on purpose.** Three copies of
> this callout named three different sets the moment two new files arrived,
> which is what a list with no code to check it against does. The predicate
> holds however many there are.
>
> **A predicate is only better than a list if it is true.** These three
> callouts first said this file is in no index “like every document under
> `docs/` that is not the blueprint tree”, which is false twice over:
> `docs/runbooks/README.md` is an index of the runbooks beside it, and the
> root `README.md` links `docs/roadmap.md`. Scope is the durable fact and
> being unindexed never was — a false predicate is a stale list with an
> argument in front of it.

## The suites

Three runners, and `dotnet test` says nothing about any suite but its own.
**No count opens this sentence**, and its removal is the fix rather than a
recount: it said seven, then ten, and #61's secret scan made it eleven inside
the pull request correcting the sentence around it. What a reader can check is
whether this block matches the enumeration in `CLAUDE.md` and the workflows
that run them, which needs no numeral. **Workflows, plural, and not `ci.yml`
alone** — ADR-036's broker ACL runs in `broker-permissions.yml`, so a check
aimed at the one file misses a suite that is in this block and was, until
recently, missing from `CLAUDE.md`'s enumeration as well.

```bash
dotnet tool restore                # dotnet-ef, pinned in .config/
dotnet restore Platform.slnx
dotnet build Platform.slnx
dotnet test  Platform.slnx         # needs a running Docker daemon

(cd tools/new-service && py -3.12 -m unittest)  # no Docker, no SDK

bash deploy/helm/smoke.sh                       # needs helm 3, no Docker, no SDK

py -3.12 deploy/observability/check.py          # no helm, no Docker, no SDK
py -3.12 -m unittest discover -s deploy/compose/rabbitmq   # then check_permissions.py
py -3.12 deploy/compose/rabbitmq/check_permissions.py   # ADR-036's broker ACL
py -3.12 -m unittest discover -s deploy/keycloak   # then realm_check.py
py -3.12 deploy/keycloak/realm_check.py check --kind local   # §11's obligations

(cd .github/licence-gate && py -3.12 -m unittest)        # §4.4's register gate
(cd .github/secret-scan && py -3.12 -m unittest)         # §15.1's secret scan
py -3.12 .github/secret-scan/secret_scan.py              # from the repo root

py -3.12 -m unittest discover -s .github/pipeline-gate   # PR-25's quality gates
py -3.12 -m unittest discover -s .github/coverage        # the coverage merge
py -3.12 -m unittest discover -s deploy/canary           # §15.5's rollout
py -3.12 -m unittest discover -s .github/closure-gate    # what a PR closes
py -3.12 -m unittest discover -s .claude/scripts         # the review loop's helpers
```

**The licence gate and the secret scan are in that list because CI runs them
on the same terms**, and leaving the first out is what once made this count
seven. `ci.yml` tests each gate and then runs it — the pattern every gate here
follows — so a suite that ships with the repository, runs in CI, and is
invisible to `dotnet test` is one of these whatever directory it lives in. The
two share a job, because §15.1 draws "SCA + secret scan" as one node and both
are stdlib Python over text.

**Only the first is a §12 suite, and the rest are here anyway**, because
this file is written for someone with a checkout rather than for someone
deciding what to test. The scaffold's tests exercise a developer tool; the
chart gate renders `deploy/helm/` and asserts what comes out (§15.3); the
observability gate pairs §13.6's alerts with §13.9's runbooks both ways and
checks that every metric a loaded rule reads is one something publishes;
PR-25's three cover the pipeline's own inventories, the coverage merge, and
§15.5's rollout arithmetic; the closure gate compares what a pull request
says it closes against what GitHub will actually close; the secret scan reads
the working tree for credential shapes and holds every accepted one as a
fingerprinted line with a reason; ADR-036's broker ACL derives what each
service may touch from that service's own source and holds §14.1's
`definitions.json` to it; the realm gate asserts §11's token obligations —
§11.3's access-token lifetime, ADR-034's absent refresh token, §11.2's
password grant — against a Keycloak realm representation, and reads no realm
of its own here beyond §14.1's realm export, because the half that judges a
deployed realm runs from `deploy.yml`'s rollout job and, since ADR-043,
hourly from `realm.yml`'s own `deployed` job, where a credential exists; and
the last covers the review loop's own
helpers under `.claude/scripts/`. None
is in `Platform.slnx`, so a green solution says nothing about any of them,
which is exactly why a person needs to be told they exist.

**ADR-036's broker ACL was missing from that list until this change**, and
`CLAUDE.md`'s enumeration already carried it — which is what made the gap here
visible at all, since the paragraph above the block tells a reader to compare
the two. A prose list of what each suite covers is a second enumeration of
the block above it, so it goes stale on the block's clock and nothing
structural reads either.

**The review helpers' suite needs `bash`, `grep`, `git` and `jq`, and no
network** — the `gh` its
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
keeps it closed. Issues have added subjects since, and those are regression
cases of the first kind — each fails against behaviour that shipped:
**#56**, what a Copilot feed helper admits, what it reports about what it
dropped, and that no command can reach those feeds outside the fixed helpers;
**#33**, which paths the harness denies itself and that the worktree root is
not among them; **#52**, that the reviewer's transcript reaches no stream and
that the one bounded read of it cannot be widened; **#57**, that both
sweeps still state who may suppress a finding and that neither has drifted back
to the unconditional rule; **#140**, that the Grok ceiling is one declared
number both helpers enforce and that a row posted under the old ceiling is
still read as spent; **#60**, that the two commands stating editing boundaries
path-scope `Edit` away from every tracked tree while each keeps its own subject
editable; **#30** and **#23**, the argv guard — the `--output` write primitive
in every spelling including the quoted one, `ext::`, command substitution, and
a push allow-list that refuses anything but one remote, one refspec naming a
destination, and options from a fixed set; **#181**, the edit-target guard —
that a link inside an allowed tree is refused whatever it points at, that a
denied tree spelled as *itself* is admitted here rather than judged twice, and
that a checkout reached through a link is not refused wholesale, with its cases
in `test_edit_target_guard.py` beside the others and picked up by the same
`discover`; it is the one suite that needs a real link on disk, and runs every
case against **each** primitive the platform grants — a symbolic link, a
junction, or both — never a skip, which would report a pass for a property it
never tested. It is also the one module CI runs on **three** platforms:
`review-helpers` on Linux, and `edit-target-guard` as a matrix over Windows and
macOS — because the junction fallback, `..` after a link and the on-disk case
`realpath` answers with are Windows' own, and a case-insensitive mount is
macOS' default, and no Linux runner reaches any of them. That job runs the
module alone rather than the directory, because this module needs nothing but
Python and a filesystem where the suite beside it needs `bash`, `grep`, `git`,
`jq` and a `gh` stub; it runs verbose, because the module prints which link
primitives it exercised and a green run does not say — **measured on the
runners rather than assumed**: `windows-latest` reports `symlink, junction`,
so that account holds `SeCreateSymbolicLinkPrivilege` and only the
run-against-every-primitive rule reaches the junction fallback there, and
`macos-latest` reports `symlink`;
**#150**, that what suppresses a
sweep finding is decided by a helper rather than by a reader; **#75**
again, that the issue helper leaves `gh issue create` no free parameter and
that the conversion exclusion reaches its own child rather than only its
source — the second helper of the kind that never shipped wrong, whose title
did; and **#17**, that every credential-bearing `docker run` in the reviewer
joins the internal network and only the proxy reaches the bridge, which is
the whole of the egress confinement's width and the one part of it a text
gate can hold. Its depth is `test_egress_proxy.py` in the same directory,
which the same `discover` picks up: it runs the proxy on loopback and drives
a socket at it, so the allowed tunnel relays, a host off the list, an
allowed host on another port, a plain `GET` and an upstream that refuses
are each answered as the file says, and the log line never carries the
request as the reviewer wrote it.

**Two of those took their counterfactual somewhere other than the previous
commit, and that is worth knowing before trusting them.** #140's read-side
cases are green against `main` and have to be, since `main` reads the old
comment shape natively — their counterfactual is the *naive migration*, a
deliberately narrowed filter, and four of them fail against it. #60's is a tree
removed from the deny list. A case whose counterfactual is not the previous
commit needs saying so, or the next reader assumes none was taken.

**No count opens that list any more**, here or in `.github/workflows/ci.yml` or
`.claude/scripts/test_grok_helpers.py`, and those three enumerations are what a
reader compares instead. The numeral said four, then five, then six, and was
stale again inside the pull request that added two subjects — a figure restated
in three files goes stale in all three at once.

**`CLAUDE.md` was the third holder and no longer is**, which is why the trio
above names the suite's own docstring in its place: the extraction folded that
file's commands section into this one, and the issue-by-issue subjects came
with it. This sentence went on naming `CLAUDE.md` for a round after `ci.yml`
and the docstring had both been corrected — a three-site reconciliation that
landed on two, which is the prefix a multi-target edit leaves behind when the
follow-up is resumed from the report rather than from the list. The regression
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
does not: `deploy/helm/README.md`, since that gate reaches no cluster;
`deploy/observability/README.md`, since that one reaches no Prometheus and does
not validate rule syntax; and `deploy/keycloak/README.md`, since that one
reaches a live realm from `deploy.yml` and — since ADR-043 — hourly from
`realm.yml`'s own `deployed` job, and a file from CI, and the difference
between those two subjects is the whole of what it is for.

**Running a gate is the other half, and the block above carries some of those
runs but not all of them.** Each gate is tested and then run — the pattern
every gate here follows, across `ci.yml` and the path-filtered workflows
beside it — so a green suite says the gate works and not that this checkout
passes it. The block above already runs the observability gate, the broker ACL,
the realm gate, the secret scan and the chart gate; these are the gate
invocations it does **not** carry, and all four need no daemon, no SDK and no
network:

```bash
(cd .github/licence-gate && py -3.12 licence_gate.py)    # from its own directory
py -3.12 .github/pipeline-gate/pipeline_gate.py filters
py -3.12 .github/pipeline-gate/pipeline_gate.py images
py -3.12 deploy/canary/canary.py check
```

**The Compose smoke is the fifth and is fenced apart from them, because it is
the one gate here that needs a running daemon and the one whose run changes the
machine it runs on.** `compose.yml` drives three commands against §14.1's file,
and the third is not tidying: RabbitMQ seeds its default user only on an empty
database, so ADR-036's removal of `guest` is true after a `down -v` and false
after a plain `down`. The teardown is part of what the gate asserts, which is
why it is listed here rather than left to the reader.

```bash
docker compose -f deploy/compose/docker-compose.yml config -q
docker compose -f deploy/compose/docker-compose.yml up -d --wait --quiet-pull
docker compose -f deploy/compose/docker-compose.yml down -v
```

`config -q` runs first so that a YAML error costs no image download to find,
and `up --wait` is the assertion rather than the setup — it exits non-zero if a
healthcheck never passes, and a one-shot that another service gates on with
`service_completed_successfully` satisfies it by exiting 0, so the migrators'
exit codes are part of what this proves. `rabbitmq` is built rather than
pulled, so an image build rides on the `up`.

**`HELM=` is an override, not a gate run**, so it is documented here rather
than listed above: the chart gate is already invoked in the suite block, and
this only tells it where the executable is when `helm` is not on `PATH`.

```bash
HELM=/path/to/helm bash deploy/helm/smoke.sh
```

**`pipeline_gate.py stages` is the one that cannot be run on its own**: it
reads what the three test steps wrote, so it needs a `dotnet test` per stage
into `./TestResults/{architecture,unit,integration}` first. Those three
commands are under *Categories* below, and the gate invocation sits with them.

**The chart gate runs `helm dependency update` itself** before it can render
anything — `file://` dependencies resolve from disk, so there is no network
step and no chart repository.

`deploy/canary/README.md` is that tree's operational reference, on
`deploy/observability/README.md`'s terms: what the gate asserts, and — more
usefully — the things it does not, of which the load-bearing one is that
**nothing has established a replica ratio is a traffic ratio**. kube-proxy
spreads connections rather than requests, and no render-time check reaches
that.

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
> thirty-four tests of the three classes in the collection and
> `Category!=Integration` selects the other seventy-two — 106 in total, with no
> third state and nothing counted twice. Those figures read ten/81, then
> twenty/91, then twenty-three/94, then twenty-four/95, then twenty-six/97,
> then twenty-six/98, then twenty-seven/99, and
> every retake up to the last three was the suite growing while the callout did
> not —
> the drift the paragraph below is about, arriving in the paragraph above it.
> **How many retakes is not written down**, for the reason the figures keep
> demonstrating.
>
> **The second number in that series is the total, not the fast half**, which
> is worth stating because this very sentence got it wrong once: a retake was
> appended as `twenty-six/71`, pairing the integration count with the fast one
> and silently changing what the series measures halfway along. A series is a
> claim about a quantity, so an entry in different units is not a smaller
> error than a wrong figure — it is a wrong figure that reads as consistent.
>
> **This retake is the first that goes down, and the direction matters more
> than the number.** ADR-033 withdrew the token-denylist claim, so
> `RedisKeys.Denylist` and the case pinning its shape are gone and the fast
> half is seventy where it was seventy-one. A figure that has only ever grown
> trains the next reader to check whether it is *behind*; one that can move
> either way has to be re-measured rather than reasoned about, which is what
> the paragraph below asks for and what this line exists to stop anyone
> assuming past.
>
> **And it went back up on the next branch**, which is the same lesson from the
> other side: [ADR-037](backend-architecture/appendix-a-adrs.md#adr-037--the-idempotency-marker-is-a-row-in-the-commands-own-transaction)'s
> floor on `RetentionPolicy.IdempotencyWindow` brought two cases with it, so
> the fast half went to seventy-two and the total to 98. A figure that has
> moved in both directions inside two branches is one nobody should reason
> about at all.
>
> **And it moved again on the branch that retired that floor's allowance**,
> which is the third direction: one integration case joined
> `RedisIdempotencyStoreTests` for the claim's inherited expiry
> ([ADR-038](backend-architecture/appendix-a-adrs.md#adr-038--the-marker-and-its-claim-are-ordered-by-construction-not-a-margin)),
> so the fast half is unchanged at seventy-two and the total is 99. The floor's
> own cases were rewritten rather than added to, which is why only one of the
> two numbers moved.
>
> **And it moved once more, by more, on the branch that stopped the purge
> counting at all.** The port gained a fifth member — `UnheldAsync`, which
> §9.5's marker pass asks instead of comparing two clocks
> ([ADR-039](backend-architecture/appendix-a-adrs.md#adr-039--the-markers-purge-asks-the-claim-rather-than-out-counting-it))
> — and its seven cases all need the server they interrogate, so the fast half
> is unchanged at seventy-two for the third retake running and the total is
> 106. **Three consecutive retakes moving one number and not the other is the
> propagation working**, not a coincidence to note: joining the container
> collection is what carries the category, so a case that needs a container
> cannot land in the fast half by forgetting an attribute.
>
> **Those are the runner's numbers, and `--list-tests` answers a different
> question.** For this project the two now agree — discovery and execution both
> report 106, measured rather than assumed on each retake — and they have not
> always: the gap was 82 against 81 when this callout was written, and mixing a
> partition quoted from `--list-tests` with a total from `dotnet test` is how it
> first came to claim 72 and 82. The 1,119 is summed from `dotnet test`
> output, so quote what ran. **Agreement today is a measurement and not a
> guarantee** —
> which is why the rule outlives the discrepancy that produced it.
>
> Across the solution the split is **874 and 245 of 1,119**, and the fast half
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
77 of its 81 tests never needed a container and 4 need an identity provider, so
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
on this repository they are **18**, **856** and **245**, summing to the 1,119
the whole suite runs — which is the arithmetic the callout below asks for. The
architecture stage is the one that has not moved: it is 18 gates, measured
again on the branch that took the other two figures.

**The integration figure read 187 for two branches and the arithmetic never
closed**, which is `CLAUDE.md`'s own rule about restated numbers catching one:
18 + 649 + 187 is 854 where the suites summed to 855. What settled it was
**reconciling against the branch's own CI run rather than recomputing** —
`gh run view <id> --log`, summed over the per-project totals of each stage.
That is the cheap check whenever a count here and a count elsewhere disagree:
the run already happened, and it beats arithmetic that would otherwise have to
guess which side is wrong.

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
> counts above are what makes the difference visible — 874 and 245 summing to
> 1,119 — so whoever writes the staged pipeline should assert a floor on each
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
service has been built; `docs/repo-map.md` carries the four commands and the
cleanup.
