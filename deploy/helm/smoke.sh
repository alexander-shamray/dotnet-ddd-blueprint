#!/usr/bin/env bash
#
# Renders every chart under deploy/helm and asserts the claims §15.3, §15.4 and
# §7.4 make about what comes out. It is the Helm analogue of the Compose smoke
# (§15.1) and it is run the same way — by CI, on a path filter, and by a person
# before pushing a chart change.
#
# WHAT IT IS NOT: it deploys nothing and reaches no cluster. `helm template`
# renders locally, so this proves the charts produce the manifests the chapters
# describe and says nothing about whether a cluster would accept them. Schema
# validation against a live API server is a deploy-time gate (§15.1) and is
# named here as not covered rather than implied.
#
#   HELM=/path/to/helm bash deploy/helm/smoke.sh
#
# `helm` from PATH when HELM is unset. Requires helm 3.
set -euo pipefail

HELM="${HELM:-helm}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CHARTS_DIR="$ROOT/deploy/helm"
OUT="$(mktemp -d)"
trap 'rm -rf "$OUT"' EXIT

# Any tag will do — the point of supplying one is that `required` lets the
# render through. What happens WITHOUT one is asserted below, and is the more
# interesting half.
TAG="0000000000000000000000000000000000000000"

SERVICE_CHARTS="catalog ordering gateway web-bff"
MIGRATOR_CHARTS="catalog ordering"
DATABASELESS_CHARTS="gateway web-bff"

failures=0

pass() { printf '  ok   %s\n' "$1"; }

fail() {
    printf '  FAIL %s\n' "$1" >&2
    failures=$((failures + 1))
}

check() {
    # check <description> <condition-exit-code-producing-command...>
    local what="$1"
    shift
    if "$@" >/dev/null 2>&1; then
        pass "$what"
    else
        fail "$what"
    fi
}

# grep -c counts LINES, not matches, so every count below is a line count and
# the assertions are written to match one claim per line.
count() { grep -c "$1" "$2" 2>/dev/null || true; }

section() { printf '\n%s\n' "$1"; }

# --------------------------------------------------------------------------
section 'Resolving dependencies'
# --------------------------------------------------------------------------
# file:// dependencies resolve from disk, so this needs no network and no repo
# index. Order matters: a service chart must already hold commerce-common in
# its own charts/ before the umbrella packages it, or the umbrella renders a
# subchart whose library templates are missing.
for chart in $SERVICE_CHARTS; do
    "$HELM" dependency update "$CHARTS_DIR/$chart" --skip-refresh >/dev/null
    pass "$chart resolves commerce-common"
done
"$HELM" dependency update "$CHARTS_DIR/platform" --skip-refresh >/dev/null
pass 'platform resolves its four subcharts'

# --------------------------------------------------------------------------
section 'helm lint'
# --------------------------------------------------------------------------
for chart in $SERVICE_CHARTS platform; do
    check "$chart lints" "$HELM" lint "$CHARTS_DIR/$chart" --set-string "image.tag=$TAG" \
        --set-string "catalog.image.tag=$TAG" \
        --set-string "ordering.image.tag=$TAG" \
        --set-string "gateway.image.tag=$TAG" \
        --set-string "web-bff.image.tag=$TAG"
done

# --------------------------------------------------------------------------
section 'A deploy that cannot name its tag fails (§15.3)'
# --------------------------------------------------------------------------
# values.yaml leaves image.tag empty on purpose. Left to default it, the render
# would emit `image: registry/api:` and the kubelet resolves that to :latest —
# the one tag §15.3 forbids by name. This is the assertion that the empty
# default is a refusal rather than a hole.
for chart in $SERVICE_CHARTS; do
    if "$HELM" template "$chart" "$CHARTS_DIR/$chart" >"$OUT/untagged-$chart.txt" 2>&1; then
        fail "$chart renders WITHOUT a tag — it must not"
    elif grep -q 'image.tag is required' "$OUT/untagged-$chart.txt"; then
        pass "$chart refuses to render without a tag, and says which value is missing"
    else
        fail "$chart fails without a tag but the message does not name image.tag"
    fi
