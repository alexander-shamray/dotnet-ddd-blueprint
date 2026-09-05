# Change locality — the operating contract

How an agent works this repository so that a fix or a small feature touches
its own slice and nothing shared, so that code is the source of trust, and so
that several agents can run at once, including inside one service, without
meeting in the same file.

This file does not replace the blueprint. Where a chapter and compiled code
with green tests disagree about a **fact**, the code is right until an ADR
says otherwise. A chapter is amended when a **rule** moved, never to restate
a fact the code already owns. [`docs/change-locality-plan.md`](change-locality-plan.md)
is the sequence of PRs that makes every statement below fully true; until
those land, an agent follows this file and leaves the not-yet-cleaned files
alone.

## 1. Source of trust, in order

A lower layer cites a higher one by name. It never copies the value.

1. **Gates and tests.** Green means the claim they cover is true.
2. **Code.** Types, registrations, options, constants, SQL, Helm values. A
   value a document needs lives in one named symbol; the document names the
   symbol.
3. **ADRs** — one file each under `docs/backend-architecture/adr/`, indexed
   by `appendix-a-adrs.md`. Append-only, next free number, never rewritten.
   The only documents that change a rule.
4. **Chapters** — `docs/backend-architecture/01`–`15`. The rule and the
   reason. Not an inventory, a changelog, a count, or a copy of a constant.
5. **Git.** The commit body argues the change, the PR body is the house form,
   `git log` and `gh pr list` are the index. This is the change record.
6. **`CLAUDE.md`** and the files it points at. How to act here. Nothing that
   moves when a PR lands.

`docs/superpowers/` is a frozen pre-build record. Never edit it to match
the code that followed.

## 2. The rule

**Every fact has exactly one owner, and every other mention cites the owner.**
A document may say "`RetentionPolicy.IdempotencyWindow` has a floor and the
floor is the claim's window (ADR-038)". It may not say "seven days" unless it
is the file that defines seven days.

So a change to a fact is one edit plus a search for the **symbol**, not a
search for the value across twenty-five thousand lines. The obligation to
reconcile every restatement in the corpus is withdrawn. If you meet a stale
restatement while doing something else, leave it: removing restatements is
the plan's job, one chapter per PR, and fixing one in passing widens your
touch set for no gain.

Never write, in any document:

- a number of tests, projects, ADRs, chapters, or lines;
- a package version outside `Directory.Packages.props`, except in Appendix
  B where the version itself is the decision, as Class E says;
- a timeout, retry count, port, TTL or window as a raw integer in a second
  place;
- "since PR-NN", "this used to say", or the history of how a rule was
  corrected. The ADR trail is the history.

A measurement is not a restatement. A commit body, a PR body, an issue, or
[`change-locality-plan.md`](change-locality-plan.md) may state a count or a
value **as of a named date or commit**, because a record of what was true
when it was taken is not a claim that it is true now, and nothing has to
keep it current. The prohibition is on a document stating a fact in the
present tense in a second place.

## 3. Change classes

Name the class and the touch set before editing. Do not grow the set "just
in case". A file the work turns out to need is one of two things: inside the
class's tree set, in which case it is **added to the row with its reason
beside it** — a review found it, a test needed it — and the widening is on
the record rather than rewritten out; or outside the class's tree set, in
which case the class is wrong, so change the class, not the set. A change
that genuinely needs two classes names both — `C+E` for a rule that brings
a package — and its touch set is the union.

