# Secrets

**How a secret gets from a vault into a running pod, how it is rotated, and
what the five places are that a new required key has to reach.**

This file is the operational half of
[§15.4](backend-architecture/15-cicd-deployment.md), exactly as
[`docs/testing.md`](testing.md) is the operational half of §12 — and it is
outside the blueprint tree on the same terms, so **§15.4 wins wherever the two
disagree.**

**The inventory is not repeated here.** Which keys exist, which are secrets,
which are conditional and which are required for one host only is §15.4's table,
and a second copy would be a second thing to reconcile. What follows is the
procedure that table implies.

## The one rule that decides the kind

**If the value contains a credential, it is a Secret.** Every connection string
in this platform does — SQL Server carries a login, RabbitMQ a user, and Redis
the per-service ACL user of [§8.1](backend-architecture/08-caching-redis.md).

A connection string in a ConfigMap is a password readable by anyone with
namespace read access. There is no "it is only staging".

**What the Kind buys is RBAC, not encryption, and the difference matters
because the wrong half is the one people remember.** A Secret is a separate
resource from a ConfigMap, so `get configmaps` and `get secrets` are separate
verbs and an ordinary read-only role grants only the first — that separation,
plus External Secrets keeping the value out of git entirely, is the reason for
the kind. A Secret's value is **base64, which is an encoding and not
encryption**, and Kubernetes stores it in etcd unencrypted unless an
`EncryptionConfiguration` is enabled on the API server.

**So encryption at rest is a cluster setting this platform depends on and does
not configure.** Confirm it is on before treating a Secret as protected
storage; §15.4 states the same thing from the specification side.

## The path a secret takes

```
External store (vault)
   ↓  ExternalSecret, reconciled by External Secrets Operator
Kubernetes Secret in the namespace
   ↓  secretKeyRef in the pod spec, rendered by the chart (§15.3)
Environment variable, double-underscore spelling
   ↓  .NET configuration binding
IConfiguration / IOptions<T>, validated at startup
```

Two consequences fall out of that chain and both have bitten:

- **The cluster never holds the source of truth.** A Secret edited by hand is
  reverted by the next reconcile, silently. Change the vault.
- **A `secretKeyRef` to a Secret that does not exist is a pod that never
  starts** — not a pod that starts without the value. §15.4 records this as the
  reason the two Redis rows are *conditional* rather than required: marking a
  key required against a solution where no host reads it does not over-supply,
  it stops the service. Since §8.5's behaviour landed the condition is met for
  Catalog and Ordering and unmet for the gateway and the BFF, so it is now a
  question a chart answers rather than one the platform answers once.

## Environment-variable spelling

.NET's double-underscore convention, so `ConnectionStrings__Ordering` binds to
`ConnectionStrings:Ordering`, and an indexed key is
`Ingress__TrustedNetworks__0`.

**Blank counts as missing, and nothing in the binding layer says so.** An
environment variable set to the empty string reaches configuration as `""`, not
null, so `GetRequiredSection` proves only that a section exists. Guard on the
bound values — this is a repository-wide lesson, not a secrets-specific one.

## Adding a required key

**A required setting is a deployment obligation**, and `ValidateOnStart` turns a
missing value into a refusal to boot. That is the right trade only if every
environment supplies it, so adding one means editing **five** places in the same
change:

| | |
|---|---|
| 1. Compose | `deploy/compose/docker-compose.yml` (§14.1), with a working inline default |
| 2. The Aspire host | §14.2 — **not adopted**, so this is a line to write only if it ever is |
| 3. Helm values | `deploy/helm/<chart>/values.yaml` and the umbrella (§15.3) |
| 4. The inventory | §15.4's table — the row is what makes the obligation reviewable |
| 5. The integration-test fixture | §12.4 — and this one fails first |

**The fixture is the one people forget and the one that fails loudest.**
`WebApplicationFactory` builds the real host, so `ValidateOnStart` runs there
too: a missing key throws `OptionsValidationException` out of `InitializeAsync`
and takes down the whole suite before a single assertion runs.

It is also the one environment where the correct value is a **fake**. The
fixture must supply something that satisfies `[Required]` and is unmistakably
not a credential — a test that passes with a real secret in it is a test that
will one day be run against something real. The `.invalid` convention is the
same idea for hosts.

## Before adding an options type at all

§15.4 is blunt about this and it belongs here too, because the cheapest secret
to rotate is the one that does not exist.

**An options type needs at least one member that differs between
environments.** If every value would be the same in Compose, in the test fixture
and in production, it is not configuration — it is a constant that has been
given a deployment obligation and four places to be forgotten.
`Identity:Client` is the only options type in the solution, and it earns that by
holding a secret that must differ per environment.

## Rotation

The platform holds **one** client secret — `Identity__Client__ClientSecret`, for
the BFF, the only host that calls a peer synchronously
([§9.7](backend-architecture/09-messaging.md),
[§11.5](backend-architecture/11-identity-authorization.md), ADR-017). Everything
else is a datastore credential.

