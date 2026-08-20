{{- /*
The name every object a chart renders takes — and the one thing here that is
deliberately NOT derived from the release name.

Helm's convention is `{{ .Release.Name }}-{{ .Chart.Name }}`, and it is wrong
for this platform: these names are ROUTING CONFIGURATION. The gateway's route
file resolves `http://catalog-api:8080/` and `http://ordering-api:8080/`
(§10.2, appsettings.json), and the BFF's one synchronous hop resolves
`http://catalog-api:8081` from a literal in PricingHop.cs, which argues on the
record that the value does not vary because "the host is the Kubernetes Service
name". A release-derived name makes that false the moment the umbrella chart
installs the same workload under a different release, and the failure is a 502
rather than a template error.

So the name is a value, and it is required. `workload.name` is what the Service
is called, which is what a peer dials.
*/}}
{{/*
`required` is not enough on its own, and every guard in these charts rested on
it until Copilot said so.

Helm's `required` fails on nil and on the empty string, and passes anything
else — including `" "`. The hosts do not agree: `AddJwtAuthentication` guards
with `IsNullOrWhiteSpace` and says in its own comment that an environment
variable set to the empty string arrives as `""` rather than null. So an
overlay with `identity.authority: " "` rendered cleanly, began a rollout, and
died in the new pod — which is the failure every render-time guard here exists
to move earlier.

BLANK COUNTS AS MISSING is already a lesson in CLAUDE.md, learned twice against
this exact key. This is the third time, and it goes in one helper so there is
one place to be wrong. `toString` before `trim` because `trim` errors on a
non-string, and `tag: 1.2` is a YAML float.
*/}}
{{- define "commerce.require" -}}
{{- $value := index . 0 -}}
{{- $message := index . 1 -}}
{{- required $message ($value | default "" | toString | trim) -}}
{{- end -}}

{{- define "commerce.name" -}}
{{- include "commerce.require" (list .Values.workload.name "workload.name is required: it is this deployable's Service name, and therefore the string the gateway's route file and the BFF's pricing hop dial (§10.2, §9.7).") -}}
{{- end -}}

{{- /*
The image tag, required rather than defaulted.

values.yaml carries `tag: ""` deliberately (§15.3): a deploy that cannot name
its image must fail rather than roll something nobody chose. CI supplies it
from the build; a config-only deploy reads the running value back out of the
cluster first (§15.1). `required` is what turns the empty default into a
refusal — without it the empty string renders `image: registry/api:` and the
kubelet resolves that to `:latest`, which is the one tag §15.3 forbids by name.
*/}}
{{- define "commerce.tag" -}}
{{- include "commerce.require" (list .Values.image.tag "image.tag is required and values.yaml leaves it empty on purpose: a deploy that cannot name its image must fail rather than roll something nobody chose (§15.3). CI supplies it; a config-only deploy resolves the running tag first (§15.1).") -}}
{{- end -}}

{{- /*
The selector, which carries the workload name and NOTHING release-derived.

**Because the selector is workload identity, not release bookkeeping.** These
pods are found by their name: the Service selects them, and that name is the
string §10.2's route file and §9.7's pricing hop dial. Putting the release into
the selector would make a pod's identity depend on which command installed it,
for a field a Deployment will never let you change afterwards.

This comment used to justify it by the standalone-to-umbrella migration, and
that justification is dead — §15.3 and `platform/values.yaml` now record that
Helm rejects the adoption outright on ownership, so the migration never reaches
the API server's immutable-selector check. The conclusion survives its original
argument, which is worth saying rather than quietly keeping: a release-scoped
selector would still be wrong, and the reason is now the one above.
*/}}
{{- define "commerce.selectorLabels" -}}
app.kubernetes.io/name: {{ include "commerce.name" . }}
app.kubernetes.io/part-of: commerce
{{- end -}}

{{/*
The migration Job's POD labels, which must NOT match the Service selector.

A Service selects pods, and the migration Job's pod template carried
`commerce.labels` — which contains `commerce.selectorLabels` verbatim. So for
the length of every `pre-upgrade` hook, the migrator became an endpoint of the
service it was migrating: a pod with a database connection, no HTTP listener,
and a share of live traffic being routed to it. Measured in the render, not
inferred — `catalog-api`'s Service selector and the Job's pod labels were the
same two lines.

The same match put it inside the PodDisruptionBudget, so a one-shot pod counted
toward the availability of a service it does not serve.

`-migrate` on the name is what breaks both, and `component` says what the pod
is for anyone reading `kubectl get pods -L`. The Job OBJECT keeps the ordinary
labels: object labels are not what endpoints are computed from, and losing the
identity there would cost the one thing these labels are for.
*/}}
{{- define "commerce.migrationPodLabels" -}}
app.kubernetes.io/name: {{ include "commerce.name" . }}-migrate
app.kubernetes.io/part-of: commerce
app.kubernetes.io/component: migrator
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ include "commerce.tag" . | trunc 63 | trimSuffix "-" | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | quote }}
{{- end -}}

{{- /*
The full label set for object metadata. Free to carry release-scoped and
version-scoped labels, because unlike the selector above nothing here is
immutable.
*/}}
{{- define "commerce.labels" -}}
{{ include "commerce.selectorLabels" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- /*
Truncated for the reason the Job name is (see _migration-job.tpl): a label
value may not exceed 63 characters, and the API server rejects the whole object
rather than the label. A commit SHA is 40 and never reaches it — this is the
guard for the day somebody tags a release something long, not an expectation
that they will.
*/}}
app.kubernetes.io/version: {{ include "commerce.tag" . | trunc 63 | trimSuffix "-" | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | quote }}
{{- end -}}

