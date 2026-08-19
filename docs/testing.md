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

## The two suites

Two runners, and `dotnet test` says nothing about the second:

```bash
dotnet tool restore                # dotnet-ef, pinned in .config/
dotnet restore Platform.slnx
dotnet build Platform.slnx
dotnet test  Platform.slnx         # needs a running Docker daemon

cd tools/new-service && py -3.12 -m unittest    # no Docker, no SDK
```

**`py -3.12`, not `python`.** Every CI job that runs Python pins 3.12, and a newer
interpreter is the hazard — it accepts APIs 3.12 does not, so the local suite
goes green on code the runner cannot execute. The scaffold *script* is a
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
> selects the other seventy-two — 82 in total, with no third state and nothing
> counted twice. Across the solution the split is **612 and 164 of 776**, and
> the fast half runs in about 76 seconds.
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
59 of its 63 tests never needed a container and 4 need an identity provider, so
a project-level split would have had nothing to split. What it buys there is a
container start rather than a fast suite — the BFF's fast half still takes
about a minute, because §9.7's resilience tests wait on real timeouts.

**A test class that needs a container and forgets the collection fails loudly
rather than quietly**, which is the direction this has to fail in. It has no
fixture, so it does not run against one; it also carries no category, so it
runs in the fast half and fails there. What it cannot do is report a pass.

**Nothing in CI runs the two halves separately yet.** PR-25 owns the staged
pipeline of [§15.1](backend-architecture/15-cicd-deployment.md); this is the
category it stages on, delivered ahead of it so the filter is real before the
stage that depends on it is written.

> **A filter is a new way for a suite to not run, and that is
> [§12.1](backend-architecture/12-test-strategy.md)'s oldest trap wearing
> different clothes.** A missing test adapter makes `dotnet test` report no
> tests and exit **zero**; a mistyped `--filter` does exactly the same. The
> counts above are what makes the difference visible — 612 and 164 summing to
> 776 — so whoever writes the staged pipeline should assert a floor on each
> stage's count rather than trusting a green exit. That assertion is PR-25's
> quality gate and is named here because this PR is what created the way to
> get it wrong.

## Coverage

**Reported, not gated** — [§12.9](backend-architecture/12-test-strategy.md)
calls coverage a diagnostic rather than a target, and a threshold that fails a
build is PR-25's quality gates. What ships here is the number, measured over
the layer where it means something.

```bash
dotnet test Platform.slnx --collect:"Code Coverage" --settings coverage.runsettings
```

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

The run writes one `*.cobertura.xml` per test project under
`TestResults/<guid>/`; the `line-rate` attribute on each `<coverage>` element
is the figure, and `<package name="…">` names the assembly it belongs to.

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
They need no container, so they are in the fast half:

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
