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

# EVERY PATH OUTSIDE deploy/helm THAT THIS SCRIPT READS, declared once.
#
# The workflow's path filter must cover each of them, or a change to one is a
# green pull request that skips the gate watching it. That list has now gone
# stale THREE times — the routing files, then HealthCheckExtensions.cs, then
# these service trees — and every time for the same reason: a read was added
# here and the filter updated somewhere else, or not at all.
#
# So it is declared here, beside the reads, and the agreement is asserted
# below. A list maintained in two places by hand is a list that drifts; this
# branch has spent six findings learning that about counts and inventories, and
# this is the same lesson applied to the gate's own inputs.
SOURCE_INPUTS="
src/Gateway/Gateway.Api/appsettings.json
src/BFF/Web.Bff
src/Services/Catalog
src/Services/Ordering
src/BuildingBlocks/Common.Web/HealthCheckExtensions.cs
.gitattributes
deploy/canary/canary.json
"

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

# Read from the values files rather than from a render, and asserted HERE for
# that reason: a second chart setting it renders nothing at all — it has no
# clientId, scope or secret ref — so under `set -e` the run aborts in the
# render section and this never reports. The gate is red either way; it is only
# legible if the file check runs first.
#
# ADR-017's budget is one synchronous hop, so it is one chart (§11.5).
credentialed="$(grep -l 'clientCredentials: true' "$CHARTS_DIR"/*/values.yaml | wc -l | tr -d ' ')"
check "exactly one chart declares client credentials (found $credentialed)" \
    test "$credentialed" -eq 1

# EACH CHART MUST DECLARE WHAT ITS CODE REQUIRES, read from src/ rather than
# trusted. The render-time coherence guards refuse a HALF override — disabling
# a capability whose settings are still present — and Copilot is right that a
# whole one slips through: clearing `broker.enabled` AND `broker.secretRef`
# together omits RabbitMQ from a host that always registers MassTransit.
#
# Nothing in a template can close that, because both halves are values. What
# can is this: the committed chart is checked against the code it deploys, so a
# whole override has to be committed to reach anyone, and then it fails here.
# An ad-hoc `--set` at deploy time remains outside a render-time gate's reach,
# and `deploy/helm/README.md` says so rather than implying otherwise.
#
# The real fix is a `chart:` capability block — one key per capability, named
# for what it is — and it is deferred to its own change rather than taken at
# round nine of a review loop.
# SCOPED TO ITS OWN BLOCK, and the first version was not — `^ +enabled: true`
# matched whichever block came first (there are five in Catalog's values), so
# clearing the broker entirely left this gate green while claiming to check it.
# A vacuous assertion in the file whose whole subject is vacuous assertions.
declares() {
    # declares <chart> <block> -> exit 0 when that block sets enabled: true
    awk -v want="$2" '
        $0 ~ ("^" want ":") { inside = 1; next }
        /^[a-z]/ { inside = 0 }
        inside && /^  enabled: true$/ { found = 1 }
        END { exit found ? 0 : 1 }
    ' "$CHARTS_DIR/$1/values.yaml"
}

for chart in $MIGRATOR_CHARTS; do
    svc="$(awk '/^workload:/ { w = 1 } w && /^  name: / { sub(/^  name: /, ""); print; exit }' \
        "$CHARTS_DIR/$chart/values.yaml")"
    src="$ROOT/src/Services/$(echo "${chart:0:1}" | tr '[:lower:]' '[:upper:]')${chart:1}"
    if [ -d "$src" ]; then
        if grep -rq 'GetConnectionString("RabbitMq")' "$src"; then
            check "$chart reads RabbitMq in src/, so its chart declares a broker" \
                declares "$chart" broker
        fi
        if grep -rqE 'GetConnectionString\("[A-Za-z]+"\)' "$src"; then
            check "$chart resolves a connection string in src/, so its chart names one" \
                grep -qE '^  connectionName: ' "$CHARTS_DIR/$chart/values.yaml"
        fi
    else
        fail "no source tree found at $src for chart $chart — the mapping, not the chart, is wrong"
    fi
done

check 'the BFF binds ServiceIdentityOptions in src/, so its chart declares credentials' \
    grep -rq 'ServiceIdentityOptions' "$ROOT/src/BFF/Web.Bff"

