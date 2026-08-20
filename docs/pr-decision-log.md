# PR decision log

**What each landed PR from PR-08 onward decided, and why those decisions bind
what comes after.**

**PR-01 to PR-07 have no entries, and never did.** The practice of recording a
PR's findings began with PR-08's persistence layer; the seven before it landed
the foundation, the building blocks and Catalog's skeleton without one. What
PR-07 established — §4.2's architecture gates, observed red before they were
trusted — is stated in `CLAUDE.md`'s phase section rather than here, because it
is a live rule and not a historical decision.

This file is the long half of `CLAUDE.md`'s *Which phase are you in* section.
It was extracted from it when that section reached 1,132 lines — every PR from
PR-08 on appended its findings and nothing was ever consolidated, so a file
loaded into
every session's context was carrying a changelog nobody needed in order to act.

**The entries below are verbatim in their arguments**, because the arguments
are the whole value and a summary of an argument is how a rule gets "corrected"
back. Four kinds of edit were made on the way out, and naming them is cheaper
than a claim that does not survive a grep: each block gained a `## PR-NN`
heading; **self-references were rebased**, since a sentence saying "the rule at
the top of this file" pointed at a section that did not travel and would
otherwise now point at nothing; where a block stated a *live* fact that
`CLAUDE.md` also carries, it now points there instead of holding a second copy
that drifts; and one 136-column line was rewrapped. Not one argument was
shortened.

A few lines still run one to nine columns past 80, each ending in a backticked
identifier too long to break. They came across as they were, and the original
carried them the same way.

**That sentence used to carry a count, and the count was wrong.** It said five
where six matched its own description — the sixth arrived with a later entry,
as the next one will, because nothing recomputes it and the sentence is not
where anybody looks. The predicate is checkable and the number was not, so the
number is gone rather than corrected: `CLAUDE.md` makes the same argument about
its own line count one file over, and a figure this file states about itself
invalidates with the next PR that appends to it.

**It is guidance, not specification.** Where an entry disagrees with the
blueprint, the blueprint wins — the same rule `docs/superpowers/` carries — and
the disagreement is a bug report against one of them rather than a defect to
tidy away. `CLAUDE.md`'s own *one rule that matters* governs it: a claim
changed here is changed everywhere it appears.

**It sits outside `docs/backend-architecture/` deliberately**, beside
`docs/roadmap.md` and for the same reason. It is not a chapter, so it is in no
index, carries no nav footer, and is in the scope of neither
`/validate-blueprint` nor `/check-links` — both of which glob the blueprint
directory. Nothing structural will catch its drift; the *one rule* is what
carries it, exactly as it carries `CLAUDE.md` itself.

**Newest first.** A PR appends its block at the top of the log, under the same
heading form as its neighbours.

**It does not rewrite another PR's argument — but it does reconcile another
PR's live claims, and the two are not the same thing.** An entry records how a
PR was reasoned through, so re-arguing it later destroys the only thing the
entry is for. A *number* restated inside that argument is different: it is a
claim about the system, and the one rule above binds it exactly as it binds any
other. PR-10's block is the worked example — it states the compose smoke's
timeout and says in place that the figure "is restated here, which is what
makes it a claim to reconcile rather than a fact to read". PR-17, PR-18 and
PR-19 each raised it, in that block, and were right to. **A log that forbade
those edits would guarantee the staleness the one rule exists to prevent.**

---

## PR-23 — the charts, and a name the platform already depended on

PR-23 shipped [§15.3](backend-architecture/15-cicd-deployment.md)'s charts: a
library chart holding every template once, three deployables that are values
plus one-line includes and a fourth — the gateway — that adds one template of
its own, an umbrella,
[§7.4](backend-architecture/07-persistence.md)'s migration hook, and
`deploy/helm/smoke.sh` behind the second path-filtered workflow of §15.1. Seven
of its decisions bind what comes after.

- **Helm's `fullname` convention would have broken the platform's routing, and
  nothing in the chart could have shown it.** The idiom is
  `{{ .Release.Name }}-{{ .Chart.Name }}`,
  and §10.2's route file resolves `http://catalog-api:8080/`
  while §9.7's pricing hop resolves `http://catalog-api:8081` — both literals
  in source, both arguing *on the record* that the value does not vary because
  "the host is the Kubernetes Service name". A release-derived name makes that
  sentence false the moment the umbrella installs the same workload under its
  own release, and the failure is a 502 at run time rather than a template
  error. So `workload.name` is a required value, the Service takes it verbatim,
  and **the selector carries nothing release-scoped** — a Deployment's
  `.spec.selector` is immutable, so a release-derived one is rejected by the
  API server on exactly the standalone-to-umbrella migration an umbrella chart
  exists to perform. The gate that keeps this true reads the two source files
  and asserts every rendered Service is a name one of them dials; the CI filter
  therefore names two paths under `src/`, which is the one place a `deploy/**`
  workflow reaches outside its own tree.
- **A gate cannot fail on a file that is not there, and this is that lesson at
  its earliest point.** "The gateway renders no migration Job" passed against a
  gateway that declared `image.migrator: gateway-migrator` — because the
  gateway chart carries no migration template at all, so the values key the
  comment beside it credited was never consulted by anything. It is not a gate
  that stopped covering a surface; it is one whose subject never existed. The
  assertion is now about the **agreement** between the two halves — a chart has
  a migration template exactly when its values name a migrator image — which
  fails from either side. Of every deliberate defect run through that gate it
  is the **one** that failed to turn a green run red, and it was found by
  running them rather than by reading it again.
- **§15.4's two Redis rows were required against a solution where nothing
  reads them**, and the consequence is worse than the over-supply that table
  already warns about. Supplying credentials no code path sends merely
  provisions something to rotate; a `secretKeyRef` naming a Secret nobody
  created is a pod stuck in `CreateContainerConfigError` and a service that
  never starts. No host calls `AddRedisConnections`, so both rows are now
  conditional on the consumer existing. The rule that resolved it is §14.1's,
  applied one deployment target over — **a key joins when a host's code reads
  it** — and it is the same rule that keeps the charts' environment identical
  in shape to the Compose blocks'.
- **`terminationGracePeriodSeconds` had a rule and no number, and the number is
  not free.** §15.3 requires the grace period to exceed the longest in-flight
  operation. The longest one is not per-service: `HostOptions.ShutdownTimeout`
  bounds the whole drain and defaults to **30 seconds** — measured on the
  pinned SDK rather than read off a documentation page, with nothing in this
  solution overriding it — and `ServiceOptions.OperationTimeout` (20 s) sits
  inside that window. Kubernetes' own default is also 30, which is the trap:
  **30 is not a margin over 30.** A pod at the default is `SIGKILL`ed at the
  instant the host would have finished draining, and nothing logs it.
- **A measurement taken through a tool that normalises reports the absence of
  the defect it was taken to find.** Go's template engine copies bytes through,
  so a CRLF template renders `path: /health/live\r` and the smoke's
  `$`-anchored greps match nothing on a Linux runner. Run here, the same
  mutation was **green** — MSYS `grep` treats CRLF as the line terminator and
  strips the CR before matching — so the first measurement said the hazard did
  not exist. Only reading the rendered bytes settled it. `.gitattributes` now
  pins `deploy/helm/**` to LF, on the same argument its `*.cs` paragraph
  already makes, and the paragraph records how the answer was nearly missed.
- **A rollout checksum has to cover every ConfigMap the pod mounts, and the
  narrow one is how that was learned.** Changing a ConfigMap changes nothing a
  running pod reads, so the pod template carries a hash that moves with the
  values — otherwise a config-only deploy (§15.1) reports success and rolls
  nothing. Hashing the *rendered ConfigMap* looked right and was not: the
  gateway renders a second one from its own template, so `cors.origins` and
  `ingress.trustedNetworks` — the two keys most likely to be edited without a
  rebuild — rewrote a mounted object while the annotation stayed
  byte-identical. It now hashes the whole of `.Values`, which over-triggers on
  keys the container never sees and is the safe direction. **Found by writing
  the assertion, not by reading the template**, which is the argument for
  writing the assertion.
- **`Chart.lock` and `charts/` are generated, not source.** `file://`
  dependencies resolve from disk, so the lock pins nothing a remote repository
  could move and the tarball is a binary copy of a directory two levels up.
  Committed, the lock would be a second copy of a version `Chart.yaml` already
  states — the drift the one rule exists to close — so both are ignored and
  `helm dependency update` is the whole of the setup, run by `smoke.sh` and by
  the CI job before anything else.

---

## PR-22 — the rest of §4.2, and a category that cannot drift

PR-22 put the last of [§4.2](backend-architecture/04-solution-structure.md)'s
dependency table behind gates — the cross-service clause three of its five
rows carry had no test, and the Migrator row had none of any kind — split the
suite on
`Category=Integration`, added `docs/testing.md` and started reporting
domain-layer coverage. Six of its decisions bind what comes after.

- **A gate the scaffold copies cannot be keyed on a name the scaffold
  rewrites.** The obvious instrument for "no service references another
  service's projects" is a list of §4.1's six service names, and it is wrong
  here for a mechanical reason: `new_service.py` applies its patches and *then*
  renames every casing of the template's name, so a list naming `Catalog`
  reaches the new service with `Catalog` **replaced** rather than joined —
  dropping the one service a scaffolded service is most likely to reference by
  accident. No spelling of the patch survives that, because the rename is what
  the patch output is fed through. So the gate asks a measured question
  instead: **every package this platform pins is strong-named and none of this
  repository's own projects is**, checked across all ten service assemblies.
  `Dapper` is the single unsigned package in the graph and is named as the
  residual. A second one would be misread as first-party and fail the gate —
  which is the direction it has to fail in, because the failure names an
  assembly nobody expected and the alternative predicate would have opened a
  hole silently. The rule then covers Inventory, Payments, Shipping and
  Notifications before any of them exists, which no list would have.
- **§4.2's table has two kinds of row, so it gets two kinds of gate.** A row
  saying what a project *may* reference is an allow-list and gets an allow-list
  over `GetReferencedAssemblies`; a row saying it may reference any package
  cannot have one and gets a named deny. Picking by the row rather than by
  taste is what keeps a gate from contradicting the sentence it enforces —
  a full allow-list on `*.Infrastructure` would have been the strongest
  instrument available and would have flatly denied the "any package" the table
  grants it. **The migrator is the row that most wanted this**, because its
  must-not is a sentence rather than a list — *anything it does not need to
  apply a migration* — which a deny-list cannot express at all.
- **A pre-granted exemption for a class that does not exist is a hole, not a
  provision.** §4.2's composition-root rule read "only `Program.cs` **and
  host-level `*ServiceCollectionExtensions`**", and the gate had never
  implemented the second limb; no host has such a class. Both directions were
  available and the prose was narrowed to the code rather than the gate widened
  to the prose. The exemption is the whole of that gate's trust, and its
  companion test — *the composition root is the only thing exempted* — is only
  meaningful while the exempted set is small enough to hold in mind. A host
  that genuinely wants a registration extension may have one; what it does not
  get is a licence written before it existed.
- **A category is the opposite of a skip, and this repository had refused them
  together.** `CLAUDE.md` said the container tests were "neither skipped nor
  categorised", on one argument that only applies to the first: a skip on a
  missing daemon fails open, so CI goes green on a runner whose Docker broke. A
  category decides which *stage* runs a test and never whether it may be
  absent. **Where it goes is what makes it undriftable**: the trait is declared
  on the `[CollectionDefinition]`, so joining the container collection *is*
  carrying the category — there is no per-class attribute to forget and
  therefore no reflection gate owed to check that nobody did, which is the
  first time this repo has closed one of these by construction rather than by
  adding a second test. xUnit v3's propagation was measured before the design
  was trusted: 10 and 71 of 81 on one assembly, 614 and 164 of 778 across the
  solution, with no third state.
- **"No container starts" is a claim about a run, so it was measured — and the
  first attempt to measure it proved nothing.** Pointing `DOCKER_HOST` at a
  dead endpoint and watching the fast half pass looked conclusive and was not:
  Testcontainers ignored the variable on this host, and the *integration* half
  passed against the real daemon under the same override. What settled it was
  `docker events --filter event=create` over the window, reporting nothing
  against a probe that captured a control container started beside it. **A
  green run under a broken override reads exactly like a green run under a
  working one**, which is the same shape as this repository's vacuous-gate
  failures one layer out.

