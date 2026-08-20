{{- /*
§7.4's migration Job, run by Helm as a hook before anything else in the release
moves. ADR-007 is the decision behind it: never `Database.Migrate()` at
application startup, because three replicas race to apply the same migration, a
rolling deploy runs old code against a new schema, and the runtime identity
would need DDL permissions it must not have.

The hook weight is negative so this runs ahead of any other hook a chart grows,
and `before-hook-creation` deletes the previous Job of the same name rather
than leaving a graveyard of completed ones. Both are §7.4's.

**Idempotent, not merely correct once** (§15.1). A config-only deploy goes
through the same `helm upgrade` and therefore the same hook with the same
migrator image, and `Database.Migrate()` applies nothing when the history table
is already current — which is what makes re-running it on every deploy safe.

Rendered only where `image.migrator` is set — and the gateway and the BFF do
not include this template at all, because they own no database (§10.1, §4.2).
Two ways of saying one thing, deliberately: §15.3 argues the gateway's chart is
not a service chart with the database parts deleted, so the absence is
structural, and the guard here is what catches a chart that grows the template
before it grows the value. smoke.sh asserts the two agree, because either half
on its own is a claim nothing tests.
*/}}
{{- define "commerce.migrationJob" -}}
{{- if .Values.image.migrator -}}
{{- $tag := include "commerce.tag" . -}}
apiVersion: batch/v1
kind: Job
metadata:
  {{- /*
  The tag is in the name because the Job is per-release-image, and truncation
  is a guard rather than a nicety: Kubernetes stamps `job-name` onto the pods
  it creates and a label value may not exceed 63 characters, so a long tag
  would fail at the API server rather than here. The budget is comfortable for
  what CI actually supplies — `ordering-api-migrate-` is 21 characters and a
  40-character commit SHA lands on 61 — and a tag long enough to truncate would
  collide with its neighbours, which is why the shape of the tag is worth
  keeping boring.
  */}}
  name: {{ printf "%s-migrate-%s" (include "commerce.name" .) $tag | trunc 63 | trimSuffix "-" }}
  labels:
    {{- include "commerce.labels" . | nindent 4 }}
  annotations:
    "helm.sh/hook": pre-install,pre-upgrade
    "helm.sh/hook-weight": "-5"
    {{- /*
    `hook-succeeded` as well as `before-hook-creation`, and without it these
    Jobs accumulate for ever.

    `before-hook-creation` deletes the previous resource of the SAME NAME, and
    the name above embeds the tag — so every new SHA is a differently named Job
    and nothing ever collects the last one. A namespace would carry one
    completed migration Job per release it has ever seen. That is not
    theoretical tidiness: the runbook (§13.6) looks for the failed Job, and it
    is looking in a list of every migration that has ever succeeded.

    Failures are deliberately NOT collected. `hook-failed` would delete the one
    artefact `migration-failure.md` needs, so a failed migration stays until
    somebody has read it — which is also why there is no
    `ttlSecondsAfterFinished` here: the TTL controller does not distinguish
    Complete from Failed.
    */}}
    "helm.sh/hook-delete-policy": before-hook-creation,hook-succeeded
spec:
  backoffLimit: 2
  template:
    metadata:
      labels:
        {{- include "commerce.labels" . | nindent 8 }}
    spec:
      restartPolicy: Never
      securityContext:
        runAsNonRoot: true
      containers:
        - name: migrate
          image: "{{ .Values.image.registry }}/{{ .Values.image.migrator }}:{{ $tag }}"
          imagePullPolicy: {{ .Values.image.pullPolicy }}
          securityContext:
            allowPrivilegeEscalation: false
            capabilities:
              drop: [ALL]
          env:
            {{- /*
            The MIGRATOR identity (DDL), not the runtime one — §7.1's split.
            This secret is mounted here and into no API pod, which is the whole
            point of there being two: the running service holds a login that
            cannot alter its own schema.
            */}}
            - name: ConnectionStrings__{{ .Values.database.connectionName }}Migrator
              valueFrom:
                secretKeyRef:
                  name: {{ required "database.migratorSecretRef.name is required whenever image.migrator is set." .Values.database.migratorSecretRef.name | quote }}
                  key: {{ required "database.migratorSecretRef.key is required whenever image.migrator is set." .Values.database.migratorSecretRef.key | quote }}
          resources:
            {{- toYaml .Values.migrationJob.resources | nindent 12 }}
{{- end -}}
{{- end -}}
