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

**The credentials are not `guest/guest`.** That is §14.1's Compose default; a
deployed broker's are supplied by External Secrets
([§15.4](../backend-architecture/15-cicd-deployment.md)), and reaching for the
local default here authenticates as an account that does not exist. Take an
authorised management credential from the incident context and export it:

```bash
kubectl -n <ns> port-forward svc/rabbitmq 15672:15672 &

export RABBIT_USER=... RABBIT_PASSWORD=...   # from the vault, not from §14.1

curl -su "$RABBIT_USER:$RABBIT_PASSWORD" -X POST \
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

Move it back with the Management API's shovel, declared as a one-shot
parameter. It moves every message on the error queue back to the endpoint and
deletes itself when the queue is empty:

```bash
kubectl -n <ns> port-forward svc/rabbitmq 15672:15672 &

curl -su "$RABBIT_USER:$RABBIT_PASSWORD" -X PUT \
  -H 'content-type: application/json' \
  -d '{"value":{
        "src-protocol":"amqp091","src-uri":"'"$AMQP_URI"'","src-queue":"<endpoint>_error",
        "dest-protocol":"amqp091","dest-uri":"'"$AMQP_URI"'","dest-queue":"<endpoint>",
        "src-delete-after":"queue-length","ack-mode":"on-confirm"}}' \
  http://localhost:15672/api/parameters/shovel/%2F/replay-<endpoint>

# Watch it drain, then confirm the shovel removed itself.
curl -su "$RABBIT_USER:$RABBIT_PASSWORD" http://localhost:15672/api/shovels/%2F
```

**Both URIs are explicit, and a bare `amqp://` is the trap.** The shovel runs
*inside* the broker and connects with its own credentials, so an unqualified
URI means `guest`, which on a deployed broker is not a user — the API accepts
the parameter and the shovel then fails to connect at both ends. Set
`AMQP_URI` to the authorised value (`amqp://user:password@localhost:5672/%2F`)
so the API call and the shovel's own connections use the same account.

`src-delete-after: queue-length` is what makes this one-shot: the shovel stops
after the messages present when it started, so a consumer that fails again does
not get replayed in a loop. `ack-mode: on-confirm` is what makes it safe — a
message is removed from the error queue only once the destination has confirmed
it.

**Requires the `rabbitmq_shovel` and `rabbitmq_shovel_management` plugins.**
§14.1's image builds in the delayed-exchange plugin and not these, so check
before reaching for this during an incident:

```bash
kubectl -n <ns> exec deploy/rabbitmq -- rabbitmq-plugins list | grep shovel
```

Without them, the Management UI's *Move messages* is unavailable too, and the
fallback is to consume and republish with a short script — which is worth
knowing *before* 03:00 rather than discovering then.

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
