# Runbook — broker lane stalled

| | |
|---|---|
| Alert | `OutboxBrokerLaneStalled`, in `deploy/observability/alerts/platform-alerts.yaml` |
| Condition | `outbox.oldest.age{lane="Broker"}` > 2 minutes |
| Signal | `OutboxMetrics`, `Ordering.Infrastructure/Observability` ([§13.6](../backend-architecture/13-observability.md)) |
| Owner | Platform — a broker-lane stall is usually a shared-broker fault ([§13.8](../backend-architecture/13-observability.md)) |

## What it means

This service has integration events written to its outbox that it has not
managed to publish. **Other services are working from stale data**, and any saga
waiting on one of these events has stopped advancing.

The threshold is two minutes rather than the local lane's thirty seconds because
the broker lane crosses a network: it is meant to absorb a short RabbitMQ blip
or a rolling broker restart without paging anyone. Two minutes means it did not.

## What is *not* affected

Nothing user-facing, yet. §6.3 commits the aggregate and its outbox row in one
transaction, so writes are still succeeding and the data is safe — this is a
delivery failure, not a data-loss one. That changes if rows reach the attempt
cap, which is [`outbox-abandoned.md`](outbox-abandoned.md).

## Is the dispatcher running, or is the broker refusing?

These fail identically on the graph and need opposite responses.

```bash
kubectl -n <ns> logs deploy/ordering --since=10m | grep -i "Outbox claim failed\|Outbox message"
```

- **`Outbox claim failed`** — the dispatcher cannot reach *SQL Server*. This is
  a database incident wearing an outbox costume; readiness should be red too
  ([§13.5](../backend-architecture/13-observability.md)).
- **`Outbox message … failed, attempt N of 10`** — the dispatcher is running and
  delivery is failing. Continue below.
- **Silence** — the dispatcher is not running at all. Check the pod is up and
  that `AddHostedService<OutboxDispatcher>()` survived the last change to
  `AddOrderingInfrastructure`; a registration nothing resolves at startup fails
  quietly, and the service reports ready regardless.

## Check RabbitMQ, in this order

```bash
kubectl -n <ns> exec deploy/rabbitmq -- rabbitmqctl status
kubectl -n <ns> exec deploy/rabbitmq -- rabbitmqctl list_queues name messages consumers
kubectl -n <ns> exec deploy/rabbitmq -- rabbitmqctl list_connections user state
```

1. **Reachable?** A DNS or NetworkPolicy change is the commonest cause and the
   least obvious — the connection string is fine, the host simply is not
   routable from this pod any more.
2. **Credentials?** `ConnectionStrings__RabbitMq` is an External Secrets–managed
   Secret ([§15.4](../backend-architecture/15-cicd-deployment.md)); a rotation
   that reached the vault and not the pod presents exactly like this. Compare
   the mounted secret's version with the vault's.
3. **Queue at its length limit?** A queue at `max-length` rejects publishes.
   `list_queues` shows it; the fix is to drain the consumer, not to raise the
   limit.
4. **Memory or disk alarm?** `rabbitmqctl status` reports both. An alarmed node
   blocks publishers, which looks like a hang rather than an error.
5. **The delayed-exchange plugin.** ADR-021 schedules saga timeouts on it, which
   is why §14.1's RabbitMQ image is built rather than pulled. A broker that came
   back without the plugin accepts ordinary publishes and fails scheduled ones.

## While it is stopped

Nothing needs replaying by hand. The dispatcher polls twice a second and the
claim is idempotent — rows keep their place in `OccurredAt` order and go out
when the broker returns. §9.5's inbox filter makes redelivery safe on the
consumer side, so a burst after recovery is not a hazard.

**Do not delete rows to clear the backlog.** They are the only copy of those
events.

## Recovering

Once the broker is back, watch the gauge fall rather than assuming:

```promql
max by (service_name) (outbox_oldest_age_seconds{lane="Broker"})
sum by (service_name) (outbox_pending_count{lane="Broker"})
```

The age gauge should drop to near zero within a minute or two of the backlog
draining. If the count falls but the **age does not**, a specific old row is
failing while newer ones succeed — that is a poison message, and it is
[`outbox-abandoned.md`](outbox-abandoned.md) once it hits the cap. Read it now
rather than waiting:

```sql
SELECT TOP 10 Id, MessageId, MessageType, Attempts, LockedUntil, LEFT(LastError, 500) AS LastError
FROM ordering.OutboxMessages
WHERE ProcessedAt IS NULL
    AND Lane = 'Broker'
ORDER BY OccurredAt;
```

## What downstream is missing

Worth writing into the incident record, because it is the part nobody
reconstructs afterwards. §3.2 owns the contract map; for Ordering's broker lane
the consumers are the services subscribed to `OrderPlaced`, `OrderCancelled` and
the rest of §9.6's published set. Each of them has been operating on a world
that stopped at the timestamp of the oldest unshipped row.
