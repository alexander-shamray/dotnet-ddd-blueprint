# Delivery roadmap

**How long the blueprint takes to build, and what the schedule rests on.**

[Appendix C](backend-architecture/appendix-c-delivery-plan.md) sequences the
work into 26 pull requests and fixes their dependencies. It says what to build
and in what order; it does not say how long. This roadmap attaches a number to
each of those pull requests and derives a calendar from them.

It restates nothing. Every title below is Appendix C's title verbatim, every
dependency is Appendix C's dependency, and where the two disagree Appendix C is
right — this file is an estimate laid over a plan, not a second plan.

| | |
|---|---|
| **Scope** | PR-01 … PR-25. PR-26 is optional and conditional, and is priced separately below rather than counted |
| **Total** | **98 ideal engineer-days** |
| **Calendar** | **28 weeks** — roughly six and a half months |
| **Critical path** | 54 ideal days, so no amount of staffing beats about 1.8× |

## What these numbers are

They were quoted before any of this code was written, against a specification
that was already finished — an unusually good position to estimate from. The
blueprint names the types, the tests, the failure modes and the migration
shapes, so most of what normally hides inside an estimate has already been
argued out in ~10,500 lines of prose.

It is also the trap. A specification this precise makes an estimate feel
measured when it is still a guess, and the numbers below have the same standing
as any other pre-implementation estimate: they are an argument about relative
size that happens to be denominated in days. None has been re-priced against a
pull request that has actually landed. The ranking of the pull requests is the
durable part; the absolute total is the part that moves, and delivered work is
the only thing that should move it.

> **Estimate the specification, not the aspiration.** Each figure prices what
> Appendix C's *Delivers* column actually lists, including its tests. Where a
> pull request delivers documentation rather than code — PR-24's twelve
> runbooks — it is priced as the writing job it is, because writing twelve
> runbooks that are each checked in both directions is not faster than writing
> code.

## Basis

Three assumptions carry the whole schedule. Each is stated separately so it can
be argued with separately.

**Estimates are ideal engineer-days.** Uninterrupted focus on one pull request:
no meetings, no review latency, no context switching, no production
interruption. This is deliberately not a calendar figure. Mixing the two is how
an estimate stops being falsifiable — when a slipped week could mean either
"the work was larger than we thought" or "three days went to something else",
the estimate can never be shown wrong and so never improves.

**One engineer.** Chosen, not assumed. The dependency graph fans out
considerably after PR-07 — the Redis, messaging and security branches are
genuinely independent — and none of that parallelism is used here. The
*Critical path* section below prices what it would be worth.

**A solo engineer sustains about 3.5 ideal days per five-day week.** This one
ratio is everything standing between the day totals and the calendar. It is the
number to change first if the schedule looks wrong, because changing it
re-derives every date without re-estimating a single pull request. At 3.5,
98 ideal days is 28 weeks; at 3.0 it is 33; at 4.0 it is 25.

## Estimate per pull request

Phases and titles are Appendix C's. **Cum.** is the running total in ideal
engineer-days; **Week** is the calendar week that total completes in, at
3.5 days a week.

### Foundation

| PR | Title | Est. | Cum. | Week |
|---|---|---|---|---|
| **01** | `chore: solution structure, SDK pin, central package management, CI skeleton` | 3d | 3 | 1 |
| **02** | `feat(common): Result, Error, and domain primitives` | 3d | 6 | 2 |
| **03** | `feat(common): ProblemDetails, error catalogue, correlation middleware` | 2d | 8 | 3 |
| **04** | `feat(common): CQRS dispatcher and pipeline behaviours` | 4d | 12 | 4 |
| **05** | `feat(common): OpenTelemetry and structured logging defaults` | 3d | 15 | 5 |
| **06** | `feat(dev): Docker Compose — SQL Server, Redis, RabbitMQ, Keycloak, OTel` | 3d | 18 | 6 |