- **Measuring a layer changes it, and the change was invisible on the machine
  that wrote the measurement.** `coverage.runsettings` instruments the Domain
  assemblies and nothing else — which are exactly the assemblies §4.2's Domain
  gates read `GetReferencedAssemblies` on. On the Linux runner an instrumented
  Domain assembly reports a `netstandard` reference no source line can produce,
  and **both** Domain gates went red on the first CI run that collected
  coverage. It does not reproduce on Windows: the same collector leaves
  `Ordering.Domain.dll` byte-identical, checked by hashing it either side of a
  run, so every local run — Debug, Release, with the collector and without —
  was green on a defect CI found immediately. **A green local suite is a claim
  about one machine**, and this is the sharpest instance of it the repository
  has: not a stale artefact, but a platform where the instrumentation mode
  differs.

  The one-line fix was to admit `netstandard` to the allow-list, and it is the
  wrong one — an architecture rule relaxed everywhere, for ever, and in every
  service the scaffold renders, to accommodate a test tool. CI runs the gates
  first and uninstrumented instead, and collects coverage over the complement;
  the two filters are exhaustive and disjoint, so the counts still sum to the
  suite. **If a change needs one of those gates relaxed, the gate is probably
  right** — this is that rule meeting a case where relaxing it would have been
  easier and nobody would have noticed.

Two smaller things are worth carrying. **Coverage is reported and never
gated** — §12.9 calls it a diagnostic, and a diagnostic wired to a build
failure stops being read and starts being satisfied; the threshold is PR-25's.
The filter is a *pattern*, `.*\.Domain\.dll$`, so every later service's Domain
joins it the day it exists rather than waiting for someone to edit a list. And
the collector is the one `Microsoft.NET.Test.Sdk` already carries, so the
figure cost no package and Appendix B no entry — measured at **83.4%** across
`Catalog.Domain`, `Ordering.Domain` and `Common.Domain` on the run that landed
this PR. That is the complement run's figure and it is the right one: the
architecture gates reach Domain types by reflection and nothing else, so
counting them inflated both halves of the ratio for no behaviour tested.

**One thing was found and deliberately not fixed.** `Catalog.Infrastructure`
carries a `ProjectReference` to `Catalog.Application` that its own code never
names — `GetReferencedAssemblies` does not list it, and no file in the project
has a `using` for it. Ordering's equivalent *is* used, so the two services
differ. §4.2 permits the reference either way, and removing it would break the
moment Infrastructure names an Application type, which §4.2 anticipates; it is
recorded here rather than tidied because the reverse case — a *used*
dependency that no csproj declares — is what `Common.Domain` in the Application
gate is, and the two look alike from a distance and are not.

**That paragraph is also the hole in the five gates, and the external review
found it there.** Copilot's round raised one finding at six sites: every gate
in §4.2's table reads `GetReferencedAssemblies`, which reports the emitted
`AssemblyRef` table, so a forbidden `ProjectReference` or `PackageReference`
that no compiled code *names* is invisible to all of them — the reviewer's
evidence being the unused edge recorded directly above. The mechanism is
correct and the consequence is real: a project may declare a forbidden
reference and the gate that names that row stays green.

**The instrument was not changed, and the reasoning is the part worth
keeping.** Reading the declared graph is the fix — the restore assets, or a
reference list MSBuild emits into an assembly attribute — and it is a
repo-wide build change whose own failure mode is the one this repository
repeats most: a target that quietly stops emitting leaves every gate passing
vacuously, so it owes a companion test whose subject is what the gate is
looking at. Landing that here would have put a new build-system dependency
into `Directory.Build.props` at the least-reviewed moment in the change, with
the Grok budget spent and one Copilot round behind it. **The limit is also not
this PR's**: the Domain gate has read the same table since it was written, so
the finding describes the gate family rather than the rows PR-22 added.

**The second round found a hole the first one's fix had just papered over, and
this one was closed rather than documented.** The cross-service gate subtracts
every assembly under this service's own prefix, so an `Api → Migrator`
reference passes it *and* the composition-root gate — while §4.2 names the
migrator in no row's "may reference" column, because it is a leaf job host.
`Nothing_in_this_service_references_the_migrator` is the third gate, over the
other four assemblies, and it was **observed red** against a deliberate
`Catalog.Api → Catalog.Migrator` reference that a line of `Program.cs` actually
used — the qualifier being the whole lesson of the round before: an unused
reference is invisible to this instrument, so a probe that only declares the
edge proves nothing.

**Two gates rather than one wider predicate**, because they ask different
questions — *whose is it* and *which layer is it* — and a single `.Where` doing
both reads as neither. The migrator is skipped as a subject rather than
exempted inside the predicate: an assembly does not reference itself, so
including it would pass vacuously, which is this repository's most-repeated
failure wearing its usual disguise.

**What did change is the claim.** The reach is now stated in §4.2 beside the
two-shape table, in `docs/testing.md`, and in all four test files a reader
meets before trusting a green run — the escape needs a reference that is both
forbidden and entirely unused, and it closes the moment anybody relies on it,
which makes these gates late rather than absent. **The declared-graph
instrument is owed**, and is the first thing to reach for the next time §4.2's
enforcement is opened.

## PR-21 — the saga, and the four things §9.6 did not say

PR-21 landed §9.6's `OrderFulfilmentSaga` with its four compensation paths and
four timeouts, the four command handlers those timeouts send to, §9.4's
`ordering-commands` endpoint and §9.3's allow-list — empty since PR-18, and the
reason the saga had nothing to start on. Five of its decisions bind what comes
after.

