# Helm charts

§15.3 is the specification; this file is how to run what it specifies. Where
the two disagree, the chapter wins.

A chart per deployable and an umbrella that installs them together. A service
can be rolled on its own — which is what §15.1's per-service path filters
produce — and an environment can be stood up whole.

```
common/      the library chart: every template, once
catalog/     ┐
ordering/    │ Chart.yaml + values.yaml + one-line templates that include
web-bff/     ┘ the library's. The values ARE the per-service decisions.
gateway/     the same, plus edge-config.yaml — the two keys no service has
             (§15.3), in a template only this chart carries
platform/    the umbrella — four dependencies and no values of its own
smoke.sh     renders all five and asserts what comes out
```

## Setup is one command per chart

`Chart.yaml`'s `file://../common` dependency resolves from disk, so this needs
no network and no chart repository:

```bash
helm dependency update deploy/helm/catalog     # and ordering, gateway, web-bff
helm dependency update deploy/helm/platform    # after the four above
```

Order matters: a service chart must already hold `commerce-common` in its own
`charts/` before the umbrella packages it, or the umbrella renders a subchart
whose library templates are missing. `smoke.sh` does both, in that order.

> **Editing `common/` and then rendering renders the OLD template.**
> `helm dependency update` copies the library chart into each dependant's
> `charts/` as a tarball, and `helm template` reads the tarball — so a change
> to `common/templates/` is invisible until the dependency is resolved again.
> There is no warning; the render simply succeeds with the previous version.
> `smoke.sh` re-resolves on every run and is therefore never wrong about this,
> which is exactly why an ad-hoc `helm template` can be.

`charts/` and `Chart.lock` are ignored rather than committed, and `.gitignore`
argues why.

## Rendering and installing

Every chart refuses to render without an image tag. That is the point:

```bash
$ helm template catalog deploy/helm/catalog
Error: execution error at (catalog/charts/commerce-common/templates/_helpers.tpl:…):
image.tag is required and values.yaml leaves it empty on purpose: a deploy that
cannot name its image must fail rather than roll something nobody chose (§15.3).
```

So a render always carries one:

```bash
helm template catalog deploy/helm/catalog --set-string image.tag="$SHA"

helm upgrade --install catalog-api deploy/helm/catalog \
    --namespace commerce --set-string image.tag="$SHA"
```

The umbrella takes a tag per subchart, because §15.1 builds and deploys per
service — a change under `src/Services/Catalog` rebuilds Catalog alone, and
every other subchart must keep the tag it is already running:

```bash
helm upgrade --install platform deploy/helm/platform \
    --namespace commerce \
    --set-string catalog.image.tag="$CATALOG_SHA" \
    --set-string ordering.image.tag="$ORDERING_SHA" \
    --set-string gateway.image.tag="$GATEWAY_SHA" \
    --set-string web-bff.image.tag="$BFF_SHA"
```

## What is deliberately not here

- **Secrets.** External Secrets Operator owns every `Secret` these charts name
  (§15.4); a chart that templated a connection string would put a password into
  `helm get values` and into every diff of this repository. The charts
  reference; they never create.
- **`Namespace` objects.** `--namespace` and `--create-namespace` are the
  installer's decision, and a chart that created one would fight whatever
  manages the cluster's namespaces.
- **Redis connection strings.** §15.4's inventory requires them once a host
  reads a cache, and none does — nothing calls `AddRedisConnections` yet. A
  `secretKeyRef` to a Secret that does not exist is a pod that never starts, so
  they join with the PR whose code reads them, which is the rule §14.1's
  Compose blocks already state.
- **`readOnlyRootFilesystem`.** The right posture, and a decision no chapter
  has taken. Asserting it untested against the chiselled runtime images would
  trade a review question for a CrashLoop. `runAsNonRoot` IS set, because it
  asserts what the image already does — UID 1654, measured off the base
  image's own config.
- **A cluster.** `smoke.sh` renders and greps; it never applies. Schema
  validation against a live API server is a deploy-time gate (§15.1).

## The gate

```bash
bash deploy/helm/smoke.sh          # HELM=/path/to/helm if it is not on PATH
```

**Twenty deliberate defects were run through it, and nineteen turned a green
run red** — a renamed Service, a CPU limit, a grace period back at the
Kubernetes default, a dropped hook annotation, a connection string moved into a
ConfigMap, a second chart growing client credentials, an `envFrom` naming a
ConfigMap nothing renders, a rollout checksum that missed the gateway's own
ConfigMap.

The twentieth is the reason this paragraph exists. "The gateway renders no
migration Job" passed against a gateway that declared `image.migrator`, because
that chart carries no migration template at all — so the assertion could not
fail, and the values comment crediting the missing key was describing a
mechanism nothing consulted. It is now an assertion about the agreement between
the two halves, and it fails from either side.

**One of the twenty found a defect in the charts rather than in the harness**,
and it was found by writing the assertion rather than by reading the template:
the rollout checksum hashed only the ConfigMap the library renders, so editing
`cors.origins` changed a mounted ConfigMap and left the pod template
byte-identical — a deploy that reports success and rolls nothing. The
annotation now hashes the whole of `.Values`, which over-triggers on a few keys
the container never sees and is the safe direction.

CI runs the same script, path-filtered to this tree **and to the two source
files it reads** — the gateway's route file and `PricingHop.cs`, because
renaming a destination there is what would otherwise break a deploy from a
green PR.
