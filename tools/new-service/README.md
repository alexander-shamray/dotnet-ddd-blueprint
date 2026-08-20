# The new-service scaffold

[§4.5](../../docs/backend-architecture/04-solution-structure.md) is the
specification for this directory; this file records what a developer needs at
the keyboard. One command creates a service:

```bash
python tools/new-service/new_service.py Yankee --port 5199
```

| | |
|---|---|
| `name` | The service, PascalCase. It becomes the namespace root, the project names, the database, the SQL schema, both connection-string keys and both Compose service names |
| `--port` | The host port the API publishes. **Required** — a port is an allocation recorded in [§14.1](../../docs/backend-architecture/14-local-development.md) and in `deploy/compose/README.md`, and a script that derived one would quietly disagree with a printed chapter. The run refuses a port another service already publishes |
| `--migration-id` | The `InitialCreate` id, and the base for the outbox migration one minute after it. Defaults to the current UTC timestamp; the tests pass a fixed one |
| `--repo-root` | Defaults to this script's repository |

It writes fifty-four files and edits five. The five are `Platform.slnx`,
`deploy/compose/docker-compose.yml`, `docker-compose.infra-only.yml`,
`.env.example` and `deploy/compose/README.md`.

**Run it on a clean worktree**, and undoing it is then two commands — the
generated tree is untracked and the five edits are tracked, so neither one
alone is enough:

```bash
rm -rf src/Services/Yankee tests/Yankee.*
git restore Platform.slnx deploy/compose
```

(`Yankee` rather than a `<Name>` placeholder because this is a `bash` fence and
the shell reads `<Name>` as a redirection, not as something to fill in. It
named `Ordering` at port 5101 until PR-18 made that a real service — at which
point the create raised `ScaffoldError` on a published port, and the undo,
followed literally, deleted the service that had just landed. A probe the
suite already uses cannot become a service later, which is the property the
example wanted all along.)

`git checkout .` is **not** the undo and this file used to say it was: it
leaves every generated file in place, because they are untracked, and it
discards any unrelated tracked edit you happened to have. `git status` after a
run shows exactly the five and the new directories.

## What you get

[§4.1](../../docs/backend-architecture/04-solution-structure.md)'s five
service projects, three test projects and the `TestSupport` library — nine in
all, and §4.1 is explicit that the last is not a test project — with
everything the delivery plan has built into the template through PR-16:
`DbContext` and conventions, `EfUnitOfWork`, the connection factory, the
readiness checks, the §7.4 migrator host, the `InitialCreate` migration that
creates the schema and the `AddOutbox`, `AddInbox` and
`AddOutboxRetentionIndex` ones beside it, §9.4's outbox with its dispatcher
and allow-list mapper, §9.5's inbox filter and the retention purge, the §9 bus
registration — a scaffolded host refuses to start without
`ConnectionStrings:RabbitMq` — §11.3's JWT validation and the test auth
scheme, both Dockerfiles, the Compose pair, and the architecture gates of
[§4.2](../../docs/backend-architecture/04-solution-structure.md).

The service builds and its sixty tests pass before you have written a
line, and thirty of them run against real SQL Server and RabbitMQ containers:
the migrator's exit code, §7.1's two-key boundary, the readiness probe — 200
only once the bus connects — `EfUnitOfWork`'s commit, rollback and retry
semantics, the inbox filter's once-per-endpoint guarantee, the retention
purge over both tables, and the outbox dispatcher's per-row isolation, attempt
cap and loud failure on a `Local` row with no registered handler.

Those two counts are measured rather than estimated, and they move whenever a
PR adds to the template — they read forty-one and sixteen until PR-18
recounted them against a rendered service, three PRs after they stopped being
true, and fifty-six until PR-22 did it again.

**Adding to the number is not recounting it.** PR-22 put three tests into the
template and the total moved by four, so the only way to know this pair is to
render `Yankee` at 5199 and run all three of its suites. The thirty is the
`Category=Integration` count now, which is a filter anyone can rerun rather
than a tally somebody kept.