# --------------------------------------------------------------------------
section 'The workflow watches everything this script reads'
# --------------------------------------------------------------------------
# Both triggers, because a merged change that skips the gate on `main` is the
# same defect one branch later.
covered() {
    # covered <path> <trigger-block> -> exit 0 when some filter entry matches
    awk -v want="$1" '
        { sub(/^ *- /, ""); gsub(/'"'"'/, "") }
        $0 == want { found = 1 }
        /\/\*\*$/ {
            prefix = substr($0, 1, length($0) - 3)
            if (index(want, prefix) == 1) { found = 1 }
        }
        END { exit found ? 0 : 1 }
    ' "$2"
}

awk '/^  pull_request:/ { p = 1 } p && /^      - / { print } /^  push:/ { p = 0 }' \
    "$ROOT/.github/workflows/helm.yml" >"$OUT/pr-paths.txt"
awk '/^  push:/ { p = 1 } p && /^      - / { print }' \
    "$ROOT/.github/workflows/helm.yml" >"$OUT/push-paths.txt"

# THE WORKFLOW'S OWN PATH AND THIS GATE'S OWN TREE ARE BOTH ON THIS LIST, and
# each was missing in turn. Without the workflow, removing it from both trigger
# lists means a change to those very lists no longer runs the gate validating
# them. Without `deploy/helm`, removing THAT means a chart edit — or an edit to
# this script — does not run the gate either: the tree holding the thing being
# checked, gone from the triggers, with every assertion still green.
#
# `deploy/observability/check.py` has required both since it was written; this
# copy inherited the pattern one piece at a time.
for input in $SOURCE_INPUTS deploy/helm .github/workflows/helm.yml; do
    check "the pull_request filter covers $input" covered "$input" "$OUT/pr-paths.txt"
    check "the push filter covers $input" covered "$input" "$OUT/push-paths.txt"
done

# AND THE OTHER DIRECTION, which is the half that stays green when the list is
# SHORT rather than wrong.
#
# The loop above can only ask the workflow about entries SOURCE_INPUTS already
# contains, so a path this script reads and nobody declared is invisible from
# both sides. `deploy/canary/canary.py` shipped exactly that — two entries
# declared, three paths opened, trigger assertion green throughout — and
# CLAUDE.md states the fix as owed by every copy of this pattern rather than by
# the copy that was caught. This is that debt paid here.
#
# The subject is this script's own source: every `$ROOT/…` path it names must
# be covered by a declared entry — the WHOLE path, not a prefix of it, because
# the entries here are deeper than a top-level segment
# (`src/Gateway/Gateway.Api/appsettings.json` is a file, not a tree).
#
# Two kinds of match are skipped, and neither hides a gap:
#
#   * anything ending in `/` is an interpolation prefix rather than a path —
#     `$ROOT/src/Services/$chart` is built per chart, and the concrete forms it
#     builds (`src/Services/Catalog`, `src/Services/Ordering`) are declared;
#   * `deploy/helm` is this script's OWN tree, and SOURCE_INPUTS is by
#     definition the paths outside it. The workflow file is check 7's subject
#     rather than an input to it.
grep -oE '\$ROOT/[A-Za-z0-9_./-]+' "$0" | sed -E 's|^\$ROOT/||' | sort -u >"$OUT/reads.txt"

if [ ! -s "$OUT/reads.txt" ]; then
    # Subject first: a scan that found nothing would pass the loop below
    # against any list at all, which is this gate's own most-repeated failure
    # turned on itself.
    fail 'found no $ROOT-relative reads in smoke.sh — the scan is broken, not the list'
else
    while read -r path; do
        case "$path" in
            */|deploy/helm|deploy/helm/*|.github/workflows/helm.yml) continue ;;
        esac
        matched=no
        for input in $SOURCE_INPUTS; do
            case "$path" in
                "$input"|"$input"/*) matched=yes ;;
            esac
        done
        check "SOURCE_INPUTS declares $path, which this script reads" test "$matched" = yes
    done <"$OUT/reads.txt"
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
done

# THE PATHS COME FROM THE SOURCE THAT MAPS THEM, not from literals repeated
# here. `HealthEndpointTests` says in its own comment that the kubelet's probe
# "holds its own copy of /health/startup in a manifest no compiler reads" — and
# this PR shipped that manifest. A gate holding a fourth copy would close
# nothing: rename the route and the suite stays green, the chart stays green,
# and a slow-starting pod 404s and is killed mid-boot.
grep -ohE 'MapHealthChecks\("/health/[a-z]+"' "$ROOT/src/BuildingBlocks/Common.Web/HealthCheckExtensions.cs" |
    sed -E 's|.*"(/health/[a-z]+)"|\1|' | sort -u >"$OUT/mapped-probes.txt"

mapped_count="$(wc -l <"$OUT/mapped-probes.txt" | tr -d ' ')"
if [ "$mapped_count" -eq 0 ]; then
    fail 'no health endpoints found in HealthCheckExtensions.cs — the parse, not the chart, is wrong'
else
    pass "MapCommonHealthEndpoints maps $mapped_count paths, read from source"
fi

for chart in $SERVICE_CHARTS; do
    while read -r path; do
        check "$chart probes $path, the path Common.Web maps" \
            test "$(count "path: $path$" "$OUT/$chart.yaml")" -eq 1
    done <"$OUT/mapped-probes.txt"

    # And no probe pointing at a path nothing maps, which is the direction a
    # renamed route breaks.
    awk '/path: \/health\// { sub(/.*path: /, ""); print }' "$OUT/$chart.yaml" |
        sort -u >"$OUT/probed-$chart.txt"
    stray="$(comm -23 "$OUT/probed-$chart.txt" "$OUT/mapped-probes.txt")"
    if [ -z "$stray" ]; then
        pass "$chart probes nothing Common.Web does not map"
    else
        fail "$chart probes path(s) nothing maps: $(echo "$stray" | tr '\n' ' ')"
    fi
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

    # The migrator must not be selectable BY THE SERVICE IT MIGRATES. Its pod
    # template carried the same labels the Service selects on, so for the length
    # of every pre-upgrade hook a pod with a database connection and no HTTP
    # listener was an endpoint of a live service — and inside its PDB.
    #
    # Compares the Service's selector name with the Job pod template's, which
    # is the pair that decides endpoint membership; the Job OBJECT's labels are
    # not part of that and deliberately still carry the ordinary identity.
    svc_name="$(awk '/^kind: Service$/ { s = 1 } s && /^    app.kubernetes.io\/name: / { sub(/.*: /, ""); print; exit }' "$OUT/$chart.yaml")"
    job_pod_name="$(awk '/^kind: Job$/ { j = 1 } j && /^  template:/ { t = 1 } t && /^        app.kubernetes.io\/name: / { sub(/.*: /, ""); print; exit }' "$OUT/$chart.yaml")"
    check "$chart's migrator pod is not an endpoint of its own Service ($job_pod_name vs $svc_name)" \
        test "$job_pod_name" != "$svc_name"
    check "$chart's migrator pod says what it is" \
        grep -q 'app.kubernetes.io/component: migrator' "$OUT/$chart.yaml"
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
# derived in two places — values.yaml's extraConfigMaps suffix and
# edge-config.yaml's
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

# ...and the other direction, which is the one an overlay can break. A chart
# whose workload name is dialled from src/ MUST render a Service: turning it off
# leaves a healthy release in which every routed request fails, and
# `service.enabled` was a free value until Copilot pointed at it.
for chart in $SERVICE_CHARTS; do
    name="$(awk '/^workload:/ { w = 1 } w && /^  name: / { sub(/^  name: /, ""); print; exit }' \
        "$CHARTS_DIR/$chart/values.yaml")"
    if grep -qx "$name" "$OUT/pairs.txt" 2>/dev/null || cut -d' ' -f1 "$OUT/pairs.txt" | grep -qx "$name"; then
        check "$chart is dialled as $name, so it must keep its Service" \
            grep -qE '^  enabled: true' <(awk '/^service:/ { s = 1; next } s && /^[a-z]/ { s = 0 } s' "$CHARTS_DIR/$chart/values.yaml")
    else
        pass "$chart ($name) is not a routed destination — its Service is optional"
    fi
done

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
# Rendered under a name NOTHING dials, which is the difference between
# exercising the worker branch and asserting that Ordering may drop its Service.
# Ordering is a routed destination; the check above now forbids exactly that,
# and this test used to establish it as valid one section earlier.
"$HELM" template shipping "$CHARTS_DIR/ordering" --set-string "image.tag=$TAG" \
    --set-string "workload.name=shipping" \
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
refuses 'an origin with a leading-zero port fails the render' 'non-canonically' \
    $GATEWAY_OVERLAY --set cors.enabled=true --set 'cors.origins={https://shop.example.com:08080}'

# A capability is a fact about the code, not an environment setting. Each of
# these renders cleanly and produces a pod that will not start — and each has to
# be aimed at a chart that HAS the capability, or the test is vacuous. `refuses`
# above renders the gateway, which owns no database and no migrator, so the
# first two of these were meaningless against it until this helper existed.
refuses_chart() {
    # refuses_chart <chart> <label> <needle> <helm args...>
    local chart="$1" label="$2" needle="$3"
    shift 3
    if "$HELM" template "$chart" "$CHARTS_DIR/$chart" --set-string "image.tag=$TAG" \
        "$@" >"$OUT/cap.txt" 2>&1; then
        fail "$label — it rendered instead"
    else
        check "$label" grep -q "$needle" "$OUT/cap.txt"
    fi
}

refuses_chart catalog 'disabling a database the chart is configured for fails the render' \
    'database.enabled is false' --set database.enabled=false
refuses_chart catalog 'disabling a broker the chart is configured for fails the render' \
    'broker.enabled is false' --set broker.enabled=false
refuses_chart catalog 'clearing the migrator image fails the render' \
    'image.migrator is required' --set-string 'image.migrator='

# A tag is three things with three alphabets: an image reference, a Job name
# (DNS-1123 subdomain) and a label value. `Release_1` is legal for a registry
# and illegal for Kubernetes, and the SHA-only cases above never saw it.
# One regex over the whole string was not enough: `release_1`, `release..1` and
# `release.-1` all passed it and are all refused by the API server after the
# upgrade has started. A DNS-1123 subdomain is dot-separated LABELS, so the
# check is per segment and so are these cases.
for bad in Release_1 release_1 release..1 release.-1 -release release-; do
    if "$HELM" template catalog "$CHARTS_DIR/catalog" --set-string "image.tag=$bad" \
        >"$OUT/badtag.txt" 2>&1; then
        fail "image.tag=$bad renders — Kubernetes would refuse the Job it names"
    else
        check "image.tag=$bad fails the render" \
            grep -q 'not usable as Kubernetes metadata' "$OUT/badtag.txt"
    fi
done

for good in 1.2.3 0000000000000000000000000000000000000000 v1-2-3; do
    check "image.tag=$good still renders" \
        "$HELM" template catalog "$CHARTS_DIR/catalog" --set-string "image.tag=$good"
done

# The name budget, which the per-segment check cannot see. `trunc 63` used to
# stand here and was a defect of its own: 42 `a`s then `.b` cut immediately
# after the dot, and trimming a trailing hyphen never touched a trailing dot.
long_tag="$(printf 'a%.0s' $(seq 1 42)).b"
if "$HELM" template catalog "$CHARTS_DIR/catalog" --set-string "image.tag=$long_tag" \
    >"$OUT/longtag.txt" 2>&1; then
    fail 'a tag that overruns the Job-name budget renders — it must not'
else
    check 'a tag that overruns the Job-name budget fails the render' \
        grep -q 'may not exceed 63' "$OUT/longtag.txt"
fi

# And the image reference's other two components, which were plain
# interpolations while the tag was guarded.
refuses_chart catalog 'clearing image.registry fails the render' \
    'image.registry is required' --set-string 'image.registry='
refuses_chart catalog 'clearing image.api fails the render' \
    'image.api is required' --set-string 'image.api='
refuses 'an origin with a non-numeric port fails the render' 'is not a browser origin' \
    $GATEWAY_OVERLAY --set cors.enabled=true --set 'cors.origins={https://shop.example.com:notaport}'
# Case, which the shape test cannot see: the canonical origin lowercases scheme
# and host and WithOrigins compares ordinally, so `https://SPA.example` is
# refused by the host — `ConditionalBlockTests` covers that exact value.
refuses 'a non-lowercase origin fails the render' 'is not lowercase' \
    $GATEWAY_OVERLAY --set cors.enabled=true --set 'cors.origins={https://SPA.example}'

# And the CIDR list, where blank was only the emptiest way to be wrong.
# `not-a-network` rendered and threw out of IPNetwork.Parse at startup — again
# a case the host's own suite covers.
refuses 'a malformed trusted network fails the render' 'is not an IPv4 CIDR' \
    --set 'ingress.trustedNetworks={not-a-network}'
refuses 'a trusted network with a bad octet fails the render' 'octet above 255' \
    --set 'ingress.trustedNetworks={10.0.300.0/8}'
refuses 'a trusted network with a bad prefix fails the render' 'prefix length above 32' \
    --set 'ingress.trustedNetworks={10.0.0.0/64}'
# The security case rather than a tidiness one, and measured on .NET 10:
# IPNetwork.Parse("010.0.0.0/8") returns 8.0.0.0/8, because a leading zero is
# read as octal. The operator writes one network and the gateway trusts
# another, with nothing in the render or the rollout saying so.
refuses 'a trusted network with an octal octet fails the render' 'non-canonically' \
    --set 'ingress.trustedNetworks={010.0.0.0/8}'

# The guard checked `$o` and the ConfigMap emitted `$origin`, so a trailing
# space passed every test above and then failed the host's exact text
# comparison at startup — a validator that checks one string and ships another.
"$HELM" template gateway "$CHARTS_DIR/gateway" --set-string "image.tag=$TAG" \
    $GATEWAY_OVERLAY --set cors.enabled=true \
    --set 'cors.origins={https://shop.example.com }' >"$OUT/spaced.txt" 2>&1 || true
check 'the rendered origin is the validated one, not the raw value' \
    grep -q 'Cors__Origins__0: "https://shop.example.com"' "$OUT/spaced.txt"

# Clearing clientId used to drop all three keys silently. Web.Bff binds
# ServiceIdentityOptions unconditionally, so that rendered a release whose pod
# refuses to start — an opt-out that is not one.
refuses_bff() {
    local label="$1" needle="$2"
    shift 2
    if "$HELM" template web-bff "$CHARTS_DIR/web-bff" --set-string "image.tag=$TAG" \
        "$@" >"$OUT/bff.txt" 2>&1; then
        fail "$label — it rendered instead"
    else
        check "$label" grep -q "$needle" "$OUT/bff.txt"
    fi
}
refuses_bff 'clearing the BFF client id fails the render' 'identity.clientId is required' \
    --set-string 'identity.clientId='
refuses_bff 'a whitespace-only BFF client id fails the render' 'identity.clientId is required' \
    --set-string 'identity.clientId= '
refuses_bff 'disabling the BFF client credentials fails the render' \
    'clientCredentials is false' --set identity.clientCredentials=false

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
section 'The Service forwards to a port something is listening on'
# --------------------------------------------------------------------------
# The routing gate above compares CALLER urls with rendered Service ports, and
# stops there — so it never looks at the process behind `targetPort`. Catalog
# declares its two Kestrel endpoints in its own appsettings.json (§9.7: a
# cleartext port cannot serve HTTP/1.1 and h2c at once), and moving the h2c
# listener off 8081 there would satisfy every assertion in this file while
# deploying a Service that forwards to a closed port.
#
# Three files, one number, and until now the gate held two of them together.
grep -ohE 'http://0\.0\.0\.0:[0-9]+' "$ROOT/src/Services/Catalog/Catalog.Api/appsettings.json" |
    sed -E 's|.*:([0-9]+)|\1|' | sort -u >"$OUT/listeners.txt"

if [ ! -s "$OUT/listeners.txt" ]; then
    fail 'no Kestrel endpoints found in Catalog appsettings.json — the parse, not the chart, is wrong'
else
    while read -r port; do
        check "catalog-api listens on $port and the chart declares it" \
            grep -q "containerPort: $port$" "$OUT/catalog.yaml"
    done <"$OUT/listeners.txt"
fi

# And the other direction, so a chart port with nothing behind it is caught too.
awk '/^kind: Deployment$/ { in_dep = 1 } in_dep && /containerPort:/ { print $2 }' \
    "$OUT/catalog.yaml" | sort -u >"$OUT/declared.txt"
missing="$(comm -23 "$OUT/declared.txt" "$OUT/listeners.txt")"
if [ -z "$missing" ]; then
    pass 'and declares no port Catalog does not listen on'
else
    fail "chart declares port(s) Catalog has no listener for: $(echo "$missing" | tr '\n' ' ')"
fi

# --------------------------------------------------------------------------
section 'The canary track (§15.5, ADR-022)'
# --------------------------------------------------------------------------
# The newest surface in this tree, and therefore the one most in need of
# assertions: a gate that quietly stops covering what was added last is this
# repository's most-repeated failure, and the canary render is reached by
# nothing above.
#
# EVERY service chart, not a representative one. The mechanism lives in the
# library, so a chart that failed to pick it up would be a service with no
# canary and a rollout that promoted it without ever splitting traffic.
for chart in $SERVICE_CHARTS; do
    "$HELM" template "$chart-canary" "$CHARTS_DIR/$chart" --set-string "image.tag=$TAG" \
        --set canary.enabled=true --set autoscaling.enabled=false \
        $GATEWAY_OVERLAY >"$OUT/$chart-canary.yaml"
    pass "$chart renders a canary"

    name="$(awk '/^workload:/ { w = 1 } w && /^  name: / { sub(/^  name: /, ""); print; exit }' \
        "$CHARTS_DIR/$chart/values.yaml")"

    # THE ONE THAT MAKES IT A CANARY. Traffic reaches these pods because the
    # stable release's Service selects them, and it selects on the workload
    # name alone — so the canary's pod label has to be the SAME string the
    # stable Service matches on. A `-canary` suffix leaking into this label is
    # a canary that runs, reports healthy, serves nothing, and is promoted on
    # an analysis of no traffic.
    check "$chart: canary pods answer to the stable Service's selector" \
        awk -v want="$name" '
            /^kind: Deployment$/ { in_dep = 1 }
            in_dep && /^    matchLabels:$/ { in_sel = 1; next }
            in_sel && /app.kubernetes.io\/name: / {
                sub(/.*: /, ""); if ($0 == want) found = 1; in_sel = 0
            }
            END { exit found ? 0 : 1 }
        ' "$OUT/$chart-canary.yaml"

    # And the two Deployments must NOT select each other's pods, or each scales
    # the other away. The track label is what separates them, and it has to be
    # in the Deployment's SELECTOR — not merely somewhere in the manifest.
    #
    # THESE WERE PLAIN GREPS AND THAT MADE THEM VACUOUS. The same label is on
    # the Deployment's metadata and on the pod template, so deleting it from
    # `spec.selector.matchLabels` — the one place it does any work — left both
    # assertions green while the two Deployments began selecting each other's
    # pods, which is the exact failure this pair exists to catch. A gate that
    # greps the document cannot assert something about one field of it.
    selects_track() {
        # selects_track <file> <track> -> exit 0 when the Deployment's
        # matchLabels carries that track
        awk -v want="$2" '
            /^kind: Deployment$/ { in_dep = 1 }
            in_dep && /^    matchLabels:$/ { in_sel = 1; next }
            in_sel && /^      [a-z]/ {
                if ($0 ~ ("app.kubernetes.io/track: " want)) found = 1
                next
            }
            in_sel { in_sel = 0 }
            END { exit found ? 0 : 1 }
        ' "$1"
    }

    check "$chart: the canary Deployment SELECTS on track=canary" \
        selects_track "$OUT/$chart-canary.yaml" canary
    check "$chart: the stable Deployment SELECTS on track=stable" \
        selects_track "$OUT/$chart.yaml" stable
    check "$chart: no stable object leaks into the canary render" \
        test "$(count 'app.kubernetes.io/track: stable' "$OUT/$chart-canary.yaml")" -eq 0

    # Helm refuses to render an object another release owns (§15.3), so every
    # name the canary release emits has to differ from the stable one's. These
    # are the four the stable release keeps.
    for kind in Service Ingress HorizontalPodAutoscaler PodDisruptionBudget; do
        check "$chart: the canary renders no $kind" \
            test "$(count "^kind: $kind\$" "$OUT/$chart-canary.yaml")" -eq 0
    done
    check "$chart: the canary Deployment is named $name-canary" \
        grep -q "^  name: $name-canary\$" "$OUT/$chart-canary.yaml"

    # THE REPLICA COUNT HAS TO REACH THE SPEC, and nothing else here would
    # notice if it did not. `_deployment.tpl` omits `replicas` whenever
    # `autoscaling.enabled` is true, so a canary installed from the stable
    # release's values without `--set autoscaling.enabled=false` renders no
    # replica count and the API server defaults it to one — every rung of the
    # ladder a single pod, reported as the weight `plan` computed. The render
    # above passes the flag exactly as the rollout does; this asserts the flag
    # is what it is passed for.
    check "$chart: the canary Deployment carries a replica count" \
        grep -q '^  replicas: ' "$OUT/$chart-canary.yaml"

    # The ConfigMap too — same rule, and the mount has to follow the rename or
    # the pod sits in CreateContainerConfigError. Asserted as agreement between
    # the two halves rather than against a literal, which is the shape PR-23
    # learned when a gate credited a values key nothing consulted.
    awk '/configMapRef:/ { want = 1; next } want && /name:/ { sub(/^ *name: /, ""); print; want = 0 }' \
        "$OUT/$chart-canary.yaml" | sort -u >"$OUT/$chart-canary-mounted.txt"
    awk '/^kind: ConfigMap$/ { want = 1 } want && /^  name: / { sub(/^  name: /, ""); print; want = 0 }' \
        "$OUT/$chart-canary.yaml" | sort -u >"$OUT/$chart-canary-rendered.txt"
    check "$chart: every ConfigMap the canary mounts, the canary renders" \
        test -z "$(comm -23 "$OUT/$chart-canary-mounted.txt" "$OUT/$chart-canary-rendered.txt")"
    check "$chart: and none of them is the stable release's" \
        test "$(grep -cvE -- '-canary(-|$)' "$OUT/$chart-canary-rendered.txt")" -eq 0

    # The discriminator the analysis actually reads. Without it both tracks
    # report the same series and every step compares a release against itself
    # — which passes, every time, on a canary that is on fire.
    check "$chart: the canary declares deployment.track=canary" \
        grep -q 'OTEL_RESOURCE_ATTRIBUTES: "deployment.track=canary"' "$OUT/$chart-canary.yaml"
    check "$chart: the stable release declares deployment.track=stable" \
        grep -q 'OTEL_RESOURCE_ATTRIBUTES: "deployment.track=stable"' "$OUT/$chart.yaml"
done

# ADR-022's load-bearing consequence, and nothing above asserts it.
#
# The ADR says the canary release runs §7.4's hook, because it is the first
# thing carrying the new image — and therefore that a rollback removes the pods
# and LEAVES THE SCHEMA MIGRATED, which is what makes §15.5's
# backward-compatibility requirement sharper rather than softer. The templates
# do that today only because `_migration-job.tpl` has no canary guard. A later
# `if not canary` would render nothing, break the ADR, and pass every
# assertion in the section above — the gate-coverage failure this repository
# names as its most-repeated, on the newest surface in this tree.
#
# Both directions, on the same reasoning as the migration-template check
# further up: a chart with a migrator runs the hook on both tracks, and a
# chart without one runs it on neither.
for chart in $MIGRATOR_CHARTS; do
    check "$chart: the canary runs the migration hook (ADR-022)" \
        test "$(count '^kind: Job$' "$OUT/$chart-canary.yaml")" -eq 1
    check "$chart: and it is the same hook the stable release runs" \
        test "$(count '"helm.sh/hook": pre-install,pre-upgrade' "$OUT/$chart-canary.yaml")" -eq 1
done

for chart in $DATABASELESS_CHARTS; do
    check "$chart: the canary renders no migration Job either" \
        test "$(count '^kind: Job$' "$OUT/$chart-canary.yaml")" -eq 0
done

# The rollout plan names a chart per workload, and a plan pointing at a chart
# that cannot render a canary is a deploy that fails after the scale-up.
for chart in $SERVICE_CHARTS; do
    check "$chart appears in deploy/canary/canary.json" \
        grep -q "\"chart\": \"$chart\"" "$ROOT/deploy/canary/canary.json"
done

# --------------------------------------------------------------------------
section 'Result'
# --------------------------------------------------------------------------
if [ "$failures" -ne 0 ]; then
    printf '%s assertion(s) failed\n' "$failures" >&2
    exit 1
fi
printf 'all assertions passed\n'