### A client secret

Keycloak supports two active secrets during a rotation; use that rather than a
flip.

1. Add the new secret in Keycloak, keeping the old one valid.
2. Update the vault entry.
3. Wait for External Secrets to reconcile, then restart the BFF's pods —
   configuration is read at startup, so a reconciled Secret does not reach a
   running process.
4. Confirm the BFF is authenticating: pricing calls to Catalog succeeding is the
   observable proof, since that hop is the only thing the credential is for.
5. Retire the old secret in Keycloak.

**Step 3 is the one that gets skipped**, and skipping it produces a rotation
that appears to work until the next unrelated restart.

### A database credential

The migrator and the runtime identity are **separate logins** by design
([§7.1](backend-architecture/07-persistence.md)) — the runtime one has no DDL
rights. Rotate them independently, and never merge them to simplify the
procedure: that split is the reason a compromised service cannot alter its own
schema.

The runtime identity is the higher-risk rotation because it is in use
continuously. Create the new login, grant it, update the vault, reconcile,
restart, verify readiness (`/health/ready` covers SQL —
[§13.5](backend-architecture/13-observability.md)), then drop the old login.

### A broker credential

`ConnectionStrings__RabbitMq`, and there is **one per service** since
[ADR-036](backend-architecture/appendix-a-adrs.md#adr-036--the-broker-has-a-per-service-identity)
— `catalog-rabbitmq` and `ordering-rabbitmq`, never a shared Secret. That is
the half of the rotation worth knowing first: rotating the broker is now N
rotations rather than one, and rotating *one* affects exactly one service.

A rotation that reaches the vault and not the pod presents as
[`runbooks/outbox-broker.md`](runbooks/outbox-broker.md) — the broker lane
stalls with authentication failures in the log — which is worth knowing
before rotating rather than during. **The permissions are not the vault's to
rotate**: they are declared in `deploy/compose/rabbitmq/definitions.json` for
the local broker, and are an obligation on whoever provisions a deployed one.

## Local development is a deliberate exception

§14.1's Compose file carries real-looking values, and they are **not** a defect
to be tidied away:

| | |
|---|---|
| SQL Server | `${SQL_PASSWORD:-Local_Dev_Pa55w0rd!}` |
| BFF client secret | `${BFF_CLIENT_SECRET:-local-dev-secret}` |
| Keycloak admin | `admin` / `admin` |
| RabbitMQ | `catalog-svc` / `local-dev-catalog`, `ordering-svc` / `local-dev-ordering` |

These defaults are what make `docker compose up` work with no prior setup, and
**the environment variable in front of each is the seam** that keeps them out of
anything deployed. `deploy/compose/.env.example` documents the overrides.

Note how the connection strings nest — `${CATALOG_CONNECTION:-…Password=${SQL_PASSWORD:-…}…}`
— so overriding the password alone keeps every connection string correct. That
is why `.env.example` leaves the per-service connection variables commented out:
uncommenting one freezes the password inside it and it quietly stops following.

## What must never be committed

No production connection string, key or certificate path — in a sample, in a
test, or in source.

> **There is a secret scan in CI, and what it does not do is worth knowing
> before you rely on it.** §15.1 puts "SCA + secret scan" ahead of the build
> and ahead of the path-filter fork, and argues why: neither half needs a
> build, and scanning downstream of one is scanning that a build failure skips.
> Both halves now run in the `licence-gate` job, the secret scan first
> (`.github/secret-scan/`).
>
> **It reads the working tree, not the history.** A credential committed and
> then removed is still in the object store and is still compromised, and this
> gate will not find it — which is why the rotate-then-rewrite rule below is
> the procedure rather than a formality.
>
> **It is a pattern scanner and not an oracle.** Twelve rules, each named and
> each with its own tests, over key blocks, provider-token shapes and
> credential-shaped assignments. A high-entropy string that looks like nothing
> in particular passes, and so does a credential written in a shape no rule
> describes. The list of rules is the list of things it can find.
>
> **Every exception is a line in `allowed-secrets.txt` naming a path, a rule
> and a fingerprint, with a reason** — never a glob and never an inline
> pragma, on the same argument `Directory.Build.props` makes about
> suppressions. An entry matching nothing **fails the build**, so a
> suppression whose finding has gone is reported rather than left standing.
> That is why the local-development defaults below are enumerated there once
> per site: rotating one becomes a reconciliation that file lists.
>
> **A documented control that does not exist is worse than an absent one**,
> and this callout said exactly that while there was no scanner (#119). It
> stays here in that form because the sentence is now about the gate's limits
> rather than its absence, and those are the half a reader relies on without
> checking.

`No_client_secret_is_committed` is an assertion in the test suite, and its
premise is worth restating because it has already been falsified once: it was
correct until a second caller existed. A rule about who holds a credential is
falsified by the next host that holds one.

If a secret is committed, rotate it first and rewrite history second. The commit
is public the moment it is pushed, and a force-push does not un-fetch it.
