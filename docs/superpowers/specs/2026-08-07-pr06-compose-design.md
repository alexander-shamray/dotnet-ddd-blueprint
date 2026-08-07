# PR-06 — Docker Compose: SQL Server, Redis, RabbitMQ, Keycloak, OTel

Design for Appendix C's PR-06, `feat(dev): Docker Compose — SQL Server, Redis,
RabbitMQ, Keycloak, OTel`, which depends on PR-01 and delivers, in Appendix C's
words: "The Compose file from §14.1, `.env.example`, documented ports,
healthchecks".

The specification is
[§14.1](../../backend-architecture/14-local-development.md). This document
records only the decisions that chapter does not already settle — chiefly what
"the Compose file from §14.1" means at a point in the sequence where none of
the application services it lists exist.

## 1. Infrastructure only

§14.1's file contains eleven services. Seven are infrastructure; four —
`ordering-migrator`, `ordering-api`, `gateway`, `web-bff` — are built from
Dockerfiles under `src/` that do not exist and will not exist until PR-17,
PR-18 and PR-19. A verbatim copy today fails `docker compose up` on the first
missing build context, and stays broken for roughly twelve PRs, against the
chapter's own opening claim that one command starts the platform.

**Decision.** PR-06 ships the seven infrastructure services — `sql`,
`redis-cache`, `redis-coordination`, `rabbitmq`, `keycloak`, `otel-collector`,
`grafana` — with their blocks verbatim from §14.1, plus the three named
volumes. Each application block arrives with the PR that builds its image.

The delivery plan already says this, just not in one place. PR-06 depends only
on PR-01, so it *cannot* carry blocks that reference service code. PR-11's
scaffold copies a "Compose block" per service, so blocks are expected to be
added per service. PR-16 delivers the "Keycloak realm import" and PR-19 the
BFF's client credentials "and nowhere else" — both are content §14.1's file
carries, assigned to later PRs by name. The infra-only override file
(`docker-compose.infra-only.yml`) is deferred on the same reasoning: its job
is to exclude application containers, and until the first one exists there is
nothing to exclude. It arrives with the first containerised service.

§14.1 keeps describing the finished file — it is the specification of the
target state — and gains one short paragraph recording the arrival order, so a
reader standing between PR-06 and PR-18 is not misled (§6).

## 2. The file set

```
deploy/compose/
  docker-compose.yml           name: commerce; the seven infrastructure
                               services and three volumes, verbatim §14.1
  .env.example                 SQL_PASSWORD only — the one variable the shipped
                               file references. ORDERING_* and
                               BFF_CLIENT_SECRET arrive with their blocks; a
                               variable referencing nothing is the same class
                               of untruth as an unused using
  keycloak/realm-export.json   the placeholder realm of §3
  otel/config.yaml             the collector config of §3
  README.md                    the documented-ports table and the up command,
                               citing §14.1 rather than restating its argument
.github/workflows/compose.yml  the CI smoke of §4
```

The ports table in `README.md` is the "documented ports" deliverable, extended
to what a developer actually needs at PR-06: SQL 1433 (`sa`), Redis cache 6379,
Redis coordination 6380, RabbitMQ 5672 and management 15672 (guest/guest),
Keycloak 8080 (admin/admin), OTLP 4317 gRPC and 4318 HTTP, Grafana 3000. The
local-development credentials are §14.1's documented defaults, not a leak —
CLAUDE.md's credential rule carves out exactly this file's
`${SQL_PASSWORD:-Local_Dev_Pa55w0rd!}` seam.

## 3. The two files the chapter mounts but never prints

**`keycloak/realm-export.json`.** The keycloak block bind-mounts this file
read-only. With no file at the path, Docker creates a directory in its place
and the import breaks — so the block cannot ship without *some* file, yet the
realm's real content (clients, scopes, claim mappers) is PR-16's named
deliverable. PR-06 ships the minimum that makes `start-dev --import-realm`
succeed and the authority URL resolve:

```json
{
  "realm": "commerce",
  "enabled": true
}
```

`http://keycloak:8080/realms/commerce` — the `Identity__Authority` every later
block points at — exists from PR-06 on. PR-16 replaces the file's content and
owns it from then on.

**`otel/config.yaml`.** The collector block mounts a config §14.1 never shows,
and the file is load-bearing: PR-05's OTLP export is what sends to it. The
config is the smallest correct pipeline — OTLP in on both protocols, batch,
OTLP out to the LGTM container, which ingests OTLP directly:

```yaml
receivers:
  otlp:
    protocols:
      grpc:
        endpoint: 0.0.0.0:4317
      http:
        endpoint: 0.0.0.0:4318

processors:
  batch:

exporters:
  otlphttp:
    endpoint: http://grafana:4318

service:
  pipelines:
    traces:
      receivers: [otlp]
      processors: [batch]
      exporters: [otlphttp]
    metrics:
      receivers: [otlp]
      processors: [batch]
      exporters: [otlphttp]
    logs:
      receivers: [otlp]
      processors: [batch]
      exporters: [otlphttp]
```

**Decision.** §14.1 gains this fence. A mounted config with unspecified
content is a gap in a blueprint whose job is to specify; the realm placeholder,
by contrast, is two keys owned by PR-16 from the moment it matters, and gets a
sentence of prose rather than a fence.

