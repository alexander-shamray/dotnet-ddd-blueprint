# `deploy/observability/`

Alerts, dashboards and the SLO run, as code.

[§13.8](../../docs/backend-architecture/13-observability.md) requires it:
*"Dashboards are **code**, checked into `deploy/observability/` as Grafana JSON
or equivalent. A dashboard clicked together in a UI is lost with the instance
and cannot be reviewed."* The alert rules and the k6 run are here on the same
argument.

```
alerts/
  platform-alerts.yaml    LOADED
  awaiting-signal.yaml    NOT loaded — their instrument does not exist yet
dashboards/
  golden-signals.json     RED per service, plus §13.7's two request rows
  outbox.json             the two lanes of §9.4, kept apart
slo/
  slo.js                  the k6 SLO run of §13.7 and §15.1
check.py                  the gate — run it before pushing
```

## The gate

```bash
py -3.12 deploy/observability/check.py
```

Stdlib Python, no dependencies, no restore — the licence gate's terms, and for
the same reason: it reads text, so it can run before anything is built. CI runs
it in `.github/workflows/observability.yml`, path-filtered.

It asserts seven things. The first two are §13.9's pairing in both directions;
the middle ones are §13.6's third leg — *"an alert has three parts: a condition,
a signal and a procedure"* — and the last is the gate watching its own inputs.

| | |
|---|---|
| 1 | Every alert names a runbook, and that runbook exists |
| 2 | Every runbook is named by an alert |
| 3 | No alert is defined in both rule files |
| 4 | Every metric a **loaded** rule reads is published by something |
| 5 | Every metric an **awaiting-signal** rule reads is published by **nothing** |
| 6 | Every dashboard panel's metric is published |
| 7 | The workflow's triggers cover every path outside this tree that the gate reads |
| 8 | Every service hosting §9.4's dispatcher publishes outbox gauges, or is on a declared exemption |

**Check 8 exists because checks 4 and 5 are about metric *names* and cannot see
a service missing from a series.** The four loaded outbox alerts group `by
(service_name)`; a service that runs a dispatcher and publishes no gauges is
"covered" by four alerts that can never fire for it. Catalog is that service
today — §13.3 places `OutboxMetrics` in `Ordering.Infrastructure`, so closing
it means lifting the type into common code and teaching §4.5's scaffold to emit
it, which is a larger decision. It is on the exemption list with that reason,
and the check fails in **both** directions: a new unexempted service, and a
stale exemption for one that no longer needs it.

**What it does not do.** It reaches no Prometheus, no Grafana and no cluster,
and it does **not** check that a rules file is syntactically valid to
Prometheus — `promtool` would be the tool for that, and adding it is a decision
no chapter has taken. Named here as not covered rather than implied, on
`deploy/helm/smoke.sh`'s terms.

## Why there are two rule files

Four of §13.6's twelve alerts read an instrument this platform does not publish
yet. Loading them would be the exact defect that chapter spends a callout on:

> Two of the alerts in this document were written against signals that did not
> exist, and both looked correct: the dashboard is empty either way, whether the
> system is healthy or the metric was never published.

A rule that cannot fire is not a weak alert, it is a silent one — and silence
reads as health. So they sit in `awaiting-signal.yaml`, unloaded and visible,
and each names the instrument it is waiting for:

| Alert | Owed |
|---|---|
| `StuckSaga` | a gauge over `ordering.OrderFulfilmentStates` |
| `OrdersAwaitingReview` | a gauge over `ordering.OrderReviews` |
| `CacheHitRatioCollapse` | a host that calls `AddRedisConnections` — the meter is registered, nothing publishes to it |
| `BusinessVolumeDrop` | `OrderMetrics`, which arrives with §6.6's `OrderSummaries` projection |

**Check 5 is what keeps that list honest.** It asserts the metrics named there
are published by *nothing*, so the day somebody ships one of these instruments
the gate goes red and names the rule to move. An "awaiting signal" list that
nothing re-checks would quietly become a list of alerts nobody ever turned on.

**Neither file's rule count is written down here on purpose.** The table above
names all four by alert, so it evidences itself and cannot drift silently; a
bare "eight loaded" would be a number nothing recomputes, and the first rule
that moves between the files would make it wrong. `CLAUDE.md` makes the same
argument about its own line count.

All twelve runbooks exist either way — §13.9 asks for the procedure to be
written *when the alert is created*, not when it first fires.

## Metric names

Expressions use the OpenTelemetry-to-Prometheus spelling, not the C# one: dots
become underscores and a UCUM unit becomes a suffix. So `outbox.oldest.age`
with unit `s` is `outbox_oldest_age_seconds`, while the annotation unit
`{message}` is dropped and `outbox.pending.count` stays `outbox_pending_count`.

`check.py` applies the same transformation when matching an expression against
the instruments declared in C#, which is what stops the two spellings drifting
apart in silence.

**`service_name` assumes the collector promotes the `service.name` resource
attribute to a label.** That is a collector-config decision rather than
something this repository can assert, and it is stated rather than assumed:
without the promotion, every `by (service_name)` groups everything into one
series and the per-service alerts stop discriminating.

## Loading them

Neither Prometheus nor Grafana is deployed by this repository — §15.3's charts
cover the platform's own workloads and nothing else. How these files reach a
running Prometheus (a `PrometheusRule`, a ConfigMap, a sidecar-discovered
folder) is a decision for whoever owns the observability stack, and no chapter
has taken it. What this directory guarantees is that the content is reviewed,
versioned and internally consistent.

## The SLO run

```bash
k6 run \
  -e BASE_URL=https://staging.example.com \
  -e PROM_URL=http://prometheus.observability:9090 \
  -e TOKEN_URL=https://id.staging.example.com/realms/commerce/protocol/openid-connect/token \
  -e CLIENT_ID=slo-run -e CLIENT_SECRET=... \
  -e SLO_PRODUCT_ID=... \
  deploy/observability/slo/slo.js
```

It is the first real gate after the staging deploy (§15.1), and it is **not** a
smoke test — §15.1 declines to have one, because a gate nobody has defined is a
gate that gets configured to pass.

k6 drives the traffic and Prometheus adjudicates: k6's own thresholds are a
coarse guard, because a client's wall-clock includes the edge, TLS and the
network, while §13.7's command and query rows read `request.duration`, which is
dispatcher entry to result. The authoritative assertions are in `teardown()`,
one per §13.7 row this run's own traffic can actually produce.

**Three of §13.7's seven rows are not evaluated**, each named in `teardown()`
rather than quietly dropped: availability, because a three-minute run cannot
compute a monthly objective; `projection.lag`, because no service registers an
`IProjectionHandler<T>` and so nothing writes to it; and
`messaging.delivery.lag`, because the consumer that records it handles
Catalog's product events and neither scenario produces one. Asserting any of
the three would fail every run on a healthy platform, which is how a gate gets
switched off.

**An absent series fails the run.** It is not treated as "no problem observed",
for the reason the two rule files exist.

**Availability (99.9% monthly) is deliberately not evaluated.** A three-minute
run cannot; `http_req_failed` bounds the error rate *during the run*, which is a
much weaker claim, and the run says so rather than reporting a pass.
