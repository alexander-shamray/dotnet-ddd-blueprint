# Runbook — error rate

| | |
|---|---|
| Alert | `ErrorRate`, in `deploy/observability/alerts/platform-alerts.yaml` |
| Condition | 5xx > 1% of requests over 5 minutes, per service |
| Signal | `http.server.request.duration`, ASP.NET Core instrumentation ([§13.2](../backend-architecture/13-observability.md)) |
| Owner | Platform for the gateway, the service team for a service ([§13.8](../backend-architecture/13-observability.md)) |

## What it means

One request in a hundred is failing with a 5xx. Users are seeing errors **now**,
which is what makes this a page rather than a ticket.

**A 4xx is not this alert and must not be added to it.** [§10.5](../backend-architecture/10-api-gateway.md)
maps `Error.Rule` to 422 and `ValidationException` to 400, both of which are the
system correctly refusing a request. Widening the condition to `4..|5..` is the
fastest way to make this pager ignorable.

## First, decide whether it is the edge or a service

The alert is grouped by `service_name`, so the label already says. The
distinction matters because the gateway has no domain and no database
([§10.1](../backend-architecture/10-api-gateway.md)) — a 5xx there is routing,
auth, or a backend that is not answering, never business logic.

```promql
sum by (service_name, http_route) (
  rate(http_server_request_duration_seconds_count{http_response_status_code=~"5.."}[5m])
)
```

If `service_name` is the gateway **and** a backend is also alerting, work the
backend first: the edge is reporting its downstream, and fixing the downstream
clears both.

## Then correlate to a deploy

Most 5xx spikes start at a rollout.

```bash
kubectl -n <ns> rollout history deploy/<workload>
kubectl -n <ns> get events --sort-by=.lastTimestamp | tail -30
```

A spike that begins within a minute or two of a rollout is that rollout until
proven otherwise. `kubectl rollout undo deploy/<workload>` is the shortest path
back, and the canary policy of [§15.5](../backend-architecture/15-cicd-deployment.md)
should already have caught it — if it did not, that is a second finding to
raise after the incident.

## Read one failing request end to end

Correlation IDs are assigned at the edge and flow through every log and span
([§10.4](../backend-architecture/10-api-gateway.md), §13.4), so one identifier
is enough to reconstruct the path.

1. Take an `X-Correlation-Id` from a failing response, or pick any from the
   error logs.
2. Query traces for it, and read the waterfall for the first span that faults.
3. Query logs for the same value across **all** services, not just the one that
   alerted — the exception is often thrown one hop in.

**The exception message is in the log, never in the response.** §10.5's
ProblemDetails deliberately carries no stack trace and no internal detail, so
reading the response body will not tell you what broke.

## The usual causes, in the order they occur

- **A dependency is down.** Check readiness for the service and its neighbours:
  a database that has gone away shows up as `/health/ready` failing
  ([§13.5](../backend-architecture/13-observability.md)) and the pod leaving the
  Service's endpoints. If readiness is green and 5xx is high, the dependency the
  failing path uses is *not* one readiness covers — say so in the postmortem,
  because that is a gap worth closing.
- **A migration ran and the code did not.** See
  [`migration-failure.md`](migration-failure.md); the reverse case — code ahead
  of schema — surfaces here as `SqlException` naming a missing column.
- **An unhandled exception on a new path.** The trace names the method. This is
  the case where a fix ships rather than a rollback, but roll back first and fix
  second.
- **Saturation.** Check CPU throttling and the connection pool before
  concluding it is a bug: a pool exhausted by a slow dependency raises
  timeouts, and timeouts surface as 5xx.

## Mitigation before diagnosis

In order of preference: roll back the deploy; scale out if the trace shows
saturation rather than a defect; shed load at the edge by tightening the
relevant §10.3 rate-limit policy. Only the first of those is a fix — the other
two buy time and must be reverted in the same incident.

## Closing it

The alert clears on its own once the ratio drops below 1% for five minutes.
Before closing, check that the errors stopped rather than the *traffic* stopped:
a service nobody can reach has an excellent error rate.

```promql
sum by (service_name) (rate(http_server_request_duration_seconds_count[5m]))
```

If that has collapsed too, this is [`business-volume.md`](business-volume.md)
and the incident is not over.
