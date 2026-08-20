# Runbook — error queue depth

| | |
|---|---|
| Alert | `ErrorQueueDepth`, in `deploy/observability/alerts/platform-alerts.yaml` |
| Condition | Any message in any `*_error` queue |
| Signal | `rabbitmq_queue_messages`, from the RabbitMQ exporter — not a solution instrument |
| Owner | The service team that owns the consumer ([§13.8](../backend-architecture/13-observability.md)) |

## What it means

MassTransit retried a message to exhaustion and moved it to the error queue.
**A business process has stopped** — whatever that message was going to cause
has not happened, and nothing will retry it without a human.

The threshold is `> 0` because there is no healthy number of poison messages.

## Which queue, and therefore which consumer

The queue name is the endpoint name plus `_error`, so it names the receive
endpoint directly. §9.8 and §9.4 define them, and **Ordering declares four** —
each with an error queue of its own:

| Endpoint | Carries |
|---|---|
| `ordering-catalog-events` | Catalog's three product events → the price projection (§6.6) |
| `ordering-commands` | The four commands §9.6's saga sends to this service |
| `ordering-stock-events` | `StockReserved` → `Order.ConfirmStock` |
| `ordering-fulfilment-saga` | The saga's own correlated events and timeouts |

**List them from `Endpoints`/`DependencyInjection.cs` rather than from memory.**
An earlier version of this runbook named the first two and stopped, which is
worse than naming none: the `_error` grep below still shows all four, but an
on-call who trusts a short inventory reads a poisoned `ConfirmStock` or a stuck
saga as "no Ordering error queue" and looks in another service.

```bash
kubectl -n <ns> exec deploy/rabbitmq -- \
  rabbitmqctl list_queues name messages | grep _error
```

## Read the message before deciding anything

**`rabbitmqctl` will not do this.** `list_queues` and `info_all` return queue
metadata and counts — they do not return a message body or a header, which is
everything the steps below need. Use the Management API's `get` with
`ackmode=ack_requeue_true`, which reads the message and puts it back:

```bash
kubectl -n <ns> port-forward svc/rabbitmq 15672:15672 &

curl -su guest:guest -X POST \
  -H 'content-type: application/json' \
  -d '{"count":5,"ackmode":"ack_requeue_true","encoding":"auto"}' \
  http://localhost:15672/api/queues/%2F/<endpoint>_error/get
```

`ackmode=ack_requeue_true` is the load-bearing part: **`ack_requeue_false`
consumes the message and it is gone.** The Management UI's *Get messages* with
**Requeue: yes** is the same call behind a form, and is the usual way to do it.

What you want off the result:

- **`MessageId`** — ties to the outbox row that published it, and to §9.5's
  inbox.
- **`CorrelationId`** — reaches the originating request's logs and trace
  ([§10.4](../backend-architecture/10-api-gateway.md)).
- **`MT-Fault-Message` / `MT-Fault-StackTrace`** — MassTransit's own headers,
  carrying why the final attempt failed. This is the field that decides
  everything below.

## Replay or discard

### Replay — the message is fine, the world was broken

A dependency that was down, a database that was failing over, a deploy mid-roll.
The payload is valid and the consumer would now succeed.

Move it back to the main queue with the MassTransit CLI, or the Management UI's
shovel:

```bash
# Shovel from <endpoint>_error back to <endpoint>.
kubectl -n <ns> exec deploy/rabbitmq -- rabbitmqctl eval \
  'rabbit_shovel_util:...'   # or configure a shovel in the Management UI
```

**Replay is safe by design and it is worth knowing why.** §9.5's inbox filter
records `MessageId` and makes a second delivery of the same message a no-op
inside the handler's own transaction — so replaying something that partly
succeeded does not double it. That guarantee is per consumer, so it holds for
the endpoint this message came from and says nothing about side effects a
handler produced outside the database.

### Discard — the message can never succeed

A contract the consumer no longer understands, a payload that was malformed at
source, or an event whose effect has since been produced another way.

Purge only after recording the message body somewhere durable. Once it is gone
the only record is whatever you saved.

```bash
kubectl -n <ns> exec deploy/rabbitmq -- rabbitmqctl purge_queue <endpoint>_error
```

**`purge_queue` takes the whole queue.** If it holds a mix of replayable and
unreplayable messages, shovel the replayable ones back first, then purge — or
you have discarded the good ones with the bad.

### Fix first — the consumer has a bug

Then neither replay nor discard is right yet. Leave the messages parked, ship
the fix, and replay afterwards. Messages in an error queue are not lost and are
not costing anything; replaying into a consumer that still throws just fills the
queue again and burns the trail.

## Why it reached the error queue at all

Worth answering before closing, because the retry policy is the thing that
failed. §9.8's endpoint configuration sets retry, then the inbox filter, then
the in-memory outbox, in that order. If a transient fault exhausted the retries,
the policy may be too tight for that dependency. If a *deterministic* fault
consumed every attempt, retrying it at all was wasted — a poison message should
fail fast, and MassTransit's `Ignore<T>` for known-terminal exception types is
the lever.

## Closing it

The alert clears when the queue is empty. Confirm the messages **went
somewhere** — replayed and processed, or discarded with a record — rather than
having been purged to clear the graph. An empty error queue and a green
dashboard look identical whichever way it happened, which is the same trap
[`outbox-abandoned.md`](outbox-abandoned.md) opens with.
