# Runbook — migration job failed

| | |
|---|---|
| Alert | `MigrationJobFailed`, in `deploy/observability/alerts/platform-alerts.yaml` |
| Condition | A `pre-install,pre-upgrade` hook Job non-zero, or a release stuck pending |
| Signal | `kube_job_status_failed`, plus `kube_job_status_active` and `kube_job_status_start_time` for the stuck-pending branch — kube-state-metrics, not solution instruments |
| Owner | Platform ([§13.8](../backend-architecture/13-observability.md)) |

## What it means

The deploy stopped **before any pod rolled**
([§7.4](../backend-architecture/07-persistence.md)). The migration runs as a
Helm `pre-install,pre-upgrade` hook precisely so that a schema change that
cannot be applied never reaches a running workload.

**Establish install vs upgrade first**, because the availability answer is
opposite in the two cases and nothing else on the dashboard distinguishes them:

- **Upgrade** — the previous version is still serving. Users are fine. This is
  why no other alert fires, and you have time.
- **First install** — there is no previous version and no pod at all. Nothing
  else fires for the *opposite* reason: there is nothing to be unhealthy yet.

```bash
helm -n <ns> history <release>
kubectl -n <ns> get deploy
```

A history with one revision, or no Deployment, is an install. Do not promise
availability until you have looked.

## Read the Job's log

The migrator is its own host and its own image (§15.2) — a generic host with no
listener, running `Database.MigrateAsync` and exiting.

**The Job's name carries the image tag and no release prefix.** §15.3's template
builds it as `<workload>-migrate-<image.tag>` — so it changes every deploy, and
a name guessed from the release will not resolve. Take it from the first command
rather than typing it:

```bash
kubectl -n <ns> get jobs -l app.kubernetes.io/component=migrator
job=$(kubectl -n <ns> get jobs -l app.kubernetes.io/component=migrator \
        -o jsonpath='{.items[-1].metadata.name}')

kubectl -n <ns> logs job/"$job"
kubectl -n <ns> describe job/"$job"
```

That the name embeds the tag is deliberate — it is what lets a re-deploy of the
same chart run a *new* Job rather than colliding with the finished one — and it
is also why the chart refuses a tag long enough to push the name past 63
characters, which is a `job-name` label the API server would reject mid-upgrade.

The usual failures, in the order they happen:

### It could not connect

`ConnectionStrings__OrderingMigrator` is a Secret supplied to the **Job only**
(§15.4) — a separate identity from the runtime one, with DDL rights §7.1
deliberately withholds from the service. Three ways it fails:

- The Secret is missing, so the pod never starts. `describe` shows
  `CreateContainerConfigError` and names the key. §15.3's charts make a
  `secretKeyRef` to a missing Secret a pod that never starts, by design.
- The credential is wrong or expired after a rotation that reached the vault and
  not the cluster.
- The database is unreachable — a NetworkPolicy or a firewall, not the app.

### A migration threw

The log names the migration. This is the interesting case and the one where
rolling *forward* is usually right.

- **A constraint that existing data violates.** Adding a `NOT NULL` column
  without a default to a populated table, or a unique index over values that are
  not unique. The fix is a corrected migration, not a retry — the data will
  violate it again.
- **A lock timeout.** A long-running transaction elsewhere held the object.
  Genuinely transient; retry.
- **Drift.** Somebody changed the schema by hand and the migration's assumptions
  no longer hold. Compare `__EFMigrationsHistory` against the migrations in the
  image.

```sql
SELECT TOP 20 MigrationId, ProductVersion
FROM __EFMigrationsHistory
ORDER BY MigrationId DESC;
```

### The release is stuck pending

`helm` reports `pending-install` or `pending-upgrade` when a hook never
completed — often because the Job is still retrying rather than because it
failed.

```bash
helm -n <ns> status <release>
kubectl -n <ns> get job/"$job" -o jsonpath='{.status}'
```

A pending release blocks the next deploy. Once the Job's outcome is understood,
`helm rollback` (upgrade) or `helm uninstall` (failed first install) clears it.

## Rolling forward

**Forward, not back, and the asymmetry is the point.** EF Core migrations are
not reliably reversible against data, and the previous version is still serving
on an upgrade — so there is no availability pressure forcing a fast, risky
decision.

1. Fix the migration in a branch.
2. Verify it against a restored copy of the affected database, not against an
   empty one. Most of these failures are about *existing data* and an empty
   database proves nothing.
3. Ship it as a normal deploy. The hook runs again.

**A partially applied migration is the case that needs care.** EF wraps each
migration in a transaction where the provider allows it, but some DDL on SQL
Server is not transactional. Check `__EFMigrationsHistory` before re-running: if
the row is present the migration is recorded as applied and re-running does
nothing, while the schema may be incomplete. Repairing that means a new
migration that brings the schema to where the recorded one claims it is — never
by deleting the history row, which invites the whole migration to run again over
objects that already exist.

## What not to do

- **Do not apply the schema by hand** to unblock a deploy. The history row will
  not match and the next migration will fail in a stranger way.
- **Do not give the runtime identity DDL rights** to "let the app migrate
  itself". §7.1's split is the reason a compromised service cannot alter its own
  schema.
- **Do not delete the Job** before reading its log. It is the only record.

## Closing it

The alert clears when no failed Job matches. Confirm the *deploy* completed
rather than the Job merely being gone:

```bash
helm -n <ns> status <release>
kubectl -n <ns> rollout status deploy/<workload>
```

A cleaned-up Job and a release still pending is the same alert waiting to fire
on the next attempt.