| Class | Examples | May touch | Never touches |
|---|---|---|---|
| **A — local** | fix a handler, tighten a test, add a validator rule, add a command field the aggregate already has | the owning slice `src/Services/<Svc>/**` and `tests/<Svc>.*`; **or** one project under `src/BuildingBlocks/` and its `tests/Common.<That>.Tests`; a migration if that service's schema moved; the runbook the alert maps to, if the change alters what an operator does for it — `docs/runbooks/<alert>.md`, or the file `SHARED_RUNBOOKS` in `deploy/observability/check.py` maps it to, which is then a mutex between the alerts that share it | any other `docs/**`; `CLAUDE.md`; a second service "to keep the sample in sync"; `.claude/**` |
| **B — shared mechanism** | change `IdempotencyBehavior`, outbox retention, a `Common.Web` middleware, a MassTransit registration helper | the building block and its tests; the *one* service test that proves the wiring; `tools/new-service` if a Catalog file was added or removed; the one chapter paragraph that states the rule; a runbook as in A | every chapter that *mentions* the mechanism; Appendix D; both services' copies unless the scaffold template is the subject; the decision log, lessons, repo map, style guide |
| **C — a rule moved** | a floor changes meaning; a host must refuse a token above a bound; a prohibition gains an exception | code and tests; one new ADR; the single chapter section the ADR amends; Class E's set beside it, as `C+E`, when the rule brings a package | every historical paragraph describing the old rule (the ADR is the correction, and chapters point at it); `docs/pr-decision-log.md`; `CLAUDE.md` |
| **D — docs or harness** | a new `/command`, a style pass, a chapter split, a runbook for an alert that already fires, this contract | only the `docs/`, `.claude/`, `.github/` or `deploy/` tree the issue names, and `CLAUDE.md` — or, when no issue was filed, the paths the touch-set row declares | inventories "while we are here"; `src/**` |
| **E — dependency graph** | add a package, raise a pin, add a project | `Directory.Packages.props` (exact pin, no `Version=` on the reference); the `.csproj`; `appendix-b-licences.md` — identity and licence, not the version unless the version is the decision; `global.json` or `.config/dotnet-tools.json`; `Platform.slnx` | the chapters that use the package |

Notes that decide the edge cases:

- Crossing two building-block projects in one PR is Class B, not A.
- Catalog is the scaffold's template and the script reads it at run time, so
  a Catalog change that adds or removes a file reconciles `tools/new-service`
  in the same PR. That is the one extra shared surface Class A and B get.
  The scaffold's own suite reads the rendered text and never compiles it,
  so a change touching `tests/Catalog.*` is not verified until a scaffolded
  service has been built — the dogfood in
  [`repo-map.md`](repo-map.md), which CI's `scaffold-build` job repeats.
- An unused project reference is a claim about the dependency graph that
  nothing makes true. Draw one only for the member that cannot be written
  without it, and a reference existing is not permission to reach across
  §4.2 through it; [`repo-map.md`](repo-map.md) argues each edge that
  exists.
- A migration is Class A only for the service whose schema moved. Two
  migrations in one PR against two services is two PRs.
- Appendix B is the one shared Markdown Class E must edit, because the
  licence gate makes it load-bearing. Do not also list the package in a
  chapter.
- A `.TestSupport` project is not a test project; it belongs to the service
  that owns it and is in that service's slice.

## 4. Parallel work

### Partitions

One worktree and one branch per agent, via `/branch`. Safe to run at once:

| Agent owns | Notes |
|---|---|
| `src/Services/Catalog/**`, `tests/Catalog.*` | plus `tools/new-service` when a file is added or removed |
| `src/Services/Ordering/**`, `tests/Ordering.*` | |
| one project under `src/BuildingBlocks/` and its tests | never two agents on `Common.Infrastructure` at once |
| one of `deploy/**`, `.github/**`, or one chapter under `docs/backend-architecture/` | one chapter per agent |
| `.claude/**` | one agent only: `settings.json` self-locks and `ship.md` is one file |

### Two agents inside one service

Safe when neither needs the same per-service mutex. The mutexes are:

- `Program.cs` — the composition root;
- the `DbContext` and its model snapshot — **one migration-adding agent per
  service at a time**;
- the service's `.csproj` files;
- `tests/<Svc>.TestSupport/ServiceFixture.cs`.