done

# --------------------------------------------------------------------------
section 'Rendering'
# --------------------------------------------------------------------------
for chart in $SERVICE_CHARTS; do
    "$HELM" template "$chart" "$CHARTS_DIR/$chart" --set-string "image.tag=$TAG" >"$OUT/$chart.yaml"
    pass "$chart renders"
done
"$HELM" template platform "$CHARTS_DIR/platform" \
    --set-string "catalog.image.tag=$TAG" \
    --set-string "ordering.image.tag=$TAG" \
    --set-string "gateway.image.tag=$TAG" \
    --set-string "web-bff.image.tag=$TAG" >"$OUT/platform.yaml"
pass 'platform renders'

# --------------------------------------------------------------------------
section 'Probes — three per workload (§13.5)'
# --------------------------------------------------------------------------
for chart in $SERVICE_CHARTS; do
    for probe in livenessProbe readinessProbe startupProbe; do
        check "$chart declares $probe" test "$(count "^ *$probe:" "$OUT/$chart.yaml")" -eq 1
    done
    for path in /health/live /health/ready /health/startup; do
        check "$chart probes $path" test "$(count "path: $path$" "$OUT/$chart.yaml")" -eq 1
    done
done

# --------------------------------------------------------------------------
section 'Resources — a memory limit and no CPU limit (§15.3)'
# --------------------------------------------------------------------------
# Memory is incompressible, so a leak must be bounded; CPU is compressible, and
# a limit throttles into p99 spikes well before the pod is short of capacity.
# awk rather than grep because the claim is about what is INSIDE a limits
# block: `cpu` appears legitimately under requests, and as a metric name on the
# HPA.
limits_without_cpu() {
    awk '
        /^[ ]*limits:[ ]*$/ { match($0, /[^ ]/); indent = RSTART; inside = 1; next }
        inside {
            match($0, /[^ ]/)
            if (RSTART <= indent) { inside = 0; next }
            if ($0 ~ /cpu:/) { print "cpu limit at line " NR; bad = 1 }
        }
        END { exit bad ? 1 : 0 }
    ' "$1"
}

for chart in $SERVICE_CHARTS platform; do
    check "$chart sets no CPU limit" limits_without_cpu "$OUT/$chart.yaml"
    # Every resources block has a limits block — "at least one" would pass on a
    # render where three containers of four were unbounded, which is the shape
    # this assertion exists to refuse.
    check "$chart bounds memory on every container that declares resources" \
        test "$(count '^ *limits:' "$OUT/$chart.yaml")" \
        -eq "$(count '^ *resources:' "$OUT/$chart.yaml")"
done

# --------------------------------------------------------------------------
section 'Autoscaling owns the replica count'
# --------------------------------------------------------------------------
# With the HPA on, `replicas` must be absent from the Deployment: present, every
# helm upgrade writes the chart's value and the autoscaler writes it back, so a
# config-only deploy (§15.1) silently scales the service down and it climbs out
# again over the following minutes.
for chart in $SERVICE_CHARTS; do
    check "$chart leaves replicas to its HPA" test "$(count '^ *replicas:' "$OUT/$chart.yaml")" -eq 0
    check "$chart renders an HPA" test "$(count '^kind: HorizontalPodAutoscaler$' "$OUT/$chart.yaml")" -eq 1
    check "$chart renders a PodDisruptionBudget" \
        test "$(count '^kind: PodDisruptionBudget$' "$OUT/$chart.yaml")" -eq 1
done

# --------------------------------------------------------------------------
section 'The grace period exceeds the host shutdown timeout'
# --------------------------------------------------------------------------
# HostOptions.ShutdownTimeout defaults to 30 s and nothing in this solution
# overrides it, so 30 is not a margin over 30 — a pod on the Kubernetes default
# is SIGKILLed at the instant the host would have finished draining.
for chart in $SERVICE_CHARTS; do
    grace="$(awk '/terminationGracePeriodSeconds:/ { print $2; exit }' "$OUT/$chart.yaml")"
    check "$chart grace period ($grace s) exceeds the 30 s shutdown timeout" test "${grace:-0}" -gt 30