**The outbox arrives wired and empty**, which is the state to expect: the
allow-list mapper has no entries, so every domain event this service raises is
local-only until somebody adds one (§9.3 makes translation opt-in for exactly
that reason), and no `IProjectionHandler` is registered, so no `Local` row is
staged either. The table, the dispatcher and the type map are all live.

## What you do next

The scaffold copies **no domain**. That is the point — it renders the wiring
every service needs and none of Catalog's `Product`, so nothing has to be
deleted before the real aggregate can be written.

1. Write the first aggregate, then **delete `AssemblyMarker.cs`** and
   re-anchor `ArchitectureTests` in `{Service}.Domain.Tests` and
   `{Service}.Application.Tests` on it.
2. Add the entity configuration and run `dotnet ef migrations add` — the
   generated snapshot already describes the model the service ships with,
   which is the outbox table and nothing else, so the first migration is the
   first aggregate's table and nothing else. Verified rather than argued: a
   scaffolded service was built, `migrations add` was run against it, the
   generated `Up` came out empty and EF's rewritten snapshot was byte-identical
   to the emitted one.
3. Add the first command or query. Four things come back, each with the one
   that needs it and each noted at the line concerned in the generated code —
   they are **not** a set to restore together:
   - **The first handler of either kind** brings the container wiring in
     `{Service}.Application.Tests.csproj`, and the test that the §6.2 scan
     produced a registration. That scan fails silently when lost.
   - **The first validator** brings the registration test for the validator
     scan, which fails silently in the same way, and re-anchors
     `AddValidatorsFromAssembly` on a type. A query-only slice has no
     validator and needs neither.
   - **The first query** brings `Dapper` to `{Service}.Application.csproj`,
     for §6.5's read side. A command-only slice must not add it: an unused
     package reference is a claim the project would not be making.
   - **The first domain event** brings §12.4's round-trip assertion, and with
     it a `JsonConverter` in `{Service}.Infrastructure` for any value object
     the event carries. This one is the least obvious and the most expensive
     to skip: a value object with a private constructor does not throw on the
     outbox's `Local` lane, it deserialises to its default, and the symptom is
     a projection running on a zero (§9.4).
4. Map the first endpoint in `Program.cs`, behind `RequireAuthorization()` at
   the group (§11.4) — fail closed, and let any deliberately public endpoint
   say `AllowAnonymous()` out loud. The permission it needs is a
   `{Service}Permissions` constant and a policy registered beside the other
   two `Add*` calls; add `AuthorizationPolicyTests` in the same change, which
   is what stops a policy name resolving to nothing.

Not in scope, and not silently missing: the gateway route (PR-17 builds the
gateway), the Helm chart (PR-23), and a Worker host in place of an API — §4.1
gives Shipping and Notifications one, and no such host exists yet to copy.

**`Shipping` and `Notifications` are refused by name** for that reason: the
script renders the API shape, §4.1 gives those two a Worker, and Notifications
no Domain project either. Both names are accepted again by the change that adds
the mode.

## The tests

```bash
cd tools/new-service && py -3.12 -m unittest        # Windows
cd tools/new-service && python3.12 -m unittest      # elsewhere
```

Stdlib `unittest`, no dependencies, and **3.12 by name rather than whatever
`python` resolves to** — that is the version both CI jobs pin and the floor
this tool is written to. A newer interpreter locally is the hazard rather than
an older one: it accepts APIs 3.12 does not, so the suite goes green on code CI
cannot run. `Path.read_text(newline=…)` is 3.13 and cost a CI round exactly
that way, which is why the floor is spelt into the command instead of being
mentioned underneath it.

They run against the **real repository**: `plan()` renders from the checkout
and returns a value, and `apply()` is the only thing that touches disk — which
is why they can. That is deliberate. The design
has no template directory — the template is Catalog itself, so there is one
copy of the wiring rather than two that drift — and the risk it accepts is
that Catalog moves under the script's anchors. Only rendering the tree that
actually exists catches that.

So a Catalog change that breaks the scaffold fails here, loudly, naming the
file. If it does, reconcile `new_service.py` with the template in the same
change: a file added under `src/Services/Catalog` has to be classified as
template or slice, and the script will not guess.
