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
local default here authenticates as an account that does not exist.

**Keep them out of `argv`.** Anything passed as `curl -u` or `-d` is visible in
the process list to every user on the box, and an incident is exactly when a
shell is being shared and scrollback is being pasted into a channel. A
mode-0600 curl config carries the credential, and the request body comes from
stdin:

```bash
kubectl -n <ns> port-forward svc/rabbitmq 15672:15672 &

umask 077
cat > "$HOME/.rabbit.curl" <<'EOF'
user = "OPERATOR:PASSWORD"
EOF

curl -sS --config "$HOME/.rabbit.curl" -X POST \
  -H 'content-type: application/json' \
  -d @- http://localhost:15672/api/queues/%2F/<endpoint>_error/get <<'EOF'
{"count":5,"ackmode":"ack_requeue_true","encoding":"auto"}
EOF
```

**Delete it when the incident closes** — `rm -f "$HOME/.rabbit.curl"` — and use
a credential you can revoke rather than the service's own.

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

**Percent-encode the credential before it goes in the URI.** A generated
password containing `@`, `:`, `/`, `#` or `%` — which a vault-issued one often
is — gets parsed as URI structure rather than as the credential, so the shovel
is created and then fails to connect. During an incident that reads as "the
replay did nothing".

The body carries that URI, so it goes in through stdin for the same reason the
first request did, and never on the command line:

```bash
# Reads from stdin, NOT from argv. An earlier version passed the value as an
# argument, so the encoder written to keep the password off the command line
# put it on the command line of its own child — visible in the process list for
# as long as Python ran.
enc() { python3 -c 'import sys,urllib.parse as u; print(u.quote(sys.stdin.read(), safe=""), end="")'; }
uri="amqp://$(printf '%s' "$OPERATOR" | enc):$(printf '%s' "$OPERATOR_PASSWORD" | enc)@localhost:5672/%2F"

# Unquoted heredoc, so $uri expands. Everything else here is literal.
curl -sS --config "$HOME/.rabbit.curl" -X PUT \
  -H 'content-type: application/json' \
  -d @- http://localhost:15672/api/parameters/shovel/%2F/replay-ENDPOINT <<EOF
{"value":{
  "src-protocol":"amqp091","src-uri":"$uri","src-queue":"ENDPOINT_error",
  "dest-protocol":"amqp091","dest-uri":"$uri","dest-queue":"ENDPOINT",
  "src-delete-after":"queue-length","ack-mode":"on-confirm"}}
EOF

# Watch it drain, then confirm the shovel removed itself.
curl -sS --config "$HOME/.rabbit.curl" http://localhost:15672/api/shovels/%2F
```

**Both URIs are spelled out, and a bare `amqp://` is the trap.** The shovel runs
*inside* the broker and connects with its own credentials, so an unqualified URI
means `guest` — which on a deployed broker is not a user. The API accepts the
parameter and the shovel then fails to connect at both ends, which is a worse
outcome than a rejected request because it looks like it worked. Both failure
modes present identically, and both are silent.

**The shovel definition persists with the password in it** until it deletes
itself or you remove it. `DELETE /api/parameters/shovel/%2F/replay-<endpoint>`
if it is still there after the queue drains.

`src-delete-after: queue-length` is what makes this one-shot: the shovel stops
after the messages present when it started, so a consumer that fails again does
not get replayed in a loop. `ack-mode: on-confirm` is what makes it safe — a
message is removed from the error queue only once the destination has confirmed
it.

**Requires the `rabbitmq_shovel` and `rabbitmq_shovel_management` plugins.**
§14.1's image now enables both — it was the delayed exchange alone until this
procedure needed them, because a replay path the shipped image cannot run is
not a replay path. A deployed broker is somebody else's image, so check anyway:

```bash
kubectl -n <ns> exec deploy/rabbitmq -- rabbitmq-plugins list | grep shovel
```

If they are absent, **stop and enable them** rather than improvising:

```bash
kubectl -n <ns> exec deploy/rabbitmq -- \
  rabbitmq-plugins enable rabbitmq_shovel rabbitmq_shovel_management
```

Both ship inside the official image, so this needs no download and no restart —
which is why it is the recovery here rather than a hand-rolled consume-and-
republish loop. That loop is where messages get lost at 03:00: it has to
reproduce the headers `MT-Fault-*` and `MessageId` exactly, and a mistake
consumes the evidence.

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
