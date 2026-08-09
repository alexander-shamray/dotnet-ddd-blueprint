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
rm -rf src/Services/<Name> tests/<Name>.*
git restore Platform.slnx deploy/compose
```

`git checkout .` is **not** the undo and this file used to say it was: it
leaves every generated file in place, because they are untracked, and it
discards any unrelated tracked edit you happened to have. `git status` after a
run shows exactly the five and the new directories.

## What you get

[§4.1](../../docs/backend-architecture/04-solution-structure.md)'s five
service projects, three test projects and the `TestSupport` library — nine in
all, and §4.1 is explicit that the last is not a test project — with
everything PR-07 through PR-10 built
into the template: `DbContext` and conventions, `EfUnitOfWork`, the
connection factory, the readiness check, the §7.4 migrator host, the
`InitialCreate` migration that creates the schema, both Dockerfiles, the
Compose pair, and the architecture gates of
[§4.2](../../docs/backend-architecture/04-solution-structure.md).

The service builds and its twenty-four tests pass before you have written a
line, and eleven of them run against a real SQL Server: the migrator's exit
code, §7.1's two-key boundary, the readiness probe, and `EfUnitOfWork`'s
commit, rollback and retry semantics.

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
3. Add the first command or query. Three things come back with it, each noted
   at the line concerned in the generated code: `Dapper` in
   `{Service}.Application.csproj` for §6.5's read side, the container wiring
   in `{Service}.Application.Tests.csproj`, and the two registration tests
   `Catalog.Application.Tests` carries — the validator scan and the §6.2
   handler scan both fail silently when lost.
4. Map the first endpoint in `Program.cs`. It is unauthenticated until PR-16;
   say so in `deploy/compose/README.md`, as Catalog does (§C.4).

Not in scope, and not silently missing: the gateway route (PR-17 builds the
gateway), the Helm chart (PR-23), and a Worker host in place of an API — §4.1
gives Shipping and Notifications one, and no such host exists yet to copy.

## The tests

```bash
cd tools/new-service && python -m unittest
```

Stdlib `unittest`, no dependencies, **Python 3.12** — the version both CI jobs
pin, and the floor this tool is written to. A newer interpreter locally is the
hazard rather than an older one: it accepts APIs 3.12 does not, and the suite
goes green on code CI cannot run.

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