{{- /*
The non-secret half of §15.4's inventory: everything whose Kind column reads
Config, rendered into the ConfigMap the Deployment mounts with `envFrom`.

The split is §15.4's own and it is mechanical — **if the value contains a
credential, it is a Secret** — so the two halves of this file are that table
read down its Kind column, and a key in the wrong one is a password in a
ConfigMap or a plain string in a Secret.
*/}}
{{- define "commerce.config" -}}
Identity__Authority: {{ include "commerce.require" (list .Values.identity.authority "identity.authority is required for every host, the gateway included (§15.4) — AddJwtAuthentication reads it eagerly and throws naming the key, so an unset value is a pod that never starts.") | quote }}
OTEL_EXPORTER_OTLP_ENDPOINT: {{ include "commerce.require" (list .Values.observability.otlpEndpoint "observability.otlpEndpoint is required: UseOtlpExporter reads the OpenTelemetry standard variable, and left unset it exports to localhost:4317, where nothing listens in a pod (§15.4).") | quote }}
{{- if .Values.identity.clientCredentials }}
{{- /*
Two of the three client-credential keys are Config and only the secret is a
Secret (§15.4). They belong to the one host that calls a peer synchronously
(§9.7, ADR-017); all three are [Required] on ServiceIdentityOptions and gated
by ValidateOnStart, so a missing one is a refusal to boot rather than a 401
somebody reads as the callee's fault.

A second chart growing these is a design change, not a configuration change.

**The switch is its own key, and `clientId` used to be it.** That made the
opt-out invalid rather than merely odd: clearing `identity.clientId` on the BFF
dropped all three keys, and `Web.Bff` binds `ServiceIdentityOptions`
unconditionally — so the release rendered, rolled, and the pod refused to
start. A whitespace-only value did the same while slipping past
`commerce.require`, because a truthiness test is not a requirement. §15.4 calls
this the *required-for-some-hosts* category; an explicit boolean is what makes
a host say which it is, and every value below is then required rather than
implied.
*/}}
Identity__Client__ClientId: {{ include "commerce.require" (list .Values.identity.clientId "identity.clientId is required when identity.clientCredentials: Web.Bff binds ServiceIdentityOptions unconditionally and ValidateOnStart refuses to boot without it (§15.4).") | quote }}
Identity__Client__Scope: {{ include "commerce.require" (list .Values.identity.scope "identity.scope is required when identity.clientCredentials: it becomes the audience every service validates (§11.5), and ServiceIdentityOptions marks it [Required].") | quote }}
{{- end }}
{{- end -}}

{{- /*
The secret half of the same table, and the rule that decides what is in either:
a variable joins when a host's code READS it, and not before.

That is the rule §14.1's Compose blocks already state — "an env var nothing
reads is the container form of an unused registration" — and it is why there
are no Redis keys here. §15.4's inventory marks `ConnectionStrings__RedisCache`
and `ConnectionStrings__RedisCoordination` required *once a host reads a cache*,
and none does — nothing calls `AddRedisConnections` yet. Supplying them anyway
would demand two Secrets exist before any pod can start, for values nothing
reads. They join with the PR whose code reads them, exactly as
`Identity__Authority` joined with PR-16.

Secrets are REFERENCED, never rendered. External Secrets Operator owns the
Secret objects (§15.4); a chart that templated a connection string would put a
password into `helm get values` and into every diff of this repository.
*/}}
{{- define "commerce.env" -}}
{{- if .Values.database.enabled }}
{{- /*
The RUNTIME connection string (DML only) — §7.1's split identity. The migrator
key is the other half, mounted into the migration Job and nowhere else.
*/}}
- name: ConnectionStrings__{{ include "commerce.require" (list .Values.database.connectionName "database.connectionName is required when database.enabled: it is the .NET configuration key this service's Infrastructure passes to GetConnectionString (§7.1), and it differs per service.") }}
  valueFrom:
    secretKeyRef:
      name: {{ include "commerce.require" (list .Values.database.runtimeSecretRef.name "database.runtimeSecretRef.name is required when database.enabled.") | quote }}
      key: {{ include "commerce.require" (list .Values.database.runtimeSecretRef.key "database.runtimeSecretRef.key is required when database.enabled.") | quote }}
{{- end }}
{{- if .Values.broker.enabled }}
- name: ConnectionStrings__RabbitMq
  valueFrom:
    secretKeyRef:
      name: {{ include "commerce.require" (list .Values.broker.secretRef.name "broker.secretRef.name is required when broker.enabled: AddMassTransitMessaging throws without the connection string, so the host does not start (§9.3).") | quote }}
      key: {{ include "commerce.require" (list .Values.broker.secretRef.key "broker.secretRef.key is required when broker.enabled.") | quote }}
{{- end }}
{{- if .Values.identity.clientCredentials }}
- name: Identity__Client__ClientSecret
  valueFrom:
    secretKeyRef:
      name: {{ include "commerce.require" (list .Values.identity.clientSecretRef.name "identity.clientSecretRef.name is required when identity.clientCredentials. The secret is a reference, never a value (§15.3).") | quote }}
      key: {{ include "commerce.require" (list .Values.identity.clientSecretRef.key "identity.clientSecretRef.key is required when identity.clientCredentials.") | quote }}
{{- end }}
{{- end -}}