PR-01 is scaffolding except for the licence allow-list gate, which is a real
piece of CI work rather than a checkbox. PR-04 is the largest of the six
because the dispatcher is hand-rolled —
[ADR-004](backend-architecture/appendix-a-adrs.md#adr-004--no-mediator-library)
takes no mediator library — and the behaviour-ordering tests are the point of
it, not a garnish.

### Service template

| PR | Title | Est. | Cum. | Week |
|---|---|---|---|---|
| **07** | `feat(template): service skeleton and architecture test gate` | 4d | 22 | 7 |
| **08** | `feat(template): EF Core, repositories, IUnitOfWork, migrator host` | 5d | 27 | 8 |
| **09** | `feat(common): TransactionBehavior over IUnitOfWork` | 3d | 30 | 9 |
| **10** | `feat(catalog): first vertical slice — command, query, cursor pagination` | 4d | 34 | 10 |
| **11** | `feat(tooling): new-service scaffold script` | 3d | 37 | 11 |

PR-08 is the phase's largest and the estimate is mostly Testcontainers: the
first container-backed test in a repository costs a day that the fiftieth does
not. PR-09 looks small for what it proves, and it is — the behaviour is short.
Its three tests are not, particularly the one asserting that a handler which
writes through `ExecuteRawAsync` and then returns `Result.Failure` leaves no
row behind.

### Data, cache, messaging

| PR | Title | Est. | Cum. | Week |
|---|---|---|---|---|
| **12** | `feat(common): Redis helpers — HybridCache, key namespaces, distributed locks` | 4d | 41 | 12 |
| **13** | `feat(template): MassTransit RabbitMQ registration and harness smoke` | 2d | 43 | 13 |
| **14** | `feat(template): transactional outbox and allow-list event mapper` | 6d | 49 | 14 |
| **15** | `feat(messaging): Contracts, inbox consumers, inbox + outbox retention purge` | 5d | 54 | 16 |

**PR-14 is the heaviest pull request in the plan, tied with PR-21 and PR-24**,
and the 2d/6d split
between it and PR-13 is why Appendix C separates them. Getting a bus connection
working is an afternoon; getting an outbox transactionally correct, with
`MessageTypeMap` and `OutboxJson` both landing before any row exists in the
column they define, is most of a working week — and the integration tests
proving the aggregate row and the outbox row commit in one transaction are half
of that again.

### Edge and security

| PR | Title | Est. | Cum. | Week |
|---|---|---|---|---|
| **16** | `feat(security): JWT bearer with mandatory per-service re-validation` | 4d | 58 | 17 |
| **17** | `feat(gateway): YARP routing, JWT, rate limiting, CORS` | 4d | 62 | 18 |
| **18** | `feat(ordering): second service from the scaffold` | 3d | 65 | 19 |

PR-18 is the cheapest service in the plan and that is the whole point of it —
it is priced at three days *because* PR-11 exists, and if it turns out to cost
more, the finding is about the scaffold rather than about Ordering.

### Integration and operations

| PR | Title | Est. | Cum. | Week |
|---|---|---|---|---|
| **19** | `feat(bff): the BFF host, its gRPC client and the one permitted sync hop` | 4d | 69 | 20 |
| **20** | `feat(ordering): consume Catalog events into a local projection` | 4d | 73 | 21 |
| **21** | `feat(ordering): order fulfilment saga` | 6d | 79 | 23 |
| **22** | `test: expand architecture rules and document the test strategy` | 3d | 82 | 24 |
| **23** | `feat(deploy): Helm charts, migration hooks, probes` | 5d | 87 | 25 |
| **24** | `docs(ops): runbooks, secrets, dashboards-as-code, the SLO run` | 6d | 93 | 27 |
| **25** | `ci: integration categories, canary deploy, quality gates` | 5d | 98 | 28 |

This phase is a third of the total, which surprises people who read a plan as a
list of features. PR-21's saga carries a timeout on every wait state and a
compensation path per failure, and each of those is a harness test. PR-24 is
six days of writing: twelve runbooks, one per alert, each checked from the
alert to the procedure and from the procedure back to the alert.

### Optional

| PR | Title | Est. | Cum. | Week |
|---|---|---|---|---|
| **26** | `chore(optional): consumer-driven contract tests` | 4d | — | — |

Not counted. Appendix C makes Pact conditional on a consumer relationship
becoming contentious, and pricing a conditional into a total is how a total
stops meaning anything. If it is taken, it adds a week.

## Milestones

Six, each named for a capability the platform gains rather than for a range of
pull request numbers. A milestone that reads "PR-12 through PR-15 complete"
tells nobody outside the branch anything.

| | Milestone | Through | Cum. | Week |
|---|---|---|---|---|
| **M1** | The foundation compiles and CI is green | PR-06 | 18d | 6 |
| **M2** | One service serves one request, end to end | PR-11 | 37d | 11 |
| **M3** | Events cross a service boundary transactionally | PR-15 | 54d | 16 |
| **M4** | Authenticated traffic reaches two services through the gateway | PR-18 | 65d | 19 |
| **M5** | The full async path: projection and saga | PR-21 | 79d | 23 |
| **M6** | Deployable, observable and gated | PR-25 | 98d | 28 |

**M2 is the one to watch.** It is the first point at which the platform does
something a person outside the team can see, and it arrives at week 11 — nearly
40% of the schedule — because everything before it is the machinery that makes
the second service cost three days instead of thirty. That is the correct
trade, and it is also the trade that gets abandoned around week 8 by anyone who
has not agreed to it in advance.

**M3 and M5 are the two that can slip quietly.** Both are dominated by a single
large pull request whose difficulty is invisible from outside — the outbox and
the saga — and both are places where "nearly done" can last a week.

## Critical path

With one engineer the schedule is the sum of the parts, so the dependency graph
does no work. It is still worth pricing, because it bounds what any staffing
decision could ever buy.

The longest chain through Appendix C's graph is 54 ideal days:

```
01 → 02 → 03 → 05 → 07 → 08 → 09 → 10 → 14 → 15 → 20 → 21 → 24
 3    3    2    3    4    5    3    4    6    5    4    6    6
```

Against a 98-day total, that is a **ceiling of about 1.8×** on any team of any
size. An infinite number of engineers finishes in 54 days, and the second
engineer captures most of the available gain; the fourth captures almost none,
because by then the schedule is the chain above and the chain is serial by
construction.

Two things follow. First, the honest answer to "can we go twice as fast with
twice the people" is no, and the graph says so before anyone is hired. Second,
the chain names exactly which pull requests are worth protecting from
interruption: a day lost on PR-14 is a day on the end date, while a day lost on
PR-12 — which is not on the chain — is free until it isn't.

The chain also explains the shape of the *Integration and operations* phase.
Three of its seven pull requests sit on the critical path, which is why a phase
that reads like clean-up is a third of the plan.

## What moves these numbers

Six things, ordered by how much of the total they can move.

**The domain question is unsettled, and it is the largest single risk.** Both
READMEs call the e-commerce domain "illustrative only", while
[§4.1](backend-architecture/04-solution-structure.md) and Appendix C name six
services concretely. PR-10 has since landed on the illustrative domain, so for
it substitution now means reworking shipped code; PR-18, PR-20 and PR-21 are
still re-specified rather than re-estimated. Together that is 17 of the
98 days, and the four that carry the most design argument. Nothing else on this
list is close. Settle it before M2.

**Testcontainers and Docker on the build agent.** Priced into PR-08 as one day
of first-time cost, which assumes the agent can run containers at all. If it
cannot, PR-08, PR-12, PR-14 and PR-25 each grow and the schedule acquires an
infrastructure dependency it does not currently have.

**The Keycloak realm import.** PR-16's four days assume the realm is imported
from a checked-in file that works the first time. Realm imports are reliably
worse than expected, and PR-16 sits one hop off the critical path — close
enough that a bad week there reaches the end date through PR-17 and PR-18.

**The `Directory.Build.props` analyzer policy is settled, and it is a tax.**
[ADR-019](backend-architecture/appendix-a-adrs.md#adr-019--warnings-are-errors-and-the-editorconfig-is-a-build-input)
takes `TreatWarningsAsErrors` and `EnforceCodeStyleInBuild` from PR-01 and
declines StyleCop. The half day it adds to PR-01 is inside that estimate. What
is **not** priced anywhere below is the per-pull-request cost — small each time,
24 pull requests wide — which lands as slippage spread thin rather than as a
line item. The alternative was a sweep, which at least would have had a number.

**Aspire.** Not adopted; Compose is the baseline
([§14.1](backend-architecture/14-local-development.md)) and no `Aspire.*`
package is pinned. If it is adopted, `src/AppHost` is new work and every
service picks up the client integrations for the resources it consumes, so the
cost is a line per resource per service in both directions (§14.2). Not priced
here.

**Review latency, which this roadmap does not model at all.** Ideal
engineer-days exclude it by definition, and for a solo engineer with
self-review it is genuinely near zero. Add a reviewer and the calendar changes
without any estimate changing — which is precisely the separation the *Basis*
section exists to preserve.

---

[Blueprint index](backend-architecture/README.md) · [Delivery plan](backend-architecture/appendix-c-delivery-plan.md)