Handlers, queries, validators and their tests are scanned in through
`PluggableInterfaces`, so a new slice needs none of those four. Say in the
issue — or in the touch-set row, when there is no issue — which mutex a task
needs. Do not start a second agent on the same one.

### Repo-wide mutex surfaces

Edited by one agent at a time, and named in the issue — or in the touch-set
row, when there is no issue — when a task needs one:

```
Directory.Packages.props        Directory.Build.props        Platform.slnx
.editorconfig                   deploy/compose/docker-compose.yml
deploy/compose/keycloak/realm-export.json
.github/secret-scan/allowed-secrets.txt
docs/backend-architecture/appendix-b-licences.md
```

## 5. The procedure

What an agent does instead of touring the corpus:

1. Read the issue. Name the class and the touch set before the first edit
   — in the issue, or in the first commit's body when there is none — and
   copy them into the PR body's `| Class |` and `| Touch set |` rows when
   `/pr` opens it, with any file added mid-work and its reason (§3).
2. Search **code** for the symbol: `rg` over `src/` and `tests/`.
3. Open the owning chapter section only for Class B or C, and only the
   paragraph that states the rule. Open an ADR only to cite it or, in Class
   C, to append the next one with `/new-adr`.
4. Write the test first (§12), then the change.
5. Run the smallest suite that can fail for this class.
   `docs/testing.md` has the invocations; `dotnet test Platform.slnx
   --filter "Category!=Integration"` needs no daemon.
6. After a Catalog change, run the scaffold's unit tests with `py -3.12` —
   a file added or removed is what forces `tools/new-service` to change —
   and after a change touching `tests/Catalog.*`, the four-command dogfood
   in `docs/repo-map.md` as well.
7. `/validate-blueprint` after Class C, or after an edit to a file in its
   scope — a chapter or appendix, `docs/roadmap.md`, `docs/testing.md`. Not
   after Class A. `/ship` selects its checks by the same class.
8. Commit with `/commit`; the body argues the change and, when the change
   closes an issue, carries a bare `Closes #n` line. Open the PR with `/pr`.
9. In the review loops, a finding that asks for a restated count, a
   "since PR-NN" sentence, or a corpus-wide rename of something whose owner
   site is already correct is answered by citing this file, not by editing.
10. Stop. Do not open `CLAUDE.md` looking for a sentence that still names the
    old type.

## 6. What still applies

Locality waives one rule: that every mention of a fact must be rewritten in
the same PR. It waives nothing else. In particular:

- tests ship in the same PR as the code they cover (§12);
- exact pins in `Directory.Packages.props`, never `Version=` on a reference;
- the dialect in [`docs/style-guide.md`](style-guide.md): British prose,
  identifiers keep their real spelling, 80-column prose, 120-column code;
- ADRs appended, never rewritten;
- `main` stays green;
- when a change closes an issue, `Closes #n` as a bare line, not only
  inside a table cell;
- a kind label and a severity label on every issue;
- no `#pragma`, no real credentials;
- the harness boundaries in [`docs/harness-boundaries.md`](harness-boundaries.md)
  before touching anything under `.claude/`.

## 7. Checklist

```
[ ] Class named (A/B/C/D/E) in the PR body
[ ] Touch set listed there; nothing outside it is edited
[ ] Symbol searched in src/ and tests/, not the value in docs/
[ ] Owning chapter paragraph opened only for Class B or C
[ ] Test written first; smallest suite that can fail is green
[ ] Scaffold tests run after a Catalog change; dogfood if tests/Catalog.*
    changed
[ ] ADR appended only if a rule moved; nothing in it rewritten
[ ] No present-tense count, version or raw value written outside its owner;
    no "since PR-NN"
[ ] Appendix D, decision log, lessons, repo map, style guide, testing.md,
    roadmap and CLAUDE.md untouched unless the class names one
[ ] Mutex surfaces this PR needs are named in the issue, or in the touch-set
    row when there is none
```
