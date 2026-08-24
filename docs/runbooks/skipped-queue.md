# Runbook — skipped queue depth

| | |
|---|---|
| Alert | `SkippedQueueDepth`, in `deploy/observability/alerts/platform-alerts.yaml` |
| Condition | Any message in any `*_skipped` queue |
| Signal | `rabbitmq_queue_messages`, from the RabbitMQ exporter — not a solution instrument |
| Owner | The service team that owns the endpoint ([§13.8](../backend-architecture/13-observability.md)) |

## What it means

MassTransit handed an endpoint a message it has **no consumer for** and parked
it in `<queue>_skipped`. Nothing threw, nothing was retried, and nothing will
retry it.

**This is not the error queue's problem in a different queue.** An `_error`
message is one a consumer accepted and could not finish; a `_skipped` message
is one no consumer was willing to take. The first is a failure and the second
is a routing mismatch, and they are triaged from opposite ends — which is why
they are two alerts and two procedures rather than one selector matching both.

The threshold is `> 0` for the same reason the error queue's is: there is no
healthy number. A correctly ordered release never produces one.

## Which queue, and therefore which endpoint

The queue name is the endpoint name plus `_skipped`, so it names the receive
endpoint directly — the same mapping
[`error-queue.md`](error-queue.md) uses, and the same list of Ordering
endpoints applies. Read them from `Endpoints`/`DependencyInjection.cs` rather
than from memory.

```bash
kubectl -n <ns> exec deploy/rabbitmq -- \
  rabbitmqctl list_queues name messages | grep _skipped
```

## Read the message before deciding anything

The message type is the whole diagnosis, and it is in the headers rather than
the body.

```bash
kubectl -n <ns> exec deploy/rabbitmq -- \
  rabbitmqadmin get queue=<endpoint>_skipped count=1 ackmode=reject_requeue_true
```

`MT-MessageType` carries the type URNs the publisher stamped. Compare that
against what the endpoint's consumers and — for a saga — the state machine's
`Event<>` declarations actually bind.

## Then it is one of three things

### A producer shipped ahead of its consumer

**The common cause, and the one this alert mainly exists for.** A release added
a binding or a new message type and reached the broker before every replica
that declares it. During a rollout both releases consume the same queue
([§15.5](../backend-architecture/15-cicd-deployment.md) makes the canary a
second release of the same chart), so the broker can hand a newly bound message
to a replica whose build has no consumer for it.

[§9.2](../backend-architecture/09-messaging.md) forbids this ordering, so
finding it means the rule was not followed rather than that the rule is
insufficient.

1. **Finish the rollout first.** Once every replica runs the new build, nothing
   further is skipped, and the queue stops growing. Do not roll back — the old
   build is the one that cannot handle these messages.
2. **Then replay what was parked**, with the shovel procedure in
   [`error-queue.md`](error-queue.md); the mechanics are identical and only the
   source queue name differs.
3. **Record it against the release.** A skipped queue after a deploy is a
   §9.2 violation and the next release of that service should not repeat it.

> **Replay is right here and wrong for the arrival `error-queue.md` names.**
> These messages were never refused on their merits — no consumer saw them —
> so once the consumer exists they succeed. That is the opposite of a
> `PaymentAuthorised` whose saga instance is gone, which faults every time it
> is replayed.

### A binding the service should declare is missing

If every replica is on the same build and messages are still being skipped, the
consumer was never written. Read the type against
[§3.2](../backend-architecture/03-bounded-contexts.md)'s Consumes column: if
the table gives this service the event, the binding is owed and this is a
defect. Ship the consumer, then replay.

### The message was never meant for this endpoint

A publish where a send was intended puts one message on every queue bound to
the type ([§9.6](../backend-architecture/09-messaging.md)), and the endpoints
that do not handle it skip it. The skipped copies are then noise and the real
problem is at the producer. Look for the same message id in another endpoint's
queue — if one endpoint consumed it and the rest skipped it, this is the case.

Discard the skipped copies with a record. Fixing the producer is what closes
it.

## Closing it

The alert clears when the queue is empty. Confirm the messages **went
somewhere** — replayed into a consumer that now exists, or discarded with a
record of why — rather than having been purged to clear the graph. An empty
skipped queue and a green dashboard look identical whichever way it happened,
which is the trap [`outbox-abandoned.md`](outbox-abandoned.md) opens with and
the one [`error-queue.md`](error-queue.md) closes with.