done

# --------------------------------------------------------------------------
section 'Migration hook (§7.4, ADR-007)'
# --------------------------------------------------------------------------
for chart in $MIGRATOR_CHARTS; do
    check "$chart renders a migration Job" test "$(count '^kind: Job$' "$OUT/$chart.yaml")" -eq 1
    check "$chart runs it pre-install,pre-upgrade" \
        grep -q '"helm.sh/hook": pre-install,pre-upgrade' "$OUT/$chart.yaml"
    check "$chart weights the hook ahead of any other" \
        grep -q '"helm.sh/hook-weight": "-5"' "$OUT/$chart.yaml"
    check "$chart deletes the previous hook rather than accumulating them" \
        grep -q '"helm.sh/hook-delete-policy": before-hook-creation' "$OUT/$chart.yaml"
    check "$chart mounts the MIGRATOR connection string, not the runtime one" \
        grep -qE '^ *- name: ConnectionStrings__[A-Za-z]+Migrator$' "$OUT/$chart.yaml"
done

# The gateway and the BFF own no database (§10.1, §4.2), so the hook has
# nothing to run for them.
#
# THE OUTPUT ASSERTION ALONE IS VACUOUS, and it was, until a deliberate
# `image.migrator: gateway-migrator` passed the whole suite green. Those two
# charts carry no migration template at all — §15.3's argument that the
# gateway's chart is not a service chart with the database parts deleted — so
# nothing was ever consulting the values key the comment beside it credits.
#
# So the subject is the agreement between the two halves: a chart has a
# migration template exactly when its values name a migrator image. Broken from
# either side it fires, where the render check could only ever be broken from
# one.
for chart in $DATABASELESS_CHARTS; do
    check "$chart renders no migration Job" test "$(count '^kind: Job$' "$OUT/$chart.yaml")" -eq 0
    check "$chart mounts no connection string at all" \
        test "$(count 'ConnectionStrings__' "$OUT/$chart.yaml")" -eq 0
done

for chart in $SERVICE_CHARTS; do
    has_template=no
    [ -f "$CHARTS_DIR/$chart/templates/migrate-job.yaml" ] && has_template=yes
    has_image=no
    grep -qE '^ +migrator: ' "$CHARTS_DIR/$chart/values.yaml" && has_image=yes
    check "$chart: migration template ($has_template) and image.migrator ($has_image) agree" \
        test "$has_template" = "$has_image"
done

# --------------------------------------------------------------------------
section 'The ConfigMap/Secret split is §15.4 read down its Kind column'
# --------------------------------------------------------------------------
# The rule is mechanical: if the value contains a credential, it is a Secret. A
# connection string in a ConfigMap is a password readable by anyone with
# namespace read access and unencrypted at rest.
configmap_data_lines() {
    awk '
        /^kind: ConfigMap$/ { in_cm = 1 }
        /^---$/ { in_cm = 0 }
        in_cm && /^ *[A-Za-z_]+__[A-Za-z0-9_]*:/ { print }
    ' "$1"
}

configmap_data_lines "$OUT/platform.yaml" >"$OUT/configmap-keys.txt"
check 'no ConfigMap carries a connection string' \
    test "$(count 'ConnectionStrings__' "$OUT/configmap-keys.txt")" -eq 0
check 'no ConfigMap carries a client secret' \
    test "$(count 'Identity__Client__ClientSecret' "$OUT/configmap-keys.txt")" -eq 0
check 'every ConnectionStrings__ value comes from a secretKeyRef' \
    test "$(count 'ConnectionStrings__' "$OUT/platform.yaml")" \
    -eq "$(awk '/- name: ConnectionStrings__/ { want = 1; next } want && /secretKeyRef/ { n++; want = 0 } END { print n + 0 }' "$OUT/platform.yaml")"

