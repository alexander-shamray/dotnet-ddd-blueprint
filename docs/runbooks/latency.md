# Runbook — latency

| | |
|---|---|
| Alert | `Latency`, in `deploy/observability/alerts/platform-alerts.yaml` |
| Condition | p99 > 1 s over 10 minutes, per service |
| Signal | `http.server.request.duration`, ASP.NET Core instrumentation ([§13.2](../backend-architecture/13-observability.md)) |
| Owner | The service team ([§13.8](../backend-architecture/13-observability.md)) |

## What it means

The slowest one per cent of requests are taking over a second. Users are
waiting. The [§13.7](../backend-architecture/13-observability.md) targets this
sits above are p95 < 100 ms for commands and < 80 ms for queries, so a p99 at
one second is an order of magnitude out, not a near miss.

## Separate the endpoint from the service

A service-wide p99 is almost always one route.

```promql
histogram_quantile(
  0.99,
  sum by (service_name, http_route, le) (rate(http_server_request_duration_seconds_bucket[10m]))
)
```

Two shapes, and they lead opposite ways:

- **One route slow, the rest flat** — a query plan, a projection, or a peer.
  Continue below.
- **Everything slow together** — saturation or a shared dependency. Skip to
  *Everything is slow* at the bottom.

## Read the trace waterfall, not the logs

`request.duration` is recorded by `LoggingBehavior` from dispatcher entry to
result ([§13.3](../backend-architecture/13-observability.md)), so a gap between
it and `http.server.request.duration` is time spent **outside** the pipeline —
model binding, authentication, middleware. That comparison is the fastest way to
tell "the handler is slow" from "the request is slow".

Inside the handler, the waterfall names the span. The usual suspects, in the
order they turn up here:

### A lost index

The regression this alert exists for: a query that was 40 ms is now 4 s and no
unit test noticed. §6.5's read side is Dapper on raw SQL, so the statement is in
the handler and can be run directly.

```sql
SET STATISTICS IO, TIME ON;
-- paste the handler's statement with representative parameters
```

A scan where there was a seek is the answer. Check the migration history for an
index that was dropped or renamed, and check the *shape of the parameters* —
[§6.5](../backend-architecture/06-cqrs.md)'s cursor pagination degrades badly if
a caller pages deep with a sort the index does not cover.

### A cold or missing cache

If the path reads through HybridCache, a hit-ratio collapse presents as latency
before it presents as anything else. See [`redis-cold.md`](redis-cold.md) —
though note that no host in this solution calls `AddRedisConnections` yet, so
today this branch is theory rather than a live cause.

### A slow peer

There is exactly one synchronous hop in this platform: BFF → Catalog for pricing
([§9.7](../backend-architecture/09-messaging.md), ADR-017). If the BFF is the
service alerting, check Catalog's own p99 first, then the resilience handler's
timeout hierarchy — `ServiceOptions.OperationTimeout` is 20 s and a request
sitting near it is a peer that has stopped answering rather than one that is
merely slow.

Everything else crosses the broker and cannot make an HTTP request wait. If a
trace shows a command handler blocking on a message, that is a design defect and
a bug report, not an incident action.

### A projection the write path waits on

It should not. §6.3's `TransactionBehavior` commits the aggregate and the outbox
row in one transaction and the dispatcher delivers afterwards, so a slow
projection shows up as [`projection-lag.md`](projection-lag.md), not here. A
trace that contradicts that is worth escalating on its own terms.

## Everything is slow

Check, in this order:

```bash
kubectl -n <ns> top pods
kubectl -n <ns> describe pod <pod> | grep -A5 -i throttl
```

- **CPU throttling** against the §15.3 resource shape. The charts set a memory
  limit and deliberately no CPU limit, so throttling here means the *request* is
  too low for the node's contention, not that a limit is biting.
- **Connection pool exhaustion.** A dependency that got slower turns into a pool
  that is empty, which makes every unrelated query slow. The tell is that
  latency rose everywhere at once, a few seconds after one thing got slower.
- **A neighbour on the node.** Check whether the alert is per-pod or
  per-service.

## Mitigation

Scaling out helps saturation and does nothing for a lost index — check which
before reaching for replicas. If the cause is a deploy, roll back; §15.5's
canary watches p99 for exactly this reason and should have held it.

## Closing it

The alert clears after ten minutes below the threshold. Confirm the p99 came
down **at the route that caused it**, not on the service average — a quiet hour
flatters a service-wide quantile while the bad route is still bad.
