# The new-service scaffold

[§4.5](../../docs/backend-architecture/04-solution-structure.md) is the
specification for this directory; this file records what a developer needs at
the keyboard. One command creates a service:

```bash
python tools/new-service/new_service.py Ordering --port 5101
```

| | |
|---|---|
| `name` | The service, PascalCase. It becomes the namespace root, the project names, the database, the SQL schema, both connection-string keys and both Compose service names |
| `--port` | The host port the API publishes. **Required** — a port is an allocation recorded in [§14.1](../../docs/backend-architecture/14-local-development.md) and in `deploy/compose/README.md`, and a script that derived one would quietly disagree with a printed chapter. The run refuses a port another service already publishes |
| `--migration-id` | The `InitialCreate` id. Defaults to the current UTC timestamp; the tests pass a fixed one |
| `--repo-root` | Defaults to this script's repository |

It writes thirty-five files and edits five. The five are `Platform.slnx`,
`deploy/compose/docker-compose.yml`, `docker-compose.infra-only.yml`,
`.env.example` and `deploy/compose/README.md`.

**Run it on a clean worktree**, and undoing it is then two commands — the
generated tree is untracked and the five edits are tracked, so neither one
alone is enough:

```bash
rm -rf src/Services/Ordering tests/Ordering.*
git restore Platform.slnx deploy/compose
```

(`Ordering` rather than a `<Name>` placeholder because this is a `bash` fence
and the shell reads `<Name>` as a redirection, not as something to fill in.)

`git checkout .` is **not** the undo and this file used to say it was: it
leaves every generated file in place, because they are untracked, and it
discards any unrelated tracked edit you happened to have. `git status` after a
run shows exactly the five and the new directories.

## What you get

[§4.1](../../docs/backend-architecture/04-solution-structure.md)'s five
service projects, three test projects and the `TestSupport` library — nine in
all, and §4.1 is explicit that the last is not a test project — with
everything the delivery plan has built into the template through PR-13:
`DbContext` and conventions, `EfUnitOfWork`, the connection factory, the
readiness checks, the §7.4 migrator host, the `InitialCreate` migration that
creates the schema, the §9 bus registration — a scaffolded host refuses to
start without `ConnectionStrings:RabbitMq` — both Dockerfiles, the Compose
pair, and the architecture gates of
[§4.2](../../docs/backend-architecture/04-solution-structure.md).

The service builds and its twenty-eight tests pass before you have written a
line, and eleven of them run against real SQL Server and RabbitMQ containers:
the migrator's exit code, §7.1's two-key boundary, the readiness probe — 200
only once the bus connects — and `EfUnitOfWork`'s commit, rollback and retry
semantics.

## What you do next

The scaffold copies **no domain**. That is the point — it renders the wiring
every service needs and none of Catalog's `Product`, so nothing has to be
deleted before the real aggregate can be written.

1. Write the first aggregate, then **delete `AssemblyMarker.cs`** and
   re-anchor `ArchitectureTests` in `{Service}.Domain.Tests` and
   `{Service}.Application.Tests` on it.
2. Add the entity configuration and run `dotnet ef migrations add` — the
   generated snapshot already describes the empty model, so the first
   migration is the first table and nothing else.
3. Add the first command or query. Three things come back, each with the one
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
4. Map the first endpoint in `Program.cs`. It is unauthenticated until PR-16;
   say so in `deploy/compose/README.md`, as Catalog does (§C.4).

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