# --------------------------------------------------------------------------
section 'Client credentials: exactly one chart (§11.5, §15.3)'
# --------------------------------------------------------------------------
# A second chart growing an identity.clientId is a design change, not a
# configuration change: it means a host started calling a peer synchronously,
# which is ADR-017's budget being spent.
check 'exactly one workload in the platform holds a client secret' \
    test "$(count 'Identity__Client__ClientSecret' "$OUT/platform.yaml")" -eq 1
check 'and it is the BFF' \
    test "$(count 'Identity__Client__ClientSecret' "$OUT/web-bff.yaml")" -eq 1

# --------------------------------------------------------------------------
section 'The edge keys belong to the gateway alone (§15.4)'
# --------------------------------------------------------------------------
for key in Ingress__Enabled Cors__Enabled Ingress__TrustedNetworks__0; do
    check "$key is rendered once across the platform" \
        test "$(count "$key" "$OUT/platform.yaml")" -eq 1
    check "$key is the gateway's" test "$(count "$key" "$OUT/gateway.yaml")" -eq 1
done

check 'the platform has exactly one Ingress' \
    test "$(count '^kind: Ingress$' "$OUT/platform.yaml")" -eq 1
# The backend, not merely a line saying `gateway` somewhere — the Deployment
# and the ConfigMap both carry that name, so a looser grep would pass on a
# render whose Ingress pointed at nothing at all.
check 'and it routes to the gateway Service' \
    awk '/^kind: Ingress$/ { in_ing = 1 } in_ing && /^ *service:$/ { want = 1; next } want && /name: gateway$/ { found = 1 } END { exit found ? 0 : 1 }' \
    "$OUT/platform.yaml"

# --------------------------------------------------------------------------
section 'Every ConfigMap an envFrom names is rendered by the same release'
# --------------------------------------------------------------------------
# The gateway mounts a second ConfigMap it renders itself, and the name is
# written in two places — values.yaml's extraEnvFrom and edge-config.yaml's
# metadata. This is the assertion that they agree; without it a rename in one
# is a pod stuck in CreateContainerConfigError.
awk '/configMapRef:/ { want = 1; next } want && /name:/ { sub(/^ *name: /, ""); print; want = 0 }' \
    "$OUT/platform.yaml" | sort -u >"$OUT/mounted.txt"
awk '/^kind: ConfigMap$/ { want = 1 } want && /^  name: / { sub(/^  name: /, ""); print; want = 0 }' \
    "$OUT/platform.yaml" | sort -u >"$OUT/rendered.txt"
check 'every mounted ConfigMap exists in the render' \
    test -z "$(comm -23 "$OUT/mounted.txt" "$OUT/rendered.txt")"

# ...and a change to ANY of them rolls the pods. This is the assertion the
# narrower version of that annotation failed: hashing only the ConfigMap the
# library renders left the gateway's own `gateway-edge` out, so editing
# `cors.origins` rewrote a mounted ConfigMap and left the pod template
# byte-identical — a deploy that reports success and changes nothing.
gateway_checksum() {
    "$HELM" template gateway "$CHARTS_DIR/gateway" --set-string "image.tag=$TAG" "$@" |
        awk '/checksum\/values:/ { print $2; exit }'
}
before="$(gateway_checksum)"
after="$(gateway_checksum --set cors.enabled=true --set 'cors.origins={https://shop.example.com}')"
check 'editing an edge-only value rolls the gateway pods' test "$before" != "$after"

# --------------------------------------------------------------------------
section 'Service names are routing configuration (§10.2, §9.7)'
# --------------------------------------------------------------------------
# The gateway's route file and PricingHop.cs dial these hosts as LITERALS in
# source, and PricingHop argues on the record that the value does not vary
# because "the host is the Kubernetes Service name". This is the assertion that
# keeps that sentence true.
#
# One direction only, and deliberately: every Service this platform renders
# must be a name something in src/ dials, plus the gateway — which is dialled
# by the Ingress rather than by a peer. The other direction is NOT asserted,
# because §10.2's route file deliberately names inventory ahead of the service
# that will answer it, and a gate demanding a chart per destination would fail
# on a route the blueprint means to be there.
grep -ohE 'http://[a-z0-9-]+:[0-9]+' \
    "$ROOT/src/Gateway/Gateway.Api/appsettings.json" \
    "$ROOT/src/BFF/Web.Bff/PricingHop.cs" |
    sed -E 's|http://([a-z0-9-]+):[0-9]+|\1|' | sort -u >"$OUT/dialled.txt"
