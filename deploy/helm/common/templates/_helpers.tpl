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
{{- define "commerce.name" -}}
{{- required "workload.name is required: it is this deployable's Service name, and therefore the string the gateway's route file and the BFF's pricing hop dial (§10.2, §9.7)." .Values.workload.name -}}
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
{{- required "image.tag is required and values.yaml leaves it empty on purpose: a deploy that cannot name its image must fail rather than roll something nobody chose (§15.3). CI supplies it; a config-only deploy resolves the running tag first (§15.1)." .Values.image.tag -}}
{{- end -}}

{{- /*
The selector, which carries the workload name and NOTHING release-derived.

A Deployment's `.spec.selector` is immutable after creation, and the same
workload is installed two ways: standalone (`helm install catalog-api
deploy/helm/catalog`) and as a subchart of the umbrella, where `.Release.Name`
is `platform`. A selector holding the release name is therefore a field that
changes on exactly the migration an umbrella chart exists to perform, and the
error arrives from the API server as a rejected update rather than from helm.
*/}}
{{- define "commerce.selectorLabels" -}}
app.kubernetes.io/name: {{ include "commerce.name" . }}
app.kubernetes.io/part-of: commerce
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
Identity__Authority: {{ required "identity.authority is required for every host, the gateway included (§15.4) — AddJwtAuthentication reads it eagerly and throws naming the key, so an unset value is a pod that never starts." .Values.identity.authority | quote }}
OTEL_EXPORTER_OTLP_ENDPOINT: {{ required "observability.otlpEndpoint is required: UseOtlpExporter reads the OpenTelemetry standard variable, and left unset it exports to localhost:4317, where nothing listens in a pod (§15.4)." .Values.observability.otlpEndpoint | quote }}
{{- if .Values.identity.clientId }}
{{- /*
Two of the three client-credential keys are Config and only the secret is a
Secret (§15.4). They belong to the one host that calls a peer synchronously
(§9.7, ADR-017); all three are [Required] on ServiceIdentityOptions and gated
by ValidateOnStart, so a missing one is a refusal to boot rather than a 401
somebody reads as the callee's fault.

A second chart growing these is a design change, not a configuration change.
*/}}
Identity__Client__ClientId: {{ .Values.identity.clientId | quote }}
Identity__Client__Scope: {{ required "identity.scope is required whenever identity.clientId is set: it becomes the audience every service validates (§11.5), and ServiceIdentityOptions marks it [Required]." .Values.identity.scope | quote }}
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
- name: ConnectionStrings__{{ required "database.connectionName is required when database.enabled: it is the .NET configuration key this service's Infrastructure passes to GetConnectionString (§7.1), and it differs per service." .Values.database.connectionName }}
  valueFrom:
    secretKeyRef:
      name: {{ required "database.runtimeSecretRef.name is required when database.enabled." .Values.database.runtimeSecretRef.name | quote }}
      key: {{ required "database.runtimeSecretRef.key is required when database.enabled." .Values.database.runtimeSecretRef.key | quote }}
{{- end }}
{{- if .Values.broker.enabled }}
- name: ConnectionStrings__RabbitMq
  valueFrom:
    secretKeyRef:
      name: {{ required "broker.secretRef.name is required when broker.enabled: AddMassTransitMessaging throws without the connection string, so the host does not start (§9.3)." .Values.broker.secretRef.name | quote }}
      key: {{ required "broker.secretRef.key is required when broker.enabled." .Values.broker.secretRef.key | quote }}
{{- end }}
{{- if .Values.identity.clientId }}
- name: Identity__Client__ClientSecret
  valueFrom:
    secretKeyRef:
      name: {{ required "identity.clientSecretRef.name is required whenever identity.clientId is set. The secret is a reference, never a value (§15.3)." .Values.identity.clientSecretRef.name | quote }}
      key: {{ required "identity.clientSecretRef.key is required whenever identity.clientId is set." .Values.identity.clientSecretRef.key | quote }}
{{- end }}
{{- end -}}