- **No chapter had ever named a message scheduler, and §9.6's four `Schedule`
  declarations do not work without one.** [ADR-021](backend-architecture/appendix-a-adrs.md#adr-021--saga-timeouts-are-scheduled-by-the-broker)
  settles it on MassTransit's delayed message scheduler, which on RabbitMQ is
  the delayed message exchange **plugin** — so §14.1's broker is now the one
  infrastructure service that is *built* rather than pulled. Quartz over an ADO
  job store was the serious alternative and is named in the ADR as the
  successor when the plugin's Mnesia store stops being adequate; what decided
  against it here was cost rather than correctness — three packages, ~200 lines
  of vendor DDL this repository would own, a `dbo` prefix cutting across §7.2's
  per-service schema, and a second set of hand-declared receive endpoints
  because this platform deliberately does not call `ConfigureEndpoints`.
  **The deciding argument was the test**: the in-memory transport implements
  the delay itself, so §12.5's harness runs the same two registration lines
  production does, where an in-memory Quartz would be a different mechanism
  wearing the same test.
- **A missing registration that fails at the first message, not at startup, is
  the shape this repository keeps meeting.** Nothing resolves a scheduler while
  the host builds, so both lines absent leaves a service that connects,
  declares its endpoints and reports ready — and faults its first `OrderPlaced`
  onto the error queue. Measured by deleting them: **11 of the saga suite's 13
  tests fail, every one as a timeout**, each reporting the command the saga did
  not send. Not one names the cause. The two survivors are the structural pair
  that construct the state machine without starting a bus, which is worse than
  none — they leave a deleted registration looking half-covered. That is
  §12.5's own trap arriving from a registration instead of from a loaded
  runner, and it is why the lines are stated in the sample rather than
  inherited.
- **§5.4's `Order.ConfirmStock` had no caller, and no way to acquire one.**
  The saga sends four commands; §3.2's Accepts column lists exactly those four;
  none of them advances the order out of `AwaitingStock`. So `ConfirmOrder`
  arrived at an aggregate whose `ConfirmPayment` requires `AwaitingPayment` and
  refused every confirmation the platform could produce — a happy path that
  could not complete, invisible until something drove it end to end. The fix is
  a **consumer, not a contract**: §3.2 already lists `StockReserved` in
  Ordering's Consumes column, so Ordering binds it twice — the saga to decide
  what to ask next, and an `IIntegrationEventHandler` to record it on the
  aggregate. A fifth wire command was the tempting alternative and would have
  changed three chapters to add a way for a peer to drive this service.
  - **That handler dispatches rather than mutating**, and the reason is §7.5's:
    work done inside an integration-event handler commits through the inbox
    filter's `SaveChangesAsync` and stages **nothing**, so the domain event is
    dropped in silence. Silent today only because no projection subscribes to
    `OrderStockConfirmedDomainEvent` yet — §6.6's `OrderSummaries` is not
    built — which is exactly the kind of debt that is free until the PR that
    pays it cannot find it.
  - **It needs a fourth receive endpoint**, `ordering-stock-events`, because
    the saga's endpoint carried no inbox filter by documented exemption and a
    plain consumer sharing it would have inherited that exemption without
    writing anything down. §9.8's "Ordering has three" became four. **The
    exemption is gone — see the entry below — and the separation survives on a
    different reason**: the saga's retry policy is written for inapplicable
    transitions rather than for the domain rejections `Order.ConfirmStock`
    produces.
  - **The two deliveries are unordered, so `ConfirmOrder` can arrive first**,
    and the handler answers `ErrorType.Unavailable` rather than a rule failure.
    §9.8 already draws that line — retry is for faults time might fix — and a
    `Rule` error would ack a paid order's confirmation for good. The window is
    a local write against a payment authorisation and is therefore small; that
    it is small is not why it is handled.
- **§9.6's escalation insert was a read-then-write with no range lock.** The
  printed `IF NOT EXISTS … INSERT` and the conditional `INSERT … WHERE NOT
  EXISTS` that replaced it both read and then write, so both race; the
  difference is `WITH (UPDLOCK, HOLDLOCK)`, which makes the second delivery
  wait and then see the row rather than violate the primary key. This is PR-20's
  `MERGE`/`HOLDLOCK` finding one table over, and the third time this repository
  has fixed a duplicate guard that had the defect it was written to prevent.
  The same block also stopped writing `SYSDATETIMEOFFSET()`: `RetentionPurgeService`
  already computes its cutoff from `TimeProvider`, and a row on the server's
  wall clock is one no substituted clock can reason about.
- **`ShippingAddressV1` silently dropped an address line.** The record carried
  `Line1`, `City`, `PostCode` and `Country`, the domain's `Address` carries
  `Line2`, and nothing noticed because nothing had ever populated the contract —
  PR-21's mapper is its first producer. Added here rather than deferred, on the
  rule this repository already states about contracts: **a contract with no
  consumers is the only cheap moment a contract ever has**, and the same change
  one release later is a §9.2 version bump.

- **§9.8's saga inbox exemption was wrong in both halves, and it is gone.** The
  chapter said a state machine needs no `InboxFilter` because its state is its
  idempotency check, and that an inbox row would suppress legitimate redelivery
  after a mid-transition crash. The first is an argument about **non-initial**
  events: `OrderPlaced` is handled in `Initially` and
  `SetCompletedWhenFinalized()` deletes the row, so MassTransit creates a new
  saga whenever none exists — and §9.4 guarantees at-least-once, so a duplicate
  arriving after the workflow finished reserves stock and authorises payment
  **a second time**. The second describes something the filter does not do:
  `InboxFilter` records after the inner pipe returns, so a mid-transition crash
  leaves no row and the redelivery does the work again. It was protecting that
  delivery from a mechanism that was never a threat to it.
  - Copilot found it, on the third round, in a suppressed-adjacent inline
    comment. It is the only **correctness** defect either review loop found in
    this PR's own code; everything else was a claim, a count or a missing test.
  - Reproduced first: a real-broker test that starts the saga, finalises it,
    republishes the same `OrderPlaced`, and asserts no instance returns. Red
    before the filter, green after.
  - The endpoint separation §9.6 argues for `ordering-stock-events` survives on
    a different reason — retry policy, not the inbox — and both chapters now
    say so.

**Five things are owed and are named rather than built.** Each is a §9.6, §5.4
or §9.8 decision that PR-21 made *reachable* rather than one it introduced, and
naming them is the alternative to a silent gap.

- **A stock timeout strands the reservation.** §9.6's `StockTimeout` branch
  cancels the order and finalises **without releasing stock**, so a reservation
  arriving afterwards has no saga left to compensate it —
  `ConfirmStockHandler` rejects it and the stock stays held. **The rejection is
  quieter than this entry first claimed**: `command.domain_rejected` is
  `CommandConsumer`'s counter, and this command is dispatched in process by
  `StockReservedHandler`, so the only record is `LoggingBehavior`'s line.
  Copilot caught the claim; the handler's own comment had it right and this did
  not. It is the second
  stranded-reservation path in §9.6 and only the other one escalates
  (`ReviewReasons.StockNotReleased`). Closing it means a compensating
  `ReleaseStock` on the timeout branch or a second escalation reason.
- **A customer cancelling mid-workflow is invisible to the saga.** §3.2 does
  not give Ordering a subscription to its own `OrderCancelled`, and §9.6's
  machine has no cancellation branch — so a cancellation racing `StockReserved`
  leaves the saga reserving and authorising, `ConfirmOrder` is refused by the
  aggregate, and three days later a false `not_despatched` review is raised.
  Copilot found it. **The complete fix is a chapter decision, not an
  implementation gap**: cancelling a *`Confirmed`* order needs a refund, and
  §3.2's Accepts column for Payments is `AuthorisePayment` alone — there is no
  refund contract to send. A partial fix covering `AwaitingStock` and
  `AwaitingPayment` is possible and was rejected here as a state-machine change
  §9.6 owns.
- **The payment reference is accepted and goes nowhere.** `ConfirmOrder`
  carries it, `Order.ConfirmPayment` puts it on `OrderConfirmedDomainEvent` and
  stores no column, and `V1.OrderConfirmed` has no field for it — so it reaches
  a Local outbox row only once a projection handles that event, and §6.6's
  `OrderSummaries` is not built. Found by PR-21's own endpoint test asserting a
  column that does not exist. `PaymentReference`'s own doc calls it "the one
  thing that lets a support question about an order reach the provider's own
  records", which is not true of anything today.
- **`Unschedule` cancels nothing on ADR-021's scheduler**, so every order keeps
  its timeouts until they fire. Recorded in the ADR rather than here, because
  it is a property of the decision rather than of the saga — but it is the
  fourth thing this PR knows and does not fix.
- **The saga endpoint buffers its sends in memory.** §9.8 prints
  `UseInMemoryOutbox` there, and the saga repository commits the instance
  *inside* the consumer — so a crash between that commit and the flush leaves
  the saga advanced (or deleted, after `Finalize`) with a command or a schedule
  never sent, and the redelivery finds a state where the transition no longer
  applies. Copilot found it. **§9.4's own callout already states the
  premise** — "the in-memory outbox defers, it does not persist… a consumer
  whose sends must survive its own commit wants §9.4's transactional outbox" —
  so this is the chapter disagreeing with itself rather than a new discovery.
  Closing it means running MassTransit's transactional outbox alongside this
  platform's hand-rolled §9.4 one, which is a §9 decision about owning two
  outboxes and not something a saga PR settles.

### What nine rounds of review moved, and the shape of the last one

**Every finding that changed behaviour arrived from a review, and the last one
to do so arrived on round nine.** That is worth recording as a fact about the
process rather than as praise for the reviewer: the suite was green and the
chapters reconciled after round three, and rounds four through nine still
found a replayed `OrderPlaced` restarting a finished saga, a healthcheck that
passed on a broker with no plugin, and three handler branches with no test
between them and a permanently unconfirmed paid order.

**Round nine's shape is the one to carry.** All eight of its findings were
*suppressed* — none surfaced as an inline comment — and five of the eight were
one claim: **a test that cannot fail for the reason it names.**

- `ConfirmOrderHandler`'s `StockNotConfirmed` is `Unavailable` so
  `CommandConsumer` retries it; a `Rule` error there acks a paid order's
  confirmation for good. Nothing tested it. The same for
  `MarkOrderShipped`'s `NotConfirmed` versus `NotShippable` and
  `ConfirmStock`'s `NotAwaitingStock` — three branches whose whole content is
  *which* `ErrorType` they carry, reachable only through a handler no endpoint
  test drives off the happy path. `SagaCommandHandlerTests` is the answer, and
  it is six tests for six branches.
- The duplicate-suppression test asserted a status that **cannot move either
  way**: `StockReservedHandler` drops the `Result` deliberately, so a
  duplicate reaching the aggregate is refused and leaves exactly the state a
  suppressed one does. The inbox row count was carrying the test the whole
  time and the status assertion was decoration reading as proof.
- `ConcurrencyMode.Pessimistic` was argued in a comment about two events
  arriving together, and every test in the suite delivered one at a time.
  **The test written for it does not pin the mode, and that is a measurement
  rather than a caveat**: with the registration flipped to `Optimistic` it
  passes in 915 ms. Each transition is a few milliseconds, so two messages
  published together are drained back to back and no concurrency conflict
  arises — publishing concurrently does not make a saga *consume*
  concurrently. Forcing a real overlap needs a transition slow enough to hold
  the lock while the second event arrives, which means production code written
  to be slow for a test. So the mode stays **registered, reasoned and
  uncovered**, and the test claims only what it demonstrates: both events are
  consumed without faulting and leave one instance or none. The name
  `..._are_serialised` was drafted and withdrawn — a name green against both
  settings is this round's own finding committed a second time.

**The generalisation is this repository's oldest one arriving by a new
route.** *A gate that silently stops covering the newest surface* is usually
about an architecture test's selector; here it is about an assertion that was
never watching the thing beside it. **Ask what the test does when the code is
wrong, not what it does when the code is right** — five of these were green
against both.

**Round ten found the same shape once more, and one thing genuinely latent.**
Three comments and a §9.6 paragraph said the command pipeline *stages*
`OrderStockConfirmedDomainEvent`. It does not: `DomainEventDispatcher` writes a
Local row only for an event with a registered projection handler, Ordering
registers none, and the event is on no Broker allow-list either — so it is
collected and cleared with **no row of either lane**. The argument the comments
were making survives, because it is about where the handler must live for the
row to appear once §6.6's `OrderSummaries` exists; what was wrong is the tense.
`ConfirmStockCommand` even carried the caveat, attached to the wrong clause: it
explained why the *bug* would be silent while the sentence above still asserted
the staging as a present fact.

**The delayed-message leak is real, unreachable today, and guarded by a
coincidence.** `Unschedule` is a no-op on ADR-021's scheduler, so every saga
test leaves its timeouts armed in the collection-wide broker; `ResetAsync`
truncates SQL and cannot touch them. If one landed mid-run it would cross
`InboxFilter` and write a row — and `Ordering.Api.Tests`' `InboxFilterTests`
asserts `ShouldBeEmpty()` over the whole table, in that same collection. What
stops it is only that the shortest schedule is **five minutes** and the
collection runs in **1 m 18 s**; a runner four times slower turns it into a
flake in a test that has nothing to do with sagas. Not fixed here: the fix is a
broker per saga class, which buys a container set on every run (§12.4's stated
price) against a hazard needing a fourfold slowdown to reach. **Recorded so the
next person to see `InboxFilterTests` fail for no reason finds this paragraph
rather than the timing.**

**Round twelve found a defect round nine introduced, which is the loop working
as intended rather than a sign it should have stopped.** `StockReserved` has
two consumers in this service by design — the saga correlates on it,
`StockReservedHandler` records it on the aggregate — so the publish helper
added for the concurrency test registered a teardown drain for the saga's
inbox row alone. The saga could finish, the teardown pass, and the next
`ResetAsync` truncate the schema underneath a `StockReservedHandler` still
committing: **exactly the flake this class's teardown exists to close, one
endpoint over.** Checking Copilot's cross-reference — it said the sibling suite
already did this — turned up a second instance in the same round's work: the
sentinel publish was marked `drain: false` when only the *duplicate* beside it
earns that, a suppressed message writing no row where a fresh id writes two.

**Three fixed sleeps became sentinel waits, and the honest limit is recorded
in the tests themselves.** A `Task.Delay` before a negative assertion is a claim
about the runner, not about the code; publishing a fresh message afterwards
and waiting for *its* effect scales with the machine. It is a bound and not a
proof — neither endpoint sets `ConcurrentMessageLimit`, so the sentinel may
overtake — and making it a proof would mean changing the production topology
to suit a test, which was considered and refused.

---

## PR-20 — the first projection and the first receive endpoint

PR-20 landed the first projection and the first receive endpoint — §6.6's
`ProductPriceProjection`, §9.8's `ordering-catalog-events` — and six of its
decisions bind what comes after:

- **`ConfigureEndpoints(context)` is gone from both services, and it is a
  fail-open rather than a leftover.** PR-13 left the call in Catalog and
  Ordering with a comment calling it "the line every later consumer rides in
  on"; what it actually does for a registered consumer whose explicit binding
  is missing is manufacture a queue named after the consumer type — carrying
  **neither** the inbox filter **nor** the retry policy, because both are
  per-endpoint configuration an invented endpoint never receives. §9.8 permits
  an endpoint without the inbox exactly once, for the saga, and requires the
  opt-out to be written down where it is taken; a queue MassTransit invents
  takes it and writes nothing. Measured both ways by deleting one
  `ConfigureConsumer` line: with the call present the event was still projected
  and **no inbox row was written**, and one of three tests noticed; with it gone
  all three go red. The cost is stated rather than dodged — a consumer now
  needs a line in two places and nothing at startup complains if it gets one.
- **§9.8's printed `e.UseInMemoryOutbox()` does not compile at this pin.** The
  parameterless overload carries `CS0618`, which ADR-019 makes an error, so
  three sites in §9 had been unbuildable since they were written. This is
  PR-19's `AddStandardResilienceHandler` finding and PR-17's `KnownNetworks`
  finding for the third time: **a sample nobody has compiled is a sample that
  does not compile**, and the only way to find out is to build it.
- **A withdrawal has to survive having no row to write to, and §6.6's printed
  `UPDATE` did not.** `ProductDiscontinued` carries no currency (§9.1) and
  `ordering.ProductPrices` is keyed by one, so the discontinue statement
  reached only the rows that already existed. §9.4 guarantees no ordering, so
  a withdrawal claimed ahead of a still-retrying publish matched nothing and
  the publish then took the upsert's `NOT MATCHED` branch — **the one branch
  no `UpdatedAt` comparison can cover**, because there is no target row to
  compare against — putting a discontinued product back on sale. A stale price
  for a currency the withdrawal never saw does it with no reordering at all.
  Copilot found it; both cases were reproduced as failing tests before the fix
  was written.

  **The answer is the one §6.6 already gives one projection up.**
  `OrderSummaries` uses a `MERGE` rather than an `UPDATE` for its status events
  precisely so a `Cancelled` claimed before its `OrderPlaced` does not "match
  no row, change nothing, and be marked processed". A status event can carry
  its own row into existence because it knows the key; a withdrawal cannot, so
  it writes a product-level watermark — `ordering.ProductWithdrawals` — and
  the upsert derives `IsAvailable` from it on exactly the branch that has
  nothing else to consult. A **watermark**, not a flag, for the reason
  `UpdatedAt` is a comparison: a later republish re-lists the product, in
  currencies that have rows and in currencies that do not.
- **`WITH (HOLDLOCK)` is a reasoned claim, not an observed one, and the test
  says so in its own remarks.** A bare `MERGE` takes no range lock over a key it
  failed to find, so two concurrent deliveries can both insert and the loser
  violates the primary key — which the endpoint's retry would absorb, so the
  defect reads as warnings rather than as a failure. Deleting the hint left the
  suite green at eight-way and again at sixty-four-way concurrency, three runs
  each. So the hint stays and §6.6 gained it, and the test carries the class it
  is in — PR-17's rate-limiter ordering row, reasoned and unobserved —
  rather than looking like the guard it is not.
- **The currency is normalised on the write side as well as the read side, and
  neither call is redundant.** Nothing between Catalog's `Money` and the
  `MERGE` normalises anything — `Currency` crosses the wire as a `string` like
  any other — so an unnormalised contract writes a row `ProjectedPriceReader`
  cannot find *and* a second primary-key row beside the one it can, under a
  case-sensitive collation.

  **What the reader's comment said before this PR is the lesson, and it took
  two review rounds to finish.** It justified its own `ToUpperInvariant` by
  asserting that the column "is written through `Money.Of`" — a claim about a
  file that did not exist yet, and one that stayed false after it did, because
  the value arrives over a wire and not through the domain. **A comment
  describing what some other file does is a claim about that file.** Round 1
  fixed the reader and §6.4's sample; three sites that described the *reader*
  went on quoting the retired rationale for two more rounds, which is the same
  defect one indirection out — a comment describing what a comment says.
- **§13.7's read-model row says *own events* now, and broker-fed staleness is
  a named gap rather than a row.** `projection.lag` is recorded by
  `ProjectionInvoker` off an outbox row, so a read model fed by another
  service's contract never touches it — and `ProductPrices` is the platform's
  first. The first fix pointed at the **event end-to-end** row instead, which
  Copilot refuted: `IntegrationEventConsumer<T>` records
  `messaging.delivery.lag` at the *top* of `Consume`, before it resolves a
  handler, so the measurement stops where the projection starts and excludes
  the SQL round trip, §9.8's retries and a terminal failure. That row can hold
  its two-second target while the table is stale or was never written.

  **A near-miss row is worse than an absence, and §13.7 already said so two
  paragraphs down** — "an SLO that cannot be evaluated is not a weak SLO, it
  is a claim that the service is meeting a bar nobody is checking". Closing it
  needs an instrument that fires after a broker-lane handler commits, which is
  a §13.3 change with a dashboard behind it; until then the gap is written
  down, which is the standing the two cut rows already have.

**`OrderSummaries` is deliberately not in this PR**, and the reason is worth
carrying: §6.6 has two projections, and only one of them is what this PR's
title names. The other is fed mostly by Ordering's own domain events on the
local lane and needs §13.3's `OrderMetrics` and §6.6's escalated history query
with it. Appendix C names no PR for it; whoever builds the history screen
builds it.

---

## PR-19 — the BFF, and Catalog as a gRPC server

PR-19 landed the BFF — §9.7's one synchronous hop, §11.5's client credentials,
the `web-bff` route's service — and ten of its decisions bind what comes after:

- **A cleartext Kestrel endpoint cannot serve HTTP/1.1 and h2c at once, and
  §9.7's printed `http://catalog-api:8080` could not work.** Measured before a
  line was written: at the default `Http1AndHttp2`, a client asking for HTTP/2
  *exactly* — which is what `Grpc.Net.Client` does — is answered
  `HTTP_1_1_REQUIRED` and the connection is closed. ALPN is what negotiates the
  upgrade and there is no TLS on this hop to carry it (§10.1). An `Http2`-only
  endpoint is the fix and it cuts the other way too: it answers an HTTP/1.1
  request with a 400. So Catalog declares **two** endpoints and §9.7 was
  amended to 8081.

  **The second half is the one that reaches other files.** `Kestrel:Endpoints`
  in `appsettings.json` **overrides** the container image's own port
  configuration — measured against both spellings, `ASPNETCORE_HTTP_PORTS` and
  `ASPNETCORE_URLS`, and neither produces a warning — and declaring *one*
  endpoint suppresses them just as completely as declaring two. So 8080 has to
  be restated in that file or it stops existing, and a host-run Catalog now
  binds 8080, **which is Keycloak's published port**. The compose README grew
  two `Kestrel__Endpoints__…__Url` exports and the reason for them, because the
  same configuration key from a higher provider is the only thing that can move
  a port this file has claimed.
- **§9.7's fluent chain does not compile, and only compiling it says so.**
  `AddStandardResilienceHandler` returns an `IHttpStandardResiliencePipelineBuilder`
  — a different type, scoped to the pipeline it just registered — so the printed
  `.AddStandardResilienceHandler(…).AddHttpMessageHandler<T>()` is CS1929. The
  fix holds the `IHttpClientBuilder` in a local and calls both on it, which
  keeps the **order**, which is the part that carries meaning. This is PR-17's
  `KnownNetworks` finding in another chapter: a sample nobody had compiled.
- **`Google.Protobuf` was pinned below its own floor, and `NU1109` is why that
  is fatal rather than cosmetic.** §4.4 printed 3.29.3; `Grpc.AspNetCore`
  2.71.0 floors it at 3.30.2, and with `CentralPackageTransitivePinningEnabled`
  a lower pin is a package **downgrade** rather than a floor NuGet quietly
  raises. The three `Grpc.*` rows had been unrestorable since they were written.
- **An HTTP resilience pipeline cannot retry a gRPC status, and the
  configuration §9.7 prints does not say so.** gRPC carries its outcome in
  `grpc-status` — a trailer on an HTTP **200** — so `AddStandardResilienceHandler`
  sees a successful response and hands it straight back. A Catalog answering
  `Unavailable` is asked **once**, whatever `MaxRetryAttempts` says. What the
  retries do cover is a transport fault, which is the shape a service that is
  genuinely down produces. Both halves are measured in `UpstreamRetryTests`;
  the test that found it was written expecting three calls and got one.

  **The fix is deliberately not a second retry loop.** gRPC's own retry lives
  on the channel and does understand status codes — and sits *outside* the
  `HttpClient`, so each of its attempts would get a fresh
  `TotalRequestTimeout`: three of them spend fifteen seconds against a
  five-second ceiling. Stacking the two is the one change that breaks the
  hierarchy §9.7 exists to protect.
- **`InvariantCulture` is half of a safe decimal parse; the `NumberStyles`
  argument is the other half.** `NumberStyles.Number` is the obvious choice and
  includes `AllowThousands`, so `"12,50"` parses under the **invariant** culture
  as twelve hundred and fifty — the exact hundredfold error the invariant
  culture was chosen to rule out, arriving through the other argument. Caught by
  a test that expected a 500 from a malformed upstream amount and got a 200. A
  wire format has no group separators, so the parse names
  `AllowLeadingSign | AllowDecimalPoint` and nothing else.
- **One `.proto`, four generated halves across three projects, and CS0436 is
  what decides where they live.** Catalog owns the contract because Catalog
  serves it — `Catalog.Api` generates `Both`, which is two of the four —
  and `Web.Bff`
  **links** the same file rather than copying it, so the client and the server
  cannot drift. The consequence is that any project referencing `Web.Bff` and
  also generating from that `.proto` has every message type twice, and CS0436
  is an error under ADR-019 — which is why `StubCatalog` lives in
  `Web.Bff.TestSupport` and why `Catalog.Api` generates `GrpcServices="Both"`
  for its own suite. The trade there was stated rather than dodged: a generated
  client nothing in production calls, against a transport adapter no test can
  reach.

  **A linked file is a `COPY` line in a Dockerfile**, and the BFF's is the only
  one in the repository that reaches into another service's tree. It is the
  same silent-breakage class PR-14 found with `ProjectReference`, with a worse
  message: Grpc.Tools names a path under `src/Services/Catalog` in a Dockerfile
  that builds no Catalog project.
- **The `.Endpoints`-only architecture gate went vacuous the moment a second
  transport namespace existed.** `PricingService` is an endpoint in every sense
  §4.2 cares about and lived in `.Grpc`, so the gate selected none of it and
  stayed green. PR-19 made the selector a pattern and — this is the half worth
  copying — added a **second test asserting the selection itself**, naming both
  adapters. Neither survives: a later shape dropped the selector entirely and
  moved that test's subject to the exemption, for the reason the next sentence
  gives. A gate that silently stops covering the newest surface is this
  repository's most-repeated failure, and the only defence is a test whose
  subject is what the gate is looking at rather than what it found.
- **The realm was built through the admin API and verified by re-importing it**,
  which is what PR-16's entry recommended and PR-16 itself did not do. The
  `web-bff` client is Keycloak's own JSON, spliced in; the whole file was then
  imported into a fresh Keycloak and eleven claims read off real tokens — the
  audience present, `sub` present, and the two negative ones that matter most:
  a service account carrying **no** `permission` claim, and every existing
  login keeping `sub`, `email` and `realm_access`.

  **What stands afterwards is less than that run, and the difference is worth
  knowing.** `KeycloakIdentityTests` pins four of the eleven — the audience on
  the BFF's token, the absent `permission` claim, that a host running the real
  `AddJwtAuthentication` accepts it, and that a client without the scope is
  refused. The login half is `RealmImportTests`, which reads the file and
  starts nothing. So no standing test mints a password-grant token for `demo`
  or `browser`: the claims those logins carry were verified once, by hand, and
  a realm edit that broke them would be caught statically or not at all.
- **A premise about a rule is falsified by the first case that needs it, and
  `No_client_secret_is_committed` was that rule.** It said no client ships a
  secret — true while no client used the client-credentials grant, which is
  precisely two parties holding one string, one of which is a committed Compose
  file. Letting Keycloak generate it would leave the realm and the deployment
  disagreeing and the BFF refused at the token endpoint on every call. The rule
  **narrowed rather than lapsed**, and narrowing made it stronger: the value is
  pinned to the documented local default, so a generated secret still fails and
  so does a real one. PR-15 recorded the same shape about `EfUnitOfWork`.
- **The scaffold's probe port had quietly taken 5200, which was the BFF's all
  along.** `PORT = 5199` carries a careful paragraph about avoiding the 51xx
  block; the second probe was spelt `PORT + 1`, and §14.1's fence has shown
  5200 beside `web-bff` since PR-06. Every render in that suite started
  refusing the day this PR landed. **A port chosen for one constant is not a
  port reserved for two** — and arithmetic at a call site is what hid it, so the
  second port is a named constant now.

  The rest of the scaffold reconciliation is the ordinary price CLAUDE.md
  already names: ten new files classified, the gRPC package, the `Protobuf`
  item and both `Program.cs` blocks patched out, and the non-vacuity test
  replaced by the comment that tells the next service when to add it back. A
  scaffolded service was rendered and **built** afterwards, because the Python
  suite never compiles one.

## PR-27 — the body ceiling and ADR-020's response compression

PR-27 landed the last two entries of §10.1's "It does" list — the body ceiling
and ADR-020's response compression — and five of its decisions bind what comes
after:

- **`EnableForHttps = true` is what makes the edge compress at all, and this
  file argued the exact opposite first.** The claim was that TLS terminates at
  the ingress (§10.1), so the gateway is served plain `http`, so the flag never
  fires and setting it true merely says out loud what happens anyway. Every
  clause true, conclusion inverted: §4.2's forwarded-headers block enables
  `XForwardedProto`, `UseForwardedHeaders` rewrites `Request.Scheme` from the
  ingress's header, and the compression middleware decides at the first
  **write** — below the whole pipeline — so the scheme it reads is the
  rewritten one. At the default, a gateway behind an HTTPS ingress compresses
  **nothing** and no response says why. Copilot round 1 found it;
  `ForwardedSchemeCompressionTests` is the measurement, red against the
  property removed.

  **The lesson is not about compression.** A middleware that acts on the
  response decides *after* everything below it has run, so reasoning about
  what it "sees" from the position of its `Use` call is reasoning about the
  wrong moment. `UseResponseCompression` sits above `UseForwardedHeaders` and
  still reads the header `UseForwardedHeaders` wrote. Any claim of the form
  "this middleware runs before that one, so it cannot see X" is worth
  measuring rather than reading off the pipeline order.

  What survives the correction is the *decision* and the shape of its
  argument: the flag cannot be argued from the scheme in either direction —
  the response reaches the browser over TLS whatever the inner hop was — so
  ADR-020 argues it from content. No body crossing this edge pairs a secret
  with reflected input.
- **The one body that reflects a client-supplied value is the one the default
  MIME list omits, and that is luck rather than design — so a test pins it.**
  §10.5's problem+json carries the `X-Correlation-Id` the caller may have
  chosen (§10.4), which is the input half of BREACH; `application/problem+json`
  is absent from `ResponseCompressionDefaults.MimeTypes` and therefore travels
  plain. Nothing in this solution decided that, so `CompressedResponseTests`
  asserts both directions from the wire. **Adding a type to
  `CompressibleContentTypes` is re-taking ADR-020**, not a tuning change.
- **The 413 needed no exception handler, and the 400 and 409 rows each did.**
  Kestrel throws `BadHttpRequestException` carrying the status and
  `ExceptionHandlerMiddleware` reads it off the exception instead of defaulting
  to 500, so §10.5's shape arrives with `correlationId` and `traceId` for free.
  Measured over both framings — a declared `Content-Length` and a chunked body
  with none — because the plausible failure was the opposite one: YARP's
  forwarder absorbs client-body faults into its own 400, and it does not absorb
  this.
- **`ConfigureKestrel` is a silent no-op under `TestServer`, so the limit is
  the first property in the solution that a real server has to serve.**
  `WebApplicationFactory.UseKestrel(0)` is the seam, and its ordering is
  load-bearing: it throws once the host is initialised and `CreateClient` is
  what initialises one, so a factory whose client is taken first is a
  `TestServer` again with no failure to say so. The general rule is worth
  carrying past the gateway — drive `TestServer` for what the *application*
  decides, a real server for what the *server* decides, and the two are
  indistinguishable from the test.
- **The compression middleware has no ordering rule a test can catch, and
  saying so is the point.** Moved below the auth pair and the limiter, every
  test in `Gateway.Api.Tests` stays green — because the only bodies those
  middlewares produce are problem+json, which is not compressed. Its *absence*
  is caught immediately, which is the failure mode that matters:
  `AddResponseCompression` succeeds and compresses nothing without
  `UseResponseCompression`, exactly the shape §10.3's registration has. Both
  halves measured, in the habit PR-16 and PR-17 established — do not write down
  an ordering claim a test is not making.

**One test claim was found false by asserting it**, and it is the sharper
finding of the two: the chunked-body case was a second copy of the
`Content-Length` case, because `StreamContent` over a `MemoryStream` reads the
stream's length and sends the header anyway. It passed, for the wrong reason,
and only `ContentLength.ShouldBeNull()` told the difference. **A test named for
a case is not a test of it** — the streaming path is the one an attacker
chooses, since omitting a header costs the sender nothing.

**"A test that would pass" is this PR's most repeated error, and it was written
three times before anything checked it.** Copilot round 2's suppressed block —
which carried five findings under a heading saying no new comments were
generated — caught the same inversion at three sites: that the size-limit suite
"would pass" over `TestServer`, that a decompressing client would "leave every
assertion passing", and that a test carrying its own copy of the ceiling "would
pass" against a differently configured gateway. **All three are the opposite of
what happens.** Measured for the first: over `TestServer` the suite goes red,
two of three, because the oversized bodies reach the destination and answer 204
where 413 was expected.

The useful half is what the measurement added. Exactly **one** test passes
there — the one asserting a body *at* the ceiling is forwarded — so the silent
outcome is real but belongs to a suite written from the acceptance side alone.
Asserting the boundary from both sides is what converts it into a loud failure,
which the suite already did and the prose had not noticed. **A hazard framed as
"this would pass" is a claim about a run nobody performed**; this repository
already says not to write down an ordering claim a test is not making, and this
is the same rule for a counterfactual.

**ADR-020's escape hatch was named wrong too, and PR-19 is who it costs.** The
first version told the BFF to protect a secret-bearing response by *encoding*
it itself, on the ground that the middleware skips a response already carrying
a `Content-Encoding`. The mechanism is right and the instruction is useless:
gzip opens the same length side channel wherever it is applied, so a
BFF-compressed secret leaks exactly as a gateway-compressed one does. The
answer taken at the time was `Content-Encoding: identity`, skipped by the same
header check and readable on the wire — **which stood for two rounds and is
also wrong**; the paragraph below is where it lands. Copilot round 1 again,
and it is worth noticing that both of its findings were **the argument being
wrong while the code was right**: the flag and the header check were correct
in `Program.cs` throughout. A review that only diffs code would have found
neither.

**`Cache-Control: no-transform` was the opt-out all along, and the gateway was
violating RFC 9111 by ignoring it.** Round 3 proposed the directive and was
half right; round 5 pressed the other half and was fully right. The framework
does **not** implement it — measured twice, an 8 KiB body coming back gzipped
with the directive intact — but §5.2.2.6 says an intermediary "regardless of
whether it implements a cache" MUST NOT transform the content, a content coding
is such a transformation (RFC 9110 §7.7), and a YARP gateway is an
intermediary. So the gateway now carries
`NoTransformResponseCompressionProvider`, a subclass of the framework's own
with one case in front of `ShouldCompressResponse`, registered by `Replace`
rather than by sitting above `AddResponseCompression`'s `TryAddSingleton`.

**The intermediate state is the lesson, and it lasted two rounds.** Having
measured that the framework ignores the directive, this record treated the
measurement as though it settled the question — pinning the violation in a test
and telling PR-19 to use `Content-Encoding: identity` instead. A measurement
says what the code *does*; it never says what it *may* do. The specification
was one fetch away and nothing had read it.

**The request form is honoured too, and quoting it is what kept the claim
honest.** Round 8 asked for it and framed it as the same obligation; §5.2.1.6
says only that "the client is asking for intermediaries to avoid transforming
the content", where §5.2.2.6's response form is a MUST NOT. Both are refused —
a caller who says so explicitly should be believed, and it is one header read —
but the asymmetry is recorded rather than flattened into "the RFC requires it",
which is false of half of it. Three rounds in a row turned on the difference
between what a specification says and what everyone assumes it says, this one
included: **fetch the section and paste the sentence.**

**Reading a request header in a response decision costs a `Vary` entry**, and
the fix for round 8 introduced that debt in the same commit that paid off the
last one. The representation now depends on `Cache-Control`, so a shared cache
with no `Vary: Cache-Control` may serve a stored gzipped variant to the one
caller who asked for none — the policy undone from outside the process, by
something the gateway does not control. Advertised on **every** decision,
compressed ones included, because absence is a value. Round 9 found it, which
makes it the second time in this branch that fixing one thing quietly broke a
neighbouring one; the first was a wrap regression a script caught.

## PR-18 — Ordering, the second service

PR-18 landed Ordering — the scaffold's own output plus one domain, five
projects rendered by `tools/new-service` and then given §5's `Order`, with
nothing about the wiring hand-written or reconciled afterwards. It also carries
PR-16's deferred security test: user A *cancelling* user B's order → 404,
§11.4's ownership check, which needed the first resource in the platform that
has an owner.

**This block was written later than the rest of the log, and that is itself
worth recording.** PR-18's findings were filed as annotations on `CLAUDE.md`'s
directory tree rather than as a decision block, so when that tree was
compressed to one line per entry they had nowhere to land. Seven lessons were
one edit away from being lost, and none of them was inventory. **A decision
belongs in the log, not in a caption** — a tree annotation is read as a
description of what a directory holds, so a rule hidden in one is invisible
to the next person who trims the description.

- **`OrderLine` has its own `OrderLineId`, and a shared key type would be a
  silent equality bug.** `Entity<TId>` compares the *type* as well as the
  value, so a line keyed by `OrderId` would compare equal to the order that
  owns it. The separate key type is what keeps identity meaning one thing.
- **`CustomerId` deliberately has no `New()`.** A customer identifier is
  minted outside this service; a factory on it would invite an aggregate to
  invent one, which is the shape §6.4 removes from the command in the next
  entry.
- **`PlaceOrderCommand` carries no `CustomerId` — the handler reads
  `ICurrentUser.Id`** (§6.4). A subject supplied by the caller is a subject
  the caller can choose. `CancelOrder` is the same argument at the other end:
  §11.4's ownership check fails closed, and `CommandOrigin.User` is the
  **zero value** so an unset origin checks the owner rather than bypassing
  the check.
- **Handlers are public, because §6.2's scan is public-only.** An internal
  handler is not a compile error and not a registration failure — it is
  registered as nothing at all, and the first symptom is a command with no
  handler at runtime.
- **`OrderLine` is mapped as a related entity, not an owned collection**, and
  the reason is a framework limit rather than a modelling preference: an owned
  builder has no `ComplexProperty`, and the line carries `Money`.
- **The `Local`-lane round trip could not be copied from Catalog unchanged.**
  A record's generated equality compares an `IReadOnlyList` **by reference**,
  and two of Ordering's five events carry one — `OrderPlacedDomainEvent` and
  `OrderConfirmedDomainEvent` — so the assertion that passes
  for Catalog's single-valued event silently compares identity here. The
  domain allow-list is four entries for a neighbouring reason: the first event
  earned `System.Collections` and `Money.Of` earned `System.Linq`.
- **`OrderingPermissions` holds policies only.** `orders:admin` is not among
  them — it is a claim the handler reads directly, per §11.4, and registering
  a policy for it would imply an endpoint-level gate that does not exist.

`AssemblyMarker` is gone and `Order` is the gates' anchor, which is PR-11's
rule running in its stated direction: the scaffold **emits** a marker so a
service with no domain type has something for §4.2's two gates to name, and the
first aggregate is when it is **deleted**.

## PR-17 — the gateway

PR-17 landed the gateway — §10.2's routes, §10.3's limiter, §4.2's edge
pipeline — and fourteen of its decisions bind what comes after:

- **An unresolvable policy name stops the gateway; it does not silently drop
  the route, and four sites said it did.** §10.2, §4.2's sample, §11.4's
  callout and Appendix C's PR-17 row all described a per-route drop that leaves
  the host "up healthy serving whichever routes happened to validate".
  Measured: `ProxyConfigManager.InitialLoadAsync` throws out of
  `MapReverseProxy()` with an `InvalidOperationException` naming the policy and
  the route, for **both** registries — the authorization one and the rate
  limiter's. All four were amended. The correction runs the reassuring way, and
  the consequence worth carrying is that **the gateway is the one host where an
  unregistered policy name fails better than in a service**, where §11.4's
  endpoint still throws on the first request that reaches it.
- **The whole route file ships, three of its four services ahead of
  themselves.** This is the opposite of the Compose rule and the asymmetry is
  in what each costs: a Compose block naming an absent image fails `up`, a
  route to an absent destination 502s one path. What buys it is that PR-17's
  two config tests say nothing over a single route — §11.4 names a vacuously
  passing policy test as its own defect — and that delivering the file a route
  at a time makes each later PR re-decide the policies, which is §10.2's
  dual-version trap. **It is not licence to invent routes**: a `/api/v2/orders`
  route would fail the forwarded-path assertion, correctly, and the
  dual-version pair stays an example in the chapter.
- **The forwarded path is a prefix of the service's group, not an equality**,
  and Catalog is the counterexample that settled it: `/api/v1/catalog/{**}`
  strips to `/v1/catalog` while `ProductEndpoints` maps
  `/v1/catalog/products`. Appendix C said "equals" and was amended. The
  registry the assertion reads is hand-written, one entry per cluster, both
  directions asserted — `ContractSamples`' shape — because reading it from the
  services would mean the gateway's suite referencing every service, which is
  the coupling §10.1 exists to prevent.
- **A stub destination that answers beats an address that refuses, and the
  measurement is the argument.** Pointing the clusters at `127.0.0.1:1` cost
  ~2 s a request on this host, so exhausting §10.3's 100-request window took
  three and a half minutes, the window replenished, and the rate-limit test
  failed while the limiter worked. A Kestrel server on an ephemeral loopback
  port is faster *and* is the only thing that can observe the forwarded path,
  which is the assertion §10.2 says nothing else in the solution can make.
- **Both conditional reads are hoisted out of their options callbacks.**
  §4.2 printed `GetRequiredSection("Cors:Origins")` inside `AddCors`'s lambda,
  which runs when the CORS options are first resolved — on a request. "Enabled
  but unconfigured" then throws at a request rather than at a deployment, which
  is the exact deferral the flag pair exists to avoid. Both reads moved above
  their registrations and `ConditionalBlockTests` holds all four states.
- **§4.2's forwarded-headers block did not compile at this pin.**
  `KnownNetworks` carries `ASPDEPR005` in .NET 10 — an error under ADR-019, not
  a warning — and its replacement `KnownIPNetworks` takes `System.Net.IPNetwork`
  while the bare name binds to `Microsoft.AspNetCore.HttpOverrides.IPNetwork`,
  brought into scope by the `using` the `ForwardedHeaders` flags need. Two
  wrong spellings on one line, found by compiling it.
- **The 429 is written through `IProblemDetailsService`.** §10.3 printed
  `WriteAsJsonAsync`, which emits `application/json` and runs none of §10.5's
  customisation — so the one response a client is most likely to handle
  programmatically would carry neither the right media type nor
  `correlationId`, on a platform whose stated promise is one error shape.
- **`Retry-After` rounds up, and the rule needed a type to be testable at
  all.** The obvious `(int)remaining.TotalSeconds` truncates, so a lease with
  0.8 s left advertises `Retry-After: 0` — not a lost fraction but an
  instruction, sending a well-behaved client back into a limiter still
  refusing. What makes it interesting is the second half: the 429 test asserted
  a floor on the header and **passed with the truncating cast**, because the
  window is a minute long and a rejection carries tens of seconds. Reaching the
  defect through HTTP means holding a window open for fifty-nine seconds.
  `RetryAfterHeader` exists so three rows of a theory can do it instead — and a
  comment claiming the HTTP test caught it was written, and was wrong, before
  this was measured.
- **The authenticated rate-limit policy had no test, and the one added does
  not catch §4.2's ordering rule.** Only the anonymous window was ever driven
  to rejection, so the subject partition — the thing making a per-user quota
  per-user — rested on nothing. The new test proves two subjects hold
  independent buckets; run against a pipeline with `UseRateLimiter` moved above
  `UseAuthentication` it still passes, as does every other test in that
  project. The limiter is
  live under the reversal (the anonymous window still rejects), so the "degrades
  to per-IP" mechanism is reasoned and unobserved while the "silently" half is
  measured. §4.2 now says which is which. **PR-16's lesson repeated exactly**:
  keep the line, and do not believe a test is watching it.
- **The forwarded-headers block had no positive test, and the limiter's
  ordering row still has none — the contrast is the point.** Both are "this
  middleware must run before that one" claims about the same pipeline, and
  only one of them turned out to be observable. `ForwardedHeadersTests` spends
  one forwarded address's window, proves it is refused, and shows a second
  address still served; moved below `UseRateLimiter`, the two collapse onto the
  one connection the gateway can see and it goes red. The limiter-vs-
  authentication row reversed the same way and **nothing failed**. So a
  middleware-order rule is testable or it is not, case by case, and which is
  which has to be measured rather than assumed from the shape of the claim.
  Under `TestServer` the peer address is null, so the test installs an
  `IStartupFilter` to give the request one — the only seam that gets in front
  of a `Program.cs` a test may not edit.
- **"Blank counts as missing" had to be learned twice, and the second time
  was a review finding.** PR-16 wrote it into `AddJwtAuthentication` for
  `Identity:Authority` and PR-16's entry below records the argument — an
  environment
  variable set to the empty string reaches `Configuration` as `""`, not null.
  The gateway's `Cors:Origins` then shipped guarded by `GetRequiredSection`
  alone, which proves a section *exists*: `Cors__Origins__0=` binds to an array
  holding one empty string, `WithOrigins` accepts it, the host starts, and
  every browser request is refused by a policy matching no origin. **A lesson
  recorded in prose is not a lesson applied**; the guard is now a check on the
  bound values with a test behind it, which is the form that travels.
- **The fix that lands in code and not in the sample is this repository's
  most reliable defect, and PR-17 produced five of them.** `CLAUDE.md`'s *one
  rule that matters* already says a code change contradicting a chapter is not
  done
  until the chapter moves with it; what PR-17 adds is the direction it actually
  fails in. Not code drifting from a written spec — a *correction* landing in
  `Program.cs` or a test and never reaching the sample it was copied from. The
  CORS guard grew four clauses over four review rounds and §4.2's sample
  tracked it a round late every time; the stub-path assertion was tightened in
  `ProxiedRouteTests` and left weak in §12.4. **Each one re-arms the defect for
  whoever builds the next host from the chapter**, which is precisely who the
  chapter is for. The habit that catches it is mechanical: after fixing a line
  that came from a sample, grep the blueprint for the line you replaced, not
  for the topic.
- **401 and 403 carried no body at all, in every host, since PR-16.** §10.5
  opens by promising one error shape "regardless of which service produced
  it", and its own table lists both statuses — but a challenge and a forbid are
  written by the middleware before any endpoint runs, and
  `AddCommonProblemDetails` only supplies a writer that nothing on that path
  was calling. So the two statuses a client meets first were the two that broke
  the promise. **`app.UseStatusCodePages()` is the whole fix** — since .NET 8
  it writes through `IProblemDetailsService` — and it is one explicit line per
  host rather than something `AddCommonWebDefaults` can add, because it is
  middleware and §4.2 keeps middleware order visible at the composition root.
  Found by asserting the media type on a gateway 401, which is the assertion
  nobody had written: `ShouldBe(HttpStatusCode.Unauthorized)` passes just as
  happily on an empty response.
- **A permission a *route* requires obeys §11.4's rule exactly as an
  endpoint's does, and the realm role arrives in the same change as the
  constant.** PR-17 registered `inventory:admin` and named it on a route
  without adding the role to the realm's `commerce-api` client, so
  `/api/v1/inventory` was 403 for every principal Keycloak could issue — not
  a wrong answer a test would catch, a path nobody could reach. **Neither
  existing guard could see it**: §11.4's constant makes a *misspelling* a
  compile error and says nothing about a name the provider has never heard of,
  and `RealmImportTests`' closed-set assertion compares against a literal
  because `Common.Web.Tests` is a building block's suite and may not reference
  a host to read its constants. So the check lives with the constant —
  `GrantablePermissionTests` in `Gateway.Api.Tests`, observed red against a
  renamed role — and **Catalog owes the same test**: `catalog:write` is
  grantable today because PR-16 happened to add both halves at once, not
  because anything checks that it did. Verified in a live Keycloak rather than
  by reading the export: both roles present, `demo` still carrying exactly
  `catalog:write`, `browser` still carrying no `permission` claim at all, and
  `sub`, `email` and `realm_access` all intact — the negative half being the
  one §11.5 says matters most.

## PR-16 — security

PR-16 landed security — §11.3's JWT validation in `Common.Web`, §11.4's
policies and port, the realm import — and seven of its decisions bind what
comes after:

- **`ICurrentUser` and `HttpContextCurrentUser` are common, not per-service,
  and §11.4 was amended.** The chapter wrote `Ordering.Application` and
  `Ordering.Infrastructure` for the same reason §9.4 wrote
  `ordering.OutboxMessages` — it is Ordering's viewpoint. Nothing in either
  type names a service. The implementation could not go in
  `Common.Infrastructure` in any case: that project takes no
  `FrameworkReference` and `IHttpContextAccessor` arrives with one, so
  `Common.Web` is the only building block that can hold it. Both are
  registered by `AddCommonWebDefaults`, beside the `AddHttpContextAccessor()`
  without which `ValidateOnBuild` fails instead of the first ownership check.
- **`Identity:Authority` is an eager read that throws naming the key, not an
  options type.** §15.4 says `ServiceIdentityOptions` is deliberately the
  *only* options type in the solution and argues why; a second bag bound to a
  section holding one value is the shape that rule forbids. §12.4's fixture
  comment claimed `OptionsValidationException` here and was amended. The
  audience is a **constant** for the neighbouring reason — §11.5 gives the
  platform one audience, so the value never varies between environments, which
  is §15.4's own test for what is not configuration.
- **The GET stays anonymous, permanently, and says so.** PR-10's README named
  the whole slice as a temporary gap; only the write path was one. §10.2's
  `catalog-public` route matches GET alone and carries no `AuthorizationPolicy`,
  so a product listing is public at the edge and public here. The group fails
  closed with `RequireAuthorization()` and the GET adds `AllowAnonymous()`
  explicitly — absence and decision must not look the same.
- **`WebApplication` adds the authentication middleware itself, so no test can
  catch `app.UseAuthentication()` being deleted.** §4.2's ordering table said
  its absence 403s every authenticated request and §12.4 named a 401 test as
  the thing that catches it; both were checked by deleting the line, after
  which every test in the repository still passed. Keep the explicit calls —
  they are about **order**, they are required by any host that is not a
  `WebApplication`, and an implicit pipeline is unreviewable — but do not
  believe a test is watching them.

  **The claim stops at deletion, and a review round found the table promising
  more than that.** Auto-insertion is suppressed by the markers the explicit
  calls set, so it repairs an *omission* and not an *ordering*: both calls
  present in the wrong order means authorization evaluates against a `User`
  nothing has populated, and every authenticated request 401s. Measured through
  a real `WebApplication` over three pipelines — correct 200, **reversed 401**,
  neither 200. So the framework protects a host from forgetting a line and not
  from misplacing one. `Common.Web.Tests` carries all four claims, the third
  being a regression guard on the framework and the fourth this one.
- **The realm is a full Keycloak export and shrinking it is a silent
  catastrophe.** A hand-written import naming only the `commerce-api` client
  scope is the obvious first attempt; Keycloak treats `clientScopes` as the
  **complete** set, so the built-ins are never created and the token loses
  `sub`, `preferred_username`, `email` and `realm_access` at once. `sub` is the
  one that matters — `ICurrentUser.Id` reads it. Found by importing exactly
  that file into a container and reading a token, which is also how the shipped
  realm was verified. **Build a realm through the admin API and export it; do
  not write one.**
- **Permissions are client roles on a `commerce-api` client, not realm roles.**
  Measured, not assumed: a realm-role mapper also emits `offline_access`,
  `uma_authorization` and `default-roles-commerce` into the `permission` claim,
  which puts Keycloak's internals into the platform's vocabulary and makes it
  open-ended. The negative half is what the verification turned on — an
  ungranted user must carry **no** `permission` claim at all.
- **`TestAuthHandler`'s constant is `SchemeName`.** `AuthenticationHandler<T>`
  already declares a protected `Scheme`, so §12.4's printed `public const
  string Scheme` hides it, and CS0108 is an error under ADR-019. The sample had
  been unbuildable since it was written; the same collision bit a second time
  inside a nested probe handler, where `Scheme` silently bound to the base
  property instead of the enclosing constant.

**Four more arrived from the review loops, and all four are about things no
test in the repository was watching.**

- **A `ProjectReference` is a `COPY` line in two Dockerfiles, and forgetting it
  breaks the images silently for as long as nobody runs one.** `dotnet restore`
  writes each project's own `obj/project.assets.json`, so a csproj absent when
  it runs is not restored and the `--no-restore` publish fails four steps later
  with `NETSDK1004` naming a project the Dockerfile never mentions. PR-14 drew
  `Catalog.Infrastructure → Common.Contracts` and `→ Common.Infrastructure`
  without the two lines, and **both images were unbuildable from PR-14 until
  PR-16 found it by running the stack**. `dotnet build Platform.slnx` cannot
  see this, and neither can CI: the compose smoke is the only job that builds
  these images and it is path-filtered on `deploy/compose/**`, while a
  reference lands under `src/`. Fixing the filter is a real option and a wider
  change than this PR; the honest state is that the gap is named in both
  Dockerfiles and in §15.2, and carried by whoever adds the next reference.
- **Keycloak's issuer follows the request host unless `KC_HOSTNAME` says
  otherwise, and both halves of the fix are load-bearing.** A token minted
  through `localhost:8080` and a discovery document read through
  `keycloak:8080` disagree about `iss`, so `ValidateIssuer` rejected the exact
  token `deploy/compose/README.md` tells a developer to obtain — on a stack
  where every container reported healthy. `KC_HOSTNAME` pins the frontend
  issuer and `KC_HOSTNAME_BACKCHANNEL_DYNAMIC` keeps the JWKS URI
  container-reachable; **one without the other trades one broken flow for
  another**, which is why they arrive together. Measured on the master realm
  rather than argued.
- **A host-run service is Production, and that is what breaks the inner
  loop.** No project ships a `launchSettings.json`, so `dotnet run` selects
  Production, where `RequireHttpsMetadata` is on — and against a plain-HTTP
  local authority the host never fetches the discovery document at all.
  `ASPNETCORE_ENVIRONMENT=Development` leads **every host-run block that names
  an authority** — Catalog's and, since PR-17, the gateway's, but not the
  migrator's, whose job never sees a token. This line said "both host-run
  blocks" and PR-17 made it false by adding a third: the gateway snippet went
  out without the export and did not start when pasted into a clean shell,
  which is what a rule stated as a count rather than as a reason costs. The
  containers set it, which is precisely why the Compose path never showed it.
- **`ICurrentUser`'s implementation reads one authenticated projection, not
  `HttpContext.User`.** Claims and authentication are independent: a
  `ClaimsIdentity` with no authentication type carries claims perfectly
  happily and still reports `IsAuthenticated` false, so members reading the
  principal directly answered a subject and granted a permission for a caller
  the interface denies. Nothing reaches it today — `JwtBearerHandler` produces
  an authenticated principal or an empty one — which is the argument for
  fixing a fail-closed contract while it is still theoretical rather than the
  argument against.

**One finding against `CLAUDE.md`'s own procedure**, worth keeping because it
cost work: the scaffold cleanup it prescribes ends with
`git checkout -- Platform.slnx deploy/compose/`, which is correct only while
the PR does not itself change `deploy/compose/`. PR-16 changes all three files
in that tree, and the cleanup reverted them. **Commit before dogfooding the
scaffold**, or restore the tree's own changes afterwards.

## PR-15 — the consume side

PR-15 landed the consume side — §9's remaining contracts, §9.5's inbox, §9.4's
two consumers and one retention purge over both tables — and eight of its
decisions bind what comes after:

- **The contract assembly is complete, and §3.2 is what decided that.** Five
  versioned namespaces, twenty-six records and two static vocabularies —
  every name in §3.2's Publishes and Accepts columns plus the payload types
  §9.1 and §9.6 give them. This suspends the usual rule that a record belongs
  in the PR whose code publishes it, and Appendix C is what suspends it: the
  §12.6 suite constrains the assembly as a whole, so the rules "arrive with the
  assembly they constrain". **It is not licence to keep adding.** A sixth
  service's contracts arrive with that service.
- **`InboxFilter<T>` and both consumers are `Common.Infrastructure`, not
  per-service, and the chapters were amended to match.** §9.4 and §9.5 write
  `namespace Ordering.Infrastructure.Messaging` for the same reason §9.4 used
  to write `ordering.OutboxMessages` — the chapter is Ordering's viewpoint.
  Nothing in any of the three is per-service; what *is* per-service is which
  endpoint binds which contract, and that stays in each service's
  `AddMassTransitMessaging`.
- **The filter's `DbContext` is an alias, and the delegate in it is
  load-bearing.** `AddScoped<DbContext>(sp => sp.GetRequiredService<CatalogDbContext>())`
  is the registration; `AddScoped<DbContext, CatalogDbContext>()` compiles,
  resolves, and builds a **second** context in the same scope — so the inbox
  row commits in its own transaction and §9.5's atomic row silently becomes its
  non-atomic one. Nothing fails, which is why a test asserts the two
  resolutions are one instance.
- **Catalog binds no receive endpoint, and that is asserted rather than
  assumed.** §3.2 gives it one Consumes cell — `StockLevelChanged`, Inventory's
  — and no `IIntegrationEventHandler` for it exists until §8.4's cache
  invalidator has a cached query to invalidate. Binding a type with no handler
  is one of the two sites §9.4 says must throw, so the endpoint would fault
  every message it received. This is PR-14's `Local`-lane shape exactly: the
  consumers are proven by the in-memory harness in `Common.Infrastructure.Tests`,
  and the inbox and purge by container tests over the real host.
- **The inbox table ships to every service anyway, for `AddOutbox`'s reason
  inverted.** The purge runs from first boot and deletes from both tables, so a
  service carrying it without the table logs a failed delete every pass —
  where a dispatcher without its table logs a failed claim twice a second.
  Consuming nothing does not exempt a service; Catalog itself is the proof.
- **The inbox row is staged *after* the consumer returns, and staging it
  earlier is a silent disabling of the whole mechanism.** A row added before
  `next.Send` is a tracked entity on the context the consumer also uses, and
  every message-borne command reaches §6.3's `TransactionBehavior` →
  `EfUnitOfWork.ExecuteAsync` → `db.ChangeTracker.Clear()`, PR-09's line. The
  clear takes the pending row, the following `SaveChangesAsync` writes nothing,
  and no command is ever recorded. Two mechanisms already here, each right on
  its own, in tension where they meet — and invisible until a consumer does
  work, which is why the covering test drives one that clears the tracker.
- **A rolled-back unit of work now clears the tracker too, and the comment that
  said it need not is the lesson.** `EfUnitOfWork` returned on a failed
  `Result` leaving the rejected mutations tracked, because "§6.3's behaviour
  declines to SaveChanges … which is enough for tracked changes" — true while
  that behaviour was the *only* caller of `SaveChanges` on the scope. The inbox
  filter is the second, and it saves unconditionally, so a domain refusal
  would have committed its own mutations outside the rolled-back transaction.
  **A premise about who calls a method is falsified by the next PR that calls
  it**, and this one was.
- **`ProcessedAt IS NOT NULL` on the outbox purge is load-bearing, and is
  tested as such.** Purging on age alone deletes the abandoned rows §13.6's
  alert exists to surface — permanent data loss presenting as a clean, empty
  table. The inbox purges on age alone and the asymmetry is deliberate: an
  inbox row records completed work, so there is no unfinished state for a
  predicate to protect, and what protects it is a window that must outlast the
  broker's longest redelivery. Both windows are a registered `RetentionPolicy`
  rather than constants, because §9.5 tells the reader to check one of them.

**Two findings PR-15 made against the blueprint rather than against the code**,
both fixed in the chapters:

- **§12.6's round-trip assertion could not pass as written.**
  `ShouldBeEquivalentTo` compares the object graph, and a collection expression
  assigned to an `IReadOnlyList<T>` compiles to a synthesised read-only list
  where `System.Text.Json` returns a `List<T>` — a difference that is nowhere
  in the wire format. The suite compares the two **serialised** forms instead,
  because the wire form is what a contract actually is.
- **That comparison has a blind spot, and it takes a second test.** A member
  that fails to serialise at all is absent from both forms, so the contract
  loses a field and the round-trip stays green. A companion assertion requires
  every declared public property to appear in the JSON.

## PR-14 — the outbox

PR-14 landed the outbox — §7.5's flow end to end, §9.4's dispatcher, §9.3's
allow-list mapper — and six of its decisions bind what comes after:

- **`Common.Contracts` exists, with two files.** Appendix C put the project at
  PR-15 and it could not wait: `OutboxMessage.Stage` reads
  `message is IIntegrationEvent`, `MessageTypeMap` selects on that interface,
  and an allow-list mapper with an empty registry could not carry §12.4's
  "the domain type never reaches the broker" — which is only checkable
  because the contract and the domain event have different names. PR-15 adds
  the remaining records to a project that exists rather than creating one.
- **A value object on the `Local` lane needs a `JsonConverter`, and its
  absence is silent.** §5.3's `Money` is a `readonly record struct` with a
  private constructor and two get-only properties; `System.Text.Json` does not
  refuse that shape, because a struct always has a parameterless constructor —
  it builds the default, finds no setter, and returns `Amount = 0` with a null
  `Currency`. Two fixes were tried and rejected: `[JsonConstructor]` puts
  `System.Text.Json` in a domain assembly, which §4.2's allow-list gate names
  as forbidden, and a public constructor does not even work, because for a
  struct the implicit parameterless one still wins. The fix is
  `MoneyJsonConverter` in `Catalog.Infrastructure`, beside the
  `ComplexProperty` mapping that already persists the same type as two
  columns. `OutboxJson` is therefore a **registered instance taking its
  converters**, not a static field: the converters are half of what "both
  sides must agree" means. Verified red by deleting the registration.
- **`ProjectionRegistry`'s memo is a container-scoped singleton, not a static
  field.** §7.5's argument — DI registrations do not change at runtime — holds
  for one container and fails for a process holding several: two
  `WebApplicationFactory` hosts in one test assembly would share whichever
  answer was computed first, so the suite proving an event with no handler
  stages no `Local` row would poison the suite proving that one with a handler
  does.
- **`OutboxDispatcher` is registered with `AddHostedService<T>`, and the
  generic overload is load-bearing.** It records an `ImplementationType`,
  which is what `CatalogApiFactory` matches on to remove *only* this hosted
  service — MassTransit's bus is one too, so `RemoveAll<IHostedService>()`
  would stop the broker. A factory registration leaves `ImplementationType`
  null and that removal would match nothing, leaving the dispatcher draining
  rows underneath the assertions about them.
- **Catalog registers no projection handler and stages no `Local` row**, and
  that is asserted rather than assumed — it is the `IProjectionRegistry`
  contract observed from outside. §8.4's cache invalidator needs a cached
  query to invalidate and there is not one yet, so the lane's behaviours are
  proven by domain events and handlers in `Catalog.TestSupport`, admitted to
  the map through `MessageTypeSource.Add` — the mechanism §9.4 designed that
  type for.
- **The outbox schema is a registered `OutboxTable`, not a SQL literal.** §9.4
  writes `ordering.OutboxMessages` into code every service shares, which
  cannot be right; a dispatcher per service would be §9.3's prohibition on a
  second outbox table set arriving by the back door. The schema is
  shape-checked, because it is the one identifier interpolated into a
  statement rather than parameterised.

## PR-13 — the bus

PR-13 landed the bus — `AddMassTransitMessaging` in
`Catalog.Infrastructure/Messaging`, the RabbitMQ registration of §9 with no
consumer on it yet — and five of its decisions bind what comes after:

- **The helper is per-service, in the `Redis/DependencyInjection` shape.** It
  is where each service's consumers, sagas and receive endpoints will be
  configured (§9.6 registers Ordering's saga inside it), and it keeps
  MassTransit out of `Common.Infrastructure` until PR-14's outbox — the first
  common code that names a MassTransit type (`IPublishEndpoint`).
- **Broker readiness is MassTransit's own health check.** `AddMassTransit`
  registers `masstransit-bus`, tagged `ready`, itself — verified in the 8.5.3
  source — so no health-check line exists for the bus and the
  `AspNetCore.HealthChecks.Rabbitmq` pin is gone: its parameterless
  `AddRabbitMQ()` resolves an `IConnection` nothing registers, a latent
  defect §13.5 now documents. `WaitUntilStarted` stays false; readiness
  carries the wait, and `DatabaseSmokeTests` polls ready to 200 to prove the
  bus connects against a real broker.
- **`ConnectionStrings:RabbitMq` is read eagerly and throws naming the key**
  (the `AddSqlServer` posture), so every host over `Program` — fixtures
  included — must supply one; `ServiceFixture` therefore carries a RabbitMQ
  Testcontainer beside SQL, and `CatalogApiFactory` takes both connection
  strings.
- **Usage telemetry is off.** MassTransit 8.5 reports anonymous usage data to
  a vendor endpoint by default; `DisableUsageTelemetry()` is called with the
  argument in the registration — §13.2 owns this platform's telemetry.
- **The harness smoke proves composition; the readiness poll proves the
  transport.** `AddMassTransitTestHarness` replaces an existing
  `AddMassTransit` bus with the in-memory transport (verified at the pin), so
  `MessagingRegistrationTests` proves the helper composes and the pipeline
  delivers — and deliberately not the `UsingRabbitMq` half, which the swap
  removes and `DatabaseSmokeTests` asserts against a real broker. A
  test-local record carries the smoke: no contract invented before
  `Common.Contracts` existed, no retry policy before the receive endpoints
  it attaches to (§9.8).

## PR-12 — §8 as code

PR-12 landed §8 as code — `Common.Infrastructure`, the fourth building block,
one `Redis/` folder — and five of its decisions bind what comes after:

- **`Common.Infrastructure` has no project references, and that is a claim to
  preserve.** Nothing in the Redis helpers names a domain or application
  type, so no edge is drawn — the `Common.Application ↛ Common.Domain`
  argument, one project over. PR-14's outbox is what draws edges here;
  drawing one earlier is inventing a dependency the code does not have.
- **The Redis tracing instrumentation lives in `AddRedisConnections`, and can
  never move to `Common.Web`.** The connections are keyed services; the
  parameterless `AddRedisInstrumentation()` discovers only an unkeyed
  `IConnectionMultiplexer`, so in `AddObservability` it would silently
  instrument nothing — and the package reference would hand
  `StackExchange.Redis` to hosts with no Redis. §13.2 says this; the sample
  there deliberately does not show the call.
- **No service is wired.** Catalog gained no Redis env vars, no readiness
  checks and no cached query — caching a read before ADR-018's invalidation
  machinery exists (PR-14) would teach the defect §8.4 exists to prevent.
  The helpers are proven by their own Testcontainers suite, the same shape
  as PR-04's dispatcher landing three PRs before its first service. The
  Redis keys join the Compose file with the PR whose code first reads them.
- **The key prefix is `ApplicationName` verbatim — no normalisation.** One
  source shared with §13.2's `service.name`, nothing to drift. §8.3's
  lowercase examples show a service whose ApplicationName is `catalog`, not
  a lowering rule. `RedisKeys` has deliberately no `Cache(string)` method:
  cache keys are prefixed by `InstanceName`, and a full-key builder would
  double-prefix the moment its result reached `HybridCache`.
- **`RemoveByTagAsync` works at this pin, verified.** §8.4's invalidation
  mechanism was proven by the container suite two PRs before its consumer —
  along with the mandatory TTL on the lock (refused before any I/O), the
  token-checked release (a stale handle must not delete the next holder's
  key), and the span tests — one per keyed connection — which force the
  `TracerProvider` the way a host's startup would because a raw
  `ServiceProvider` runs no hosted services.

## PR-11 — the scaffold

PR-11 landed the scaffold of §4.5 — `tools/new-service/new_service.py`, stdlib
Python, one command per service — and six of its decisions bind what comes
after:

- **Catalog is the template, read at run time.** There is no template
  directory, so there is one copy of the wiring rather than two that drift, and
  the scaffold's tests render *this* repository. The consequence is stated in
  `CLAUDE.md`'s scaffold section and worth repeating here: a Catalog change can
  turn
  `tools/new-service`'s suite red, and reconciling the script belongs in the
  same change.
- **The scaffold copies no domain.** The slice is excluded by name, so a new
  service is PR-07's state with the wiring accumulated through PR-16 on it —
  five service projects, three test projects and a `TestSupport` library
  (§4.1 calls that last one *not* a test project, and counting it as one is a
  drift a review has already caught here), both images, the Compose pair, the
  `InitialCreate` migration with `AddOutbox`, `AddInbox` and
  `AddOutboxRetentionIndex` beside it, the bus
  registration with its harness smoke, §9.4's outbox and §9.5's inbox wired and
  empty, the retention purge over both tables, PR-16's token validation and
  `TestAuthHandler`, and no aggregate.
  Five things arrive with the first real slice, each noted at the line
  concerned in the generated code: `Dapper`, the application-test container
  wiring, the two silent-scan registration tests, the permission constant with
  the policy that names it and `AuthorizationPolicyTests` beside them, and —
  with the first domain event — §12.4's round-trip assertion and a
  `JsonConverter` for any value object that event carries.

  **The middleware stays and the policies go, and the split is the point.**
  `UseAuthentication`/`UseAuthorization` are copied because §11.2 says every
  host validates its own tokens whether or not it has an endpoint; a
  `{Service}Permissions` constant and the policy registered from it leave with
  the slice, because a permission nothing requires is a name in the realm
  nobody can act on.
- **The outbox and the inbox ship with their tables, which is why `AddOutbox`
  and `AddInbox` are copied
  rather than dropped with Catalog's other migrations.** A service carrying
  the dispatcher without its table would log a failed claim twice a second
  from its first boot; one carrying the retention purge without the inbox
  table would log a failed delete every pass, and consuming nothing does not
  exempt it. The snapshot is EF's own description of the model that
  leaves — the **last** migration's designer with the aggregate's `Entity(...)`
  block
  removed, which is the one edit made to a machine-owned file here. **The last
  one, and taking an earlier one is a defect with no symptom until the
  service's first `migrations add`**: the outbox designer knows nothing of the
  inbox, so the snapshot would omit a table the `DbContext` maps and EF would
  emit a second `CreateTable` for one the scaffolded migrations had already
  created. Verified
  rather than argued, the same way PR-11's empty snapshot was: a scaffolded
  service was built, `migrations add` was run against it, the generated `Up`
  came out empty and EF's rewritten snapshot was byte-identical to the emitted
  one. Two details were found only by that diff — EF sorts `System` usings
  **before** everything else, which a plain alphabetical sort got wrong the
  moment a `System` using first appeared, and `System.Collections.Generic`
  leaves with the aggregate, because EF emits it for the
  `Dictionary<string, object>` a `ComplexProperty` is mapped as.
- **`AssemblyMarker` runs the other way, and it is easy to state backwards.**
  The scaffold **emits** it — a service with no domain type has nothing for the
  two §4.2 gates to name — and the first aggregate is when it is **deleted**
  and the gates re-anchor, which is what PR-10 did to Catalog's when `Product`
  arrived. It does not "arrive with the first slice"; it leaves then. Seeing
  one in a service that *has* an aggregate is a defect, not a convention.
- **The template has no single line ending, and a tool that reads it must not
  assume one.** `.gitattributes` forces `*.cs text eol=crlf`, so C# is CRLF on
  every machine — but `.csproj`, `.slnx`, the Compose YAML, the Markdown and
  the Dockerfiles carry no attribute and arrive CRLF on Windows and **LF on the
  Ubuntu runner**. The scaffold's first version spelt its anchors with CRLF,
  passed on the machine that wrote it and matched nothing in CI. Anchors are LF
  now, matched against normalised text, with each file's own endings restored
  on the way out. Anything else in this repository that reads a file as text
  and looks for a literal line has the same trap waiting.
- **The generated model snapshot is EF's own output, not a hand-written copy.**
  It is derived from `InitialCreate.Designer.cs`, which already holds the
  tool's description of an empty model with a default schema. Verified rather
  than argued: a scaffolded service was built, `dotnet ef migrations add` was
  run against it, the generated `Up` was empty and EF's rewritten snapshot was
  byte-identical to the emitted one. Two details were found only by that diff —
  EF sorts its `using` block by namespace (so `;` must not participate in the
  sort), and the sort order changes when the service name passes `Microsoft`.

## PR-10 — the first vertical slice

PR-10 landed the first vertical slice — `Product`, `PublishProductCommand`,
`GetProductsQuery` with §6.5's cursor pagination, the two Dockerfiles, the
Compose pair on port 5102 and the `docker-compose.infra-only.yml` override
(profiles technique, printed in §14.1) — and five of its findings bind what
comes after:

- **`ValidationExceptionHandler` is §10.5's 400 row, found by the first real
  endpoint.** Until PR-10 nothing translated `ValidationBehavior`'s thrown
  `ValidationException`, and the wire answered 500 for a malformed request.
  The handler lives in `Common.Web`, registered by `AddCommonProblemDetails`,
  and §10.5 now names it — the chapter previously implied the translation
  without showing it.
- **Locally there is one `sa` login and two configuration keys.** §7.1's
  callout used to claim Compose seeds both logins; §14.2, §12.4's fixture and
  the shipped Compose file all collapse the logins and keep the keys apart,
  and §7.1 was amended to match. The identity split is a cloud-side control;
  the key split is what every local environment exercises.
- **`Catalog.TestSupport` exists**, because PR-10 was the second consumer §4.1
  was waiting for (not PR-16, as `CLAUDE.md` once guessed): the handler tests
  live in `Catalog.Application.Tests` per §12.1 and share `ServiceFixture`
  with `Catalog.Api.Tests`. It is a Library, so it references
  `xunit.v3.extensibility.core` — `xunit.v3` itself refuses non-Exe output.
- **The compose smoke now builds images.** The application blocks carry
  `build:` stanzas, so the path-filtered workflow compiles the solution inside
  Docker; PR-10 raised its timeout to 25 minutes, **PR-17 raised it again to
  30** for the gateway's image, **PR-18 raised it to 40** for Ordering's pair
  and **PR-19 to 45** for the BFF's — six images, five minutes each on top of
  the 15 that pulls alone cost, the workflow header carrying the reason every
  time. The number lives
  in `.github/workflows/compose.yml` and is restated here, which is what makes
  it a claim to reconcile rather than a fact to read: it went stale the moment
  a third image joined, stayed stale for four review rounds, and went stale
  again in the very branch that raised it — this sentence was still saying 30
  while the workflow said 35, found by Grok round 4.

  **Then the raise itself was wrong, which is the more useful failure.** 35
  came from adding PR-17's +5 again, where PR-18 adds *two* images and owed
  +10; both stated rules — `30 + 2 × 5` and `15 + 5 × 5` — give 40, and the
  header said "two more take the same five minutes each" directly above the
  35. Copilot round 9 found it. **A count in a comment guards nothing until
  somebody multiplies by it**, and a sentence explaining the guard is the
  easiest thing in the file to read as already-checked. A change under `src/`
  alone does not re-run the workflow — per-service CI builds are PR-25's.
- **Chiselled images take the `-extra` tag, and the suffix is load-bearing.**
  Plain chiselled runs globalization-invariant and `Microsoft.Data.SqlClient`
  refuses to open a connection under it — found when the containerised
  migrator first ran, fixed in both Dockerfiles and §15.2's samples. Every
  later service image inherits this: `-extra` is ICU and tzdata, nothing
  else. Verified live: `up --wait` treats a `service_completed_successfully`
  one-shot as satisfied on exit 0 and failed on exit non-zero, so the smoke
  asserts the migrator's exit code for free.

## PR-09 — TransactionBehavior, and the retry fix it shipped

PR-09 landed §6.3's `TransactionBehavior` and did **not** draw the
`Common.Application → Common.Domain` edge — the behaviour reads
`ModifiedAggregateCount` as an `int` and calls `DispatchAsync(CancellationToken)`,
so neither signature names a domain type. PR-14 drew it, with §7.5's
`IDomainEventCollector`, exactly as predicted — and the argument survives the
edge: `TransactionBehavior` still reads an `int`, because counting behind the
port is what keeps EF's change tracker on Infrastructure's side of §4.2. A
reference existing is not permission to start using it. PR-09 brought
`IDomainEventDispatcher` forward as an interface only, over Catalog's
`NullDomainEventDispatcher`, which PR-14 deleted.

PR-09 also shipped PR #15's retry fix — `db.ChangeTracker.Clear()` at the top
of every `EfUnitOfWork.ExecuteAsync` attempt, so a transient fault cannot
re-run the domain method on attempt 1's tracked, already-mutated aggregates
and commit the mutation twice. Both halves are tested: a strategy subclass
retrying a marker exception proves the delegate re-runs and the raw write
commits once, and the identity-map half — attempt 2 must read committed
state, not attempt 1's mutation — is asserted through a **test-only
`IModelCustomizer`** that maps a `TrackedProbe` entity onto the fixture's
probe table in the retry tests' own `DbContextOptions` and nowhere else. That
was first deferred to PR-10 as needing an entity type; a Copilot review on
PR #18 pushed back, and the customizer is the answer that costs neither a
production model change nor snapshot drift. The technique generalises: a test
that needs an entity the model does not have swaps the customizer, never
edits `CatalogDbContext`.

**Two standing facts, restated here rather than left in commit bodies:**

- **Raised events are no longer dropped, and PR-14 picked them up without
  touching `Product`** — which is what the aggregate raising anyway between
  PR-10 and PR-14 bought. Every `Product.Publish` now reaches §9.3's
  allow-list and commits a `Broker` row in the same transaction as the
  product. What is still dropped is the *`Local`* lane: Catalog registers no
  `IProjectionHandler`, so §7.5 stages no row for one, and that is asserted
  rather than assumed.
- **`IdempotencyBehavior`'s seat was reserved rather than filled.** PR-09
  added the third behaviour and left the fourth's place *between* Validation
  and Transaction, with the registration comment naming it.
  `PublishProductCommand` carries no `CommandId` for the same reason — §6.4
  warns the field without the interface is unprotected, so both join with
  §8.5's PR. **How many behaviours the pipeline registers today is
  `CLAUDE.md`'s**, not this entry's: it changes when §8.5 lands, and a count
  in two places is one to reconcile.

**What PR-09's line does not fix is the commit-acknowledgement race**, and that
stays open past it on purpose. If `CommitAsync` succeeds on the server and
the connection drops before the ack, the strategy retries work that is already
durable, and no in-process tidying can tell those two states apart. Closing it
needs an idempotency marker written *inside* the transaction — §8.5's
`IIdempotentCommand` already carries a usable `CommandId`, but
`IIdempotencyStore` is Redis-backed and outside the transaction, so a Redis
claim is not atomic with the SQL commit. **PR-14 did not close it, and changed
what it costs rather than leaving it unexamined**: with the outbox in place a
lost acknowledgement republishes the same fact, which is the at-least-once
delivery §9.4 promises and §9.5's inbox is built to absorb — a duplicate
rather than an invisible double-apply. The SQL-side marker is still the fix
for the *command*, and it belongs with §8.5's `IdempotencyBehavior`, whose
seat between Validation and Transaction is already reserved.

## PR-08 — the persistence layer

PR-08 landed the persistence layer, and three of its decisions bind what comes
after:

- **Catalog has a connection string, so it has a readiness check** (§13.5), and
  a host with no `ConnectionStrings:Catalog` no longer starts —
  `AddSqlServer` throws on a null one. Every `WebApplicationFactory` over
  `Catalog.Api` supplies one; `CatalogApiFactory` — in `Catalog.TestSupport`
  since PR-10 — is the single place that does it.
- **The migration is hand-authored and the snapshot is not.**
  `20260808035156_InitialCreate.cs` was rewritten into house style, because it
  is a file people edit — §7.4's hand-written DDL rides in its `Up`, and
  IDE0161 fails the build on the block-scoped namespace EF generates. The
  `.Designer.cs` and `CatalogDbContextModelSnapshot.cs` beside it carry an
  `auto-generated` header that exempts them from the analysers and are left
  **exactly** as the tool wrote them: the snapshot is the input to the next
  `migrations add`, and an edited one produces a wrong migration a PR later.
- **`dotnet test` needs Docker from here on.** Persistence is what made it
  true: Catalog gained a connection string and a real migrator run, so its
  container-backed suites cannot be satisfied by a fake. Each such suite owns
  its collection and therefore its own container set, which is §12.4's stated
  price. **The live list of which projects need a daemon is `CLAUDE.md`'s**, in
  its commands section — it has grown twice since PR-08, and this entry records
  the decision rather than the tally.

