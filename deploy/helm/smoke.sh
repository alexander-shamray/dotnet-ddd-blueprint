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

# The gateway's chart ships `ingress.trustedNetworks: []` on purpose: a
# plausible CIDR is an active security decision taken on behalf of a cluster
# nobody has seen (§15.3). Every render here therefore plays the part of the
# environment overlay, and each of the negative tests below supplies it too —
# so that a test asserting "no tag is refused" fails on the tag and not on
# something else it forgot to set. An assertion that passes for the wrong
# reason is the failure this file keeps finding in itself.
CIDR='{10.42.0.0/16}'
GATEWAY_OVERLAY="--set ingress.trustedNetworks=$CIDR"
PLATFORM_OVERLAY="--set gateway.ingress.trustedNetworks=$CIDR"

SERVICE_CHARTS="catalog ordering gateway web-bff"
MIGRATOR_CHARTS="catalog ordering"
DATABASELESS_CHARTS="gateway web-bff"

# The lists above are classifications and stay written down — which chart owns
# a database is a fact about the platform, not something to infer. What must
# NOT be written down is the membership, and until Copilot said so it was: a
# fifth chart directory could be added and every check in this file would skip
# it silently while the run still said "all assertions passed".
#
# That is this repository's most-repeated failure and this branch's own lesson
# for the fourth time — a gate that quietly stops covering the newest surface.
# So the directory is the authority and the lists are reconciled against it
# below, before anything is rendered.
discovered_charts() {
    for d in "$CHARTS_DIR"/*/; do
        name="$(basename "$d")"
        [ "$name" = common ] && continue      # the library chart renders nothing
        [ "$name" = platform ] && continue    # the umbrella has no templates
        [ -f "$d/Chart.yaml" ] && echo "$name"
    done | sort
}

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
section 'The gate covers every chart on disk'
# --------------------------------------------------------------------------
# First, because every section below iterates SERVICE_CHARTS: a chart missing
# from that list is not a weaker run, it is an unrun one that still reports
# success.
found="$(discovered_charts | tr '\n' ' ' | sed 's/ *$//')"
listed="$(printf '%s\n' $SERVICE_CHARTS | sort | tr '\n' ' ' | sed 's/ *$//')"
if [ "$found" = "$listed" ]; then
    pass "SERVICE_CHARTS is every deployable chart on disk ($found)"
else
    fail "SERVICE_CHARTS ($listed) does not match the chart directories ($found)"
fi

# And the two sub-classifications partition it, so a chart cannot be in the
# suite while belonging to neither — which is how it would reach the migration
# section and be checked by nothing there.
both="$(printf '%s\n' $MIGRATOR_CHARTS $DATABASELESS_CHARTS | sort | tr '\n' ' ' | sed 's/ *$//')"
if [ "$both" = "$listed" ]; then
    pass 'every chart is classified as owning a database or not'
else
    fail "MIGRATOR_CHARTS + DATABASELESS_CHARTS ($both) do not partition SERVICE_CHARTS ($listed)"
fi

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
        --set-string "web-bff.image.tag=$TAG" \
        $GATEWAY_OVERLAY $PLATFORM_OVERLAY
done

# --------------------------------------------------------------------------
section 'A deploy that cannot name its tag fails (§15.3)'
# --------------------------------------------------------------------------
# values.yaml leaves image.tag empty on purpose. Left to default it, the render
# would emit `image: registry/api:` and the kubelet resolves that to :latest —
# the one tag §15.3 forbids by name. This is the assertion that the empty
# default is a refusal rather than a hole.
for chart in $SERVICE_CHARTS; do
    if "$HELM" template "$chart" "$CHARTS_DIR/$chart" $GATEWAY_OVERLAY \
        >"$OUT/untagged-$chart.txt" 2>&1; then
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
    "$HELM" template "$chart" "$CHARTS_DIR/$chart" --set-string "image.tag=$TAG" \
        $GATEWAY_OVERLAY >"$OUT/$chart.yaml"
    pass "$chart renders"
done
"$HELM" template platform "$CHARTS_DIR/platform" \
    --set-string "catalog.image.tag=$TAG" \
    --set-string "ordering.image.tag=$TAG" \
    --set-string "gateway.image.tag=$TAG" \
    --set-string "web-bff.image.tag=$TAG" \
    $PLATFORM_OVERLAY >"$OUT/platform.yaml"
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
    # BOTH policies. `before-hook-creation` matches on NAME and the name embeds
    # the tag, so on its own every new SHA leaves its completed Job behind for
    # ever — and §13.6's runbook then looks for the failed one in a list of
    # every migration that ever succeeded. `hook-failed` is deliberately absent:
    # the failed Job is the artefact that runbook needs.
    check "$chart deletes the previous hook rather than accumulating them" \
        grep -q '"helm.sh/hook-delete-policy": before-hook-creation,hook-succeeded' "$OUT/$chart.yaml"
    check "$chart keeps a FAILED migration Job for the runbook" \
        test "$(count 'hook-failed' "$OUT/$chart.yaml")" -eq 0
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
    "$HELM" template gateway "$CHARTS_DIR/gateway" --set-string "image.tag=$TAG" \
        $GATEWAY_OVERLAY "$@" |
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
# The host AND the port, because both are literals and both are dialled. An
# earlier version stripped the port here and then hard-coded Catalog's two
# below, which asserted one destination of three and read as though it covered
# them all.
grep -ohE 'http://[a-z0-9-]+:[0-9]+' \
    "$ROOT/src/Gateway/Gateway.Api/appsettings.json" \
    "$ROOT/src/BFF/Web.Bff/PricingHop.cs" |
    sed -E 's|http://([a-z0-9-]+):([0-9]+)|\1 \2|' | sort -u >"$OUT/pairs.txt"

cut -d' ' -f1 "$OUT/pairs.txt" | sort -u >"$OUT/dialled.txt"
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
# way rather than trusted — which means the SERVICE port, not the container
# port. `PricingHop.cs` dials `http://catalog-api:8081`, and what answers that
# is `spec.ports[].port` on the Service; `containerPort` is the Deployment's
# and is not what the BFF resolves.
#
# TWO ROUNDS OF THE SAME HOLE, and the second is why this is a loop. Round 3
# found the check reading `containerPort`, so a `_service.tpl` publishing only
# the `http` port would pass while the hop 502'd. Round 5 found the fix
# hard-coded to `catalog-api`: `_service.tpl` takes `port` from PER-CHART
# values, so an `ordering/values.yaml` renumbering its port failed nothing —
# one destination of three asserted, by a comment that read as though it
# covered them all. Every pair the name gate parses is now a row.
service_port() {
    # <file> <service name> <port> -> exit 0 when that Service publishes it
    awk -v want="$2" -v port="$3" '
        /^kind: Service$/ { in_svc = 1; named = 0; next }
        /^---$/ { in_svc = 0; named = 0; next }
        in_svc && $0 ~ ("^  name: " want "$") { named = 1 }
        named && $0 ~ ("^ *- port: " port "$") { found = 1 }
        named && $0 ~ ("^ *port: " port "$") { found = 1 }
        END { exit found ? 0 : 1 }
    ' "$1"
}

while read -r host port; do
    if grep -qx "$host" "$OUT/services.txt"; then
        check "$host answers on Service port $port" \
            service_port "$OUT/platform.yaml" "$host" "$port"
    else
        # Not a silent cap: §10.2's route file deliberately names a
        # destination ahead of the service that will answer it, so a host with
        # no chart is expected — and saying which one keeps that expectation
        # from quietly absorbing a chart somebody forgot to add.
        pass "$host:$port dialled by src/, no chart yet — not asserted"
    fi
done <"$OUT/pairs.txt"

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
check 'and the workload survives' \
    test "$(count '^kind: Deployment$' "$OUT/worker.yaml")" -eq 1
# Named separately rather than folded into the line above, which used to say
# "and its migration hook survive" while counting Deployments alone. A
# description is a claim about what the command looks at.
check 'and so does its migration hook' \
    test "$(count '^kind: Job$' "$OUT/worker.yaml")" -eq 1
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
    $GATEWAY_OVERLAY --set cors.enabled=true >"$OUT/uncorsed.txt" 2>&1; then
    fail 'cors.enabled with no origins renders — it must not'
else
    check 'cors.enabled with no origins fails the render' \
        grep -q 'cors.origins must hold at least one origin' "$OUT/uncorsed.txt"
fi

# BLANK COUNTS AS MISSING, and an emptiness check does not see it. A list
# holding `" "` is truthy in a template, so both guards above passed it
# through, the value was rendered blank, and the host threw at startup — after
# the rollout had begun. This repository learned that against
# `Identity__Authority` and again against `Cors__Origins`; these two are the
# assertions that keep it learned.
refuses() {
    # refuses <label> <needle> <helm args...>
    local label="$1" needle="$2"
    shift 2
    if "$HELM" template gateway "$CHARTS_DIR/gateway" --set-string "image.tag=$TAG" \
        "$@" >"$OUT/blank.txt" 2>&1; then
        fail "$label — it rendered instead"
    else
        check "$label" grep -q "$needle" "$OUT/blank.txt"
    fi
}

refuses 'a blank trusted network fails the render' 'is blank' \
    --set 'ingress.trustedNetworks={ }'
refuses 'a blank CORS origin fails the render' 'is blank' \
    $GATEWAY_OVERLAY --set cors.enabled=true --set 'cors.origins={ }'
refuses 'a CORS origin with a trailing path fails the render' 'is not a browser origin' \
    $GATEWAY_OVERLAY --set cors.enabled=true --set 'cors.origins={https://shop.example.com/app}'

# The Ingress backend is this workload's Service, so the two keys are not
# independent — and the inconsistent pair is what a copied values file
# produces when a worker's Service is turned off and the edge's Ingress is
# left on. It installs cleanly and answers 503 for every request.
refuses 'an Ingress with no Service fails the render' 'ingress.enabled requires service.enabled' \
    $GATEWAY_OVERLAY --set service.enabled=false

# TLS terminates at the Ingress (§10.1) and every hop past it is plain http on
# that premise — including §9.7's. An overlay clearing it rendered a valid
# plaintext Ingress and falsified the premise silently.
refuses 'an Ingress with no TLS fails the render' 'ingress.tls is required' \
    $GATEWAY_OVERLAY --set 'ingress.tls=null'

# --------------------------------------------------------------------------
section 'Defaults that must stay absent'
# --------------------------------------------------------------------------
# Every guard above is a property of the RENDER, and this one cannot be: a
# chart shipping a plausible `trustedNetworks` renders perfectly well, which is
# precisely why the value is dangerous. Restoring the default was the one
# deliberate defect of this round that no assertion caught, so the assertion is
# about the values file itself.
#
# Wrong low, the real ingress is untrusted and §10.3's per-client rate limit
# collapses into one global bucket. Wrong high, any pod in the range picks its
# own partition and its own client IP in the logs. Neither shows up in a
# rollout, so the only safe default is none.
check 'the gateway ships no default trusted network' \
    grep -qE '^  trustedNetworks: \[\]' "$CHARTS_DIR/gateway/values.yaml"

# --------------------------------------------------------------------------
section 'Blank is not present, whatever `required` thinks'
# --------------------------------------------------------------------------
# Helm's `required` fails on nil and on "" and passes `" "`. The hosts disagree
# — AddJwtAuthentication guards with IsNullOrWhiteSpace — so a whitespace-only
# overlay rendered cleanly, began a rollout, and died in the new pod. Every
# required scalar now goes through `commerce.require`, which trims first; these
# assert the two that reach a host eagerly.
refuses 'a whitespace-only authority fails the render' 'identity.authority is required' \
    $GATEWAY_OVERLAY --set-string 'identity.authority= '
refuses 'a whitespace-only OTLP endpoint fails the render' 'observability.otlpEndpoint is required' \
    $GATEWAY_OVERLAY --set-string 'observability.otlpEndpoint=   '
refuses 'a whitespace-only workload name fails the render' 'workload.name is required' \
    $GATEWAY_OVERLAY --set-string 'workload.name= '

# The origin guard has to reject what Program.cs rejects, or it is theatre:
# each of these renders, begins a rollout, and crashes the new pod otherwise.
refuses 'an origin carrying userinfo fails the render' 'is not a browser origin' \
    $GATEWAY_OVERLAY --set cors.enabled=true --set 'cors.origins={https://user:pass@shop.example.com}'
refuses 'an origin carrying a query fails the render' 'is not a browser origin' \
    $GATEWAY_OVERLAY --set cors.enabled=true --set 'cors.origins={https://shop.example.com?x}'
refuses 'an origin naming the default port fails the render' 'default port' \
    $GATEWAY_OVERLAY --set cors.enabled=true --set 'cors.origins={https://shop.example.com:443}'

# --------------------------------------------------------------------------
section 'No pod carries a cluster credential it never uses'
# --------------------------------------------------------------------------
# Nothing here calls the Kubernetes API, and omitting the field mounts the
# namespace's default service-account token anyway — so an application
# compromise also hands over a cluster credential. It matters most on the
# migration Job, which holds the one identity with DDL rights (§7.1).
check 'every pod template disables the service-account token' \
    test "$(count 'automountServiceAccountToken: false' "$OUT/platform.yaml")" \
    -eq "$(( $(count '^kind: Deployment$' "$OUT/platform.yaml") + $(count '^kind: Job$' "$OUT/platform.yaml") ))"

# --------------------------------------------------------------------------
section 'Result'
# --------------------------------------------------------------------------
if [ "$failures" -ne 0 ]; then
    printf '%s assertion(s) failed\n' "$failures" >&2
    exit 1
fi
printf 'all assertions passed\n'
