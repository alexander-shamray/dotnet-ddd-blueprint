# ADR-022 — The canary is a second release, weighted by replicas

**Decision.** [§15.5](../15-cicd-deployment.md)'s canary is a **second Helm release
of the same chart** — `<workload>-canary`, `canary.enabled=true` — whose pods
carry the same `app.kubernetes.io/name` as the stable release's and are
therefore selected by the same Service. Traffic splits because kube-proxy
spreads connections across a Service's endpoints, so the share the new version
serves is `canary / (stable + canary)`. No service mesh, no Argo Rollouts, no
Flagger, and no ingress-controller canary annotation.

Two things follow and both are load-bearing. **The weight is quantised**, so
§15.5's ladder is a set of ceilings rather than of targets to land on:
`deploy/canary/canary.py` computes the largest canary that stays within the
requested weight and **refuses** where even one pod overshoots, naming the
stable replica count that would satisfy it. And **the stable release is never
modified** — not its image, not its replica count beyond the one scale-up the
first rung needs — so a rollback is `helm uninstall` of the canary and costs
the canary's own pods and nothing else.

The two tracks are told apart in the telemetry by a `deployment.track` resource
attribute, supplied through `OTEL_RESOURCE_ATTRIBUTES` from the chart.

> **A resource attribute is not a metric label, and the collector is what
> bridges them.** Under the standard OTLP-to-Prometheus mapping only
> `service.name`, `service.namespace` and `service.instance.id` become labels
> on each series; everything else lands in `target_info`. So a query filtering
> `deployment_track="canary"` matches nothing unless the collector copies the
> attribute onto the datapoint — and matching nothing is read here as an absent
> series, which rolls back. **Every rung, every time, on a canary behaving
> perfectly.** [§14.1](../14-local-development.md)'s collector does it with a
> `transform` processor over one attribute; **the deployed collector must do
> the same**, and that is a requirement on an environment this repository does
> not contain rather than something its gates can check.

**Why.** §15.5 specifies the behaviour and no chapter had chosen a mechanism,
so building the rollout was what forced the choice. Three were live.

**An ingress-controller canary annotation is disqualified by this platform's
topology, not by taste.** It splits traffic at the Ingress, and this platform
has exactly one — the gateway's ([§10.1](../10-api-gateway.md)). Everything behind
it is reached by Service name from YARP's route file and from `PricingHop.cs`,
both of which hold those names as literals on the stated grounds that "the host
is the Kubernetes Service name" ([§10.2](../10-api-gateway.md),
[§9.7](../09-messaging.md)). So an Ingress-level weight can canary the edge and
**cannot canary Catalog or Ordering at all** — the split would happen upstream
of the hop that chooses them. A mechanism that works for one deployable out of
four is not a platform's canary.

**A mesh or a rollout controller is the better answer and is not this one.**
Argo Rollouts or Flagger would give exact weights, an analysis loop and a
`Rollout` CRD, and Linkerd or Istio would give the traffic split without the
replica arithmetic. Each is a cluster-wide component with its own upgrade
cycle, its own failure modes and its own vocabulary, added to a platform whose
entire deploy surface is `helm upgrade` and whose charts a shell script
verifies by rendering them. It is also a component that would have to be
present before any of this could be tested at all, and **no cluster exists** —
so adopting one would mean shipping a dependency on faith and a rollout nobody
could read. The replica-weighted version needs nothing that is not already
here.

What it costs is the 5% rung. With [§15.3](../15-cicd-deployment.md)'s
`replicaCount: 3`, one canary pod already serves 25% — five times what §15.5's
first step asks for — so 5% requires **19 stable replicas**, and the rollout
scales up to that before anything rolls. The chart's own
`autoscaling.maxReplicas` is 20 on the three service charts, which is exactly
19 plus one canary — on those, the smallest configuration in which 5% is
expressible is the largest the chart allows, and neither number was chosen with
the other in mind. **It is not a platform-wide coincidence**: the gateway's
ceiling is 30, because every external request passes through it, so there the
19 is what the weight costs and nothing more. The rollout's scale-up fits under
either, and the gateway's autoscaler can still climb above the count the step
was planned against — which is a residual of raising a floor rather than
pinning a replica count.

**Consequences.** The Deployment's `matchLabels` gains
`app.kubernetes.io/track`, and **that field is immutable** — so this is a
breaking change to any installed release, which has to be deleted and
recreated. It costs nothing today because nothing has ever installed these
charts, and it would cost a downtime window if taken later. The Service's
selector is deliberately unchanged: a Service that selected only `stable` would
route the canary nothing, which is the same failure as having no canary.

The canary release renders no Service, Ingress, HorizontalPodAutoscaler or
PodDisruptionBudget. Those carry fixed names the stable release owns, and Helm
refuses to touch another release's objects (§15.3) — so the suppression is what
makes the install possible rather than a tidying decision. Autoscaling is off
on the canary for a second reason as well: the served weight *is* the replica
ratio, so an autoscaler would move the blast radius underneath the analysis
judging it.

§15.5's requirement that "every migration must be backward compatible with the
previous release" becomes sharper rather than softer. The canary release runs
[§7.4](../07-persistence.md)'s migration hook, because it is the first thing
carrying the new image; a rollback then removes the pods and leaves the schema
migrated. That is exactly the case §15.5 calls unrecoverable if the migration
was not backward compatible, and the cheap rollback this decision buys is worth
nothing against an incompatible one.

**The weight is a ceiling under ordinary operation, and a voluntary disruption
can exceed it.** The PodDisruptionBudget is the stable release's and its
selector matches both tracks, which is right — the pods serve one Service — but
it constrains the *total*. At the 5% rung that is 19 stable and one canary
against a `minAvailable` well below twenty, so a node drain during a dwell can
evict stable pods and leave the canary serving far more than the rung asked
for.

Two things bound what that costs, and neither makes it disappear. **The verdict
is measured rather than assumed**: `analyse` reads both tracks' real error rate
and p99, so an exceeded weight means more exposure for one dwell, not a wrong
decision about the release. And the disruption is voluntary, so it is somebody
draining a node rather than something the rollout does.

The fix — a temporary stable-track budget, created for the ladder and removed
with the rest — is **deliberately not taken here**, and the reason is the shape
of this workflow rather than the size of the change. It is a fourth object for
a cleanup path that has already had three defects found in it, added to a
rollout no one has run; guarding a voluntary-disruption edge by enlarging the
surface that must be undone on every failure is the wrong trade until there is
a cluster to observe either behaviour on. Recorded rather than fixed, on the
terms this ADR already uses for the connection-spreading premise below.

**Not verified against a cluster.** `deploy/canary/canary.py` has a suite and
`deploy/helm/smoke.sh` renders the canary track and asserts what comes out, but
both stop at the manifest. Whether kube-proxy's spread actually approximates
the replica ratio under real connection patterns — keep-alive, HTTP/2
multiplexing to the gRPC listener, a client that opens one connection and holds
it — is **not** established here, and long-lived connections are the known way
this mechanism under-delivers a weight. It is named as owed rather than
implied.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