echo gateway >>"$OUT/dialled.txt"
sort -u -o "$OUT/dialled.txt" "$OUT/dialled.txt"

awk '/^kind: Service$/ { want = 1 } want && /^  name: / { sub(/^  name: /, ""); print; want = 0 }' \
    "$OUT/platform.yaml" | sort -u >"$OUT/services.txt"
undialled="$(comm -23 "$OUT/services.txt" "$OUT/dialled.txt")"
if [ -z "$undialled" ]; then
    pass 'every rendered Service is a name src/ dials'
else
    fail "Service(s) nothing in src/ dials: $(echo "$undialled" | tr '\n' ' ')"
fi

# The ports are literals in the same two files, so they are asserted the same
# way rather than trusted.
check 'catalog answers the pricing hop on 8081' \
    grep -q 'containerPort: 8081' "$OUT/catalog.yaml"

# --------------------------------------------------------------------------
section 'Values that must agree across charts'
# --------------------------------------------------------------------------
# §15.3 gives each chart its own values file, so a platform-wide value is
# written four times. That is the chapter's design and it is also a drift
# risk — converted here into a gated invariant rather than left to review.
for key in Identity__Authority OTEL_EXPORTER_OTLP_ENDPOINT; do
    distinct="$(grep -h "^ *$key:" "$OUT/platform.yaml" | sed 's/^ *//' | sort -u | wc -l)"
    check "$key has one value across every chart (found $distinct)" test "$distinct" -eq 1
done

# --------------------------------------------------------------------------
section 'Branches no chart takes yet, exercised anyway'
# --------------------------------------------------------------------------
# §15.3 specifies `service.enabled: false` for Shipping and Notifications, and
# neither exists yet — so without this the branch would ship untested and the
# key would be decorative in every chart that sets it. Rendering one chart with
# the value flipped is what makes the key mean something today.
"$HELM" template ordering "$CHARTS_DIR/ordering" --set-string "image.tag=$TAG" \
    --set service.enabled=false >"$OUT/worker.yaml"
check 'service.enabled=false renders no Service' \
    test "$(count '^kind: Service$' "$OUT/worker.yaml")" -eq 0
check 'and the workload, its probes and its migration hook survive' \
    test "$(count '^kind: Deployment$' "$OUT/worker.yaml")" -eq 1
check 'and the probes still address the container port directly' \
    test "$(count 'path: /health/ready$' "$OUT/worker.yaml")" -eq 1

# Conditionally required is a real category (§15.4): off is a valid topology,
# on-but-unconfigured is a silent defect. The gateway's own startup guards
# catch it, and catching it at render says which chart value is missing.
if "$HELM" template gateway "$CHARTS_DIR/gateway" --set-string "image.tag=$TAG" \
    --set 'ingress.trustedNetworks=null' >"$OUT/untrusted.txt" 2>&1; then
    fail 'ingress.enabled with no trustedNetworks renders — it must not'
else
    check 'ingress.enabled with no trustedNetworks fails the render' \
        grep -q 'ingress.trustedNetworks must hold at least one CIDR' "$OUT/untrusted.txt"
fi

if "$HELM" template gateway "$CHARTS_DIR/gateway" --set-string "image.tag=$TAG" \
    --set cors.enabled=true >"$OUT/uncorsed.txt" 2>&1; then
    fail 'cors.enabled with no origins renders — it must not'
else
    check 'cors.enabled with no origins fails the render' \
        grep -q 'cors.origins must hold at least one origin' "$OUT/uncorsed.txt"
fi

# --------------------------------------------------------------------------
section 'Result'
# --------------------------------------------------------------------------
if [ "$failures" -ne 0 ]; then
    printf '%s assertion(s) failed\n' "$failures" >&2
    exit 1
fi
printf 'all assertions passed\n'