## 4. CI — the compose smoke

§14.2's Aspire callout argues the AppHost is "the only [environment] with no
automated exercise: Compose runs in CI, Helm is applied by CD". Nothing in
§15.1 runs Compose in CI, so the claim is false today. PR-06 makes it true
rather than amending it away — the healthchecks are a named deliverable, and
`up --wait` is the only thing that proves they pass.

A separate workflow, not a job in `ci.yml`, because path filtering is
per-workflow: it triggers on pull requests and pushes to `main` touching
`deploy/compose/**` or the workflow itself.

```
docker compose -f deploy/compose/docker-compose.yml config -q       # syntax
docker compose -f deploy/compose/docker-compose.yml up --wait --quiet-pull
docker compose -f deploy/compose/docker-compose.yml down -v         # always()
```

`--wait` blocks until every service with a healthcheck reports healthy and
every service without one is running, and exits non-zero if a container exits
instead — so a broken realm import that kills Keycloak fails the run even
though Keycloak has no healthcheck. The four healthchecks (`sql`, both Redis
instances, `rabbitmq`) are asserted directly.

The path filter also contains the one reliability hazard: §14.1 pins
`otel/opentelemetry-collector-contrib` and `grafana/otel-lgtm` to `:latest`,
so a broken
upstream `latest` can fail CI with no change in this repository — but only on
a PR that touches the Compose tree, never on the main build. The image tags
stay exactly as §14.1 writes them; repinning them is a blueprint decision this
PR does not take.

## 5. Appendix B — two product rows, and the gate mechanics

§14.1 has named `otel/opentelemetry-collector-contrib` and `grafana/otel-lgtm`
since it was written, and Appendix B registers neither — the products area of
the Chosen table has Keycloak and RabbitMQ rows and nothing for the two
observability images. That is the known drift class (NetArchTest and Aspire
were the earlier instances), and PR-06 is the PR that makes the images real,
so it closes the gap:

| Package | Licence | Role |
|---|---|---|
| OpenTelemetry Collector (contrib) | Apache 2.0 | Local and CI telemetry pipeline (§14.1) |
| Grafana OTel-LGTM | AGPL 3.0 (Grafana, Loki, Tempo, Mimir) / Apache 2.0 components | Local observability stack — development and CI only, never deployed |

**The rows carry no backticked identity, and that is load-bearing, not
cosmetic.** `licence_gate.py` extracts identities from the Package cell by
backtick (`` re.findall(r"`([^`]+)`", cells[0]) ``); a row with none never
enters the audit, which is exactly how the Keycloak and RabbitMQ rows are
exempt from the "registered but pinned nowhere" check. Writing
`` `grafana/otel-lgtm` `` in the first cell would fail the gate on the next
build. The image reference belongs in the role cell if it appears at all.

AGPL is acceptable here because the stack is self-hosted, development-only and
never distributed or deployed; the row says so, and the "beyond its reach"
paragraph at the foot of the appendix already covers why no tooling enforces
product rows.

## 6. Documentation reconciled in the same PR

| Where | Change |
|---|---|
| §14.1 | Three additions: the collector-config fence (§3); one sentence noting the realm file ships as a placeholder until PR-16's import replaces it; one paragraph after the Compose fence recording arrival order — infrastructure at PR-06, each application block with the PR that builds its image, the infra-only override with the first containerised service. Citing PR numbers in a chapter is established form |
| §15.1 | A short passage recording the compose smoke workflow: separate file, path-filtered, `config -q` then `up --wait` then `down -v`. Placed so §14.2's "Compose runs in CI" now points at something real |
| Appendix C, PR-06 row | The Delivers cell gains the CI smoke, since this design adds it beyond the row's current four deliverables |
| Appendix B | The two product rows of §5 |
| CLAUDE.md | The phase section (PR-07 is next; PR-05's OTLP export now has somewhere to send to), the present tree gains `deploy/compose/`, and the planned tree's `deploy/` line loses its "still ahead" status for compose |

`docs/roadmap.md` needs no change: M1 ("the foundation compiles and CI is
green") already closes at PR-06.

## 7. Verification

No C# ships in this PR, so no xunit tests — the test count is unchanged and
§12's TDD convention is not in play. The CI smoke is the automated exercise, and it
is real: it fails on a YAML error, a bad env-var reference, a missing mounted
file, an image that will not pull, a container that exits, and a healthcheck
that never passes.

Before the PR opens, the same sequence runs locally, plus endpoint probes CI
does not need: Grafana answers on 3000, RabbitMQ management on 15672, and
`http://localhost:8080/realms/commerce` returns the realm PR-06 imported —
proving the placeholder actually imported, which `up --wait` alone does not.

## 8. Deliberately not in this PR

- Application-service blocks, the infra-only override, and their `.env`
  variables (PR-17/18/19, per §1).
- Real realm content — clients, scopes, claim mappers (PR-16).
- Seed data (§14.3 — it runs from the migrator container, which does not
  exist).
- Repinning the two `:latest` images (§4).
- Any change to `ci.yml` — the smoke is its own workflow.
