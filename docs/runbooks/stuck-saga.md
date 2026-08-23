# Runbook — stuck saga

| | |
|---|---|
| Alert | `StuckSaga`, in `deploy/observability/alerts/awaiting-signal.yaml` — **not loaded yet** |
| Condition | Unfinalised over an hour outside `Confirmed`, **or over four days in it** |
| Signal | **Owed.** `ordering.saga.oldest_unfinalised.age` does not exist ([§13.6](../backend-architecture/13-observability.md)) |
| Owner | The service team ([§13.8](../backend-architecture/13-observability.md)) |

> **This alert cannot fire today, and that is recorded rather than hidden.**
> Nothing publishes a saga-age instrument, so the rule lives in
> `awaiting-signal.yaml` and is not loaded. The queries below work now — they
> read the table directly — so this runbook is usable whether the alert exists
> or not, which is the point of writing it before the signal lands.

## What it means

An order has entered §9.6's fulfilment saga and stopped advancing. Payment may
be taken, stock may be reserved, and nothing is moving it forward.

**Why `Confirmed` has its own threshold rather than the hour.** Every other
state is short: `AwaitingStock` times out in 5 minutes, `AwaitingConfirmation`
and `Compensating` in 10, and `AwaitingPayment` in 15. `Confirmed` is the wait
on Shipping, and its timeout is **three days** by design — so an hour-old saga
there is the healthy path, and the alert excludes it from the hourly branch. A
despatch that genuinely expires escalates to
[`order-review.md`](order-review.md), never here.

**The four-day branch is what catches a lost timeout**, and it exists because
excluding `Confirmed` outright left a hole with nothing on the other side of
it. If the `DespatchTimeout` is never delivered — the scheduler failures below —
the saga sits in `Confirmed` for ever, and `OrdersAwaitingReview` cannot fire
either, because that timeout is what creates the review row. So:

- **Fired on the hourly branch** → a short wait is overdue. Read the table
  below for which peer owes an answer.
- **Fired on the four-day branch** → the despatch deadline itself passed
  without escalating. Go straight to the scheduler section; this is a lost
  timeout, not a slow shipper.

## Find them

The state machine persists to `ordering.OrderFulfilmentStates` through
MassTransit's EF repository (§9.6).

```sql
SELECT
    OrderId      = CorrelationId,
    CurrentState,
    StartedAt,
    AgeMinutes   = DATEDIFF(minute, StartedAt, SYSDATETIMEOFFSET()),
    CancelReason
FROM ordering.OrderFulfilmentStates
WHERE (CurrentState <> 'Confirmed'
        AND DATEDIFF(minute, StartedAt, SYSDATETIMEOFFSET()) > 60)
    OR (CurrentState = 'Confirmed'
        AND DATEDIFF(minute, StartedAt, SYSDATETIMEOFFSET()) > 5760)
ORDER BY StartedAt;
```

**Both branches, because the alert has both.** An earlier version of this query
excluded `Confirmed` outright — so when the four-day branch paged, the first
diagnostic a responder ran returned no rows at all and the saga that caused the
page was the one row it could not show. 5760 minutes is the four days.

**A finalised saga has no row.** MassTransit deletes the instance on
`Finalize`, so anything this returns is by definition unfinalised and the query
needs no completion predicate — which is also why the alert's condition is
phrased as an age rather than as a state count.

Group by `CurrentState` first — one stuck order is a message; twenty in the same
state is a dependency.

## Read the state, then ask what it is waiting for

Each state is waiting on exactly one thing, and each has a timeout that should
already have fired.

| State | Waiting for | Timeout | If the timeout fires |
|---|---|---|---|
| `AwaitingStock` | `StockReserved` from Inventory | 5 min | Cancels the order, `stock_timeout` |
| `AwaitingPayment` | `PaymentAuthorised` / `PaymentDeclined` | 15 min | Cancels and releases stock, `payment_timeout` |
| `AwaitingConfirmation` | `OrderConfirmed` from **Ordering itself** | 10 min | Escalates to `OrderReviews`, `not_confirmed` |
| `Confirmed` | `ShipmentDispatched` from Shipping | 3 days | Escalates to `OrderReviews`, `not_despatched` |
| `Compensating` | `StockReleased` from Inventory | 10 min | Escalates to `OrderReviews`, `stock_not_released` |

**A saga older than its state's timeout is a timeout that did not arrive**, and
that is a different fault from the peer being slow. It is the first thing to
check, because it has one cause far more often than not.

**`AwaitingConfirmation` is the exception to everything below, because there is
no peer.** The saga sent `ConfirmOrder` to Ordering's own `ordering-commands`
queue and is waiting for the `OrderConfirmed` the aggregate publishes when it
commits — so a saga stuck there is **this service** failing to consume its own
command, and the scheduler section below is the wrong place to start. Go
straight to the outbox query further down and look for the `ConfirmOrder` that
never left, then at whether `ordering-commands` is being drained at all. The
aggregate *refusing* the command does not land here: that is a `Rule` failure
`CommandConsumer` acks, and the cancellation behind it moves the saga to
`Compensating` on its own event.

## The timeout scheduler is the usual culprit

[ADR-021](../backend-architecture/appendix-a-adrs.md#adr-021--saga-timeouts-are-scheduled-by-the-broker)
schedules every saga timeout on RabbitMQ's **delayed message exchange**, which
is why §14.1's RabbitMQ is the one infrastructure image that is *built* rather
than pulled. Three ways that breaks, all of which look like a hung saga:

1. **The plugin is missing.** A broker replaced with a stock image does **not**
   silently drop the scheduled message — measured, and recorded in
   `deploy/compose/rabbitmq/Dockerfile`. The bus starts clean; the first
   `.Schedule(…)` fails `exchange.declare` with `precondition_failed: unknown
   exchange type 'x-delayed-message'`, and **MassTransit then retries the
   topology for ever**. The call never returns, the transition never completes,
   and the only trace is a channel error in the **broker's** log every few
   seconds while the service stays healthy and quiet.

   That distinction is what you look for: not a missing message, but a
   repeating channel error on RabbitMQ and a saga frozen mid-transition.

   ```bash
   kubectl -n <ns> exec deploy/rabbitmq -- rabbitmq-plugins list | grep delayed
   kubectl -n <ns> logs deploy/rabbitmq --since=10m | grep -i 'precondition_failed\|x-delayed-message'
   ```

2. **The scheduler is not registered.** A registration nothing resolves at
   startup fails at the first message, not at boot — `ValidateOnBuild` never
   constructs an open generic and no host resolves a scheduler while it boots,
   so the service connects, declares, and reports ready with no scheduler at
   all. This is recorded in `CLAUDE.md` as a lesson because it cost a debugging
   session: the symptom is timeouts that never arrive and a log that says
   nothing.

3. **The broker lost its delayed messages.** A delayed exchange holds them in a
   node-local store; a node that was rebuilt loses whatever was pending. Nothing
   reports this — the messages simply never arrive.

If the plugin and registration are fine, check the broker lane is moving at all:
a stalled outbox means the saga's own `Send` never left the service, which is
[`outbox-broker.md`](outbox-broker.md) rather than this.

## Or the peer never answered

If timeouts are working and the saga is younger than its timeout, it is simply
waiting and there is nothing wrong yet. If it is *older* and the timeout fired
but the state did not change, look for the command that was sent:

```sql
SELECT
    MessageId,
    MessageType,
    Lane,
    Attempts,
    ProcessedAt,
    LastError = LEFT(LastError, 500)
FROM ordering.OutboxMessages
WHERE CorrelationId = @OrderId
ORDER BY OccurredAt;
```

A `CancelOrder` or `FlagOrderForReview` that never left is the answer, and the
outbox runbooks take it from there.

**For a saga in `AwaitingConfirmation` this is the first query rather than the
last**, and what it is looking for is a `ConfirmOrder`. There is no peer to have
gone quiet: the command is Ordering's own, so a row still sitting here unsent —
or sent and never consumed, which the `ordering-commands` queue depth shows —
is the whole fault. The order is still `AwaitingPayment` with the card
authorised, which is why that wait escalates rather than compensating when its
ten minutes run out.

## Manual compensation

**Last resort, and only with the order read first.** The saga sends commands to
`ordering-commands` (§9.4); the honest way to unstick one is to publish the
event it is waiting for, or the timeout it missed, rather than editing the state
row. Editing `CurrentState` directly leaves the scheduled messages armed and the
aggregate unaware, and produces a saga that is inconsistent with its own order.

**Most of these transitions have no HTTP route, and reaching for one is how an
incident stalls.** `Ordering.Api` exposes exactly two: `POST /api/v1/orders`
and `POST /api/v1/orders/{id}/cancel`. `ConfirmOrder`, `MarkOrderShipped` and
`FlagOrderForReview` are **broker-only** — the saga sends them to
`ordering-commands`, and that endpoint is their only ingress.

| To do this | Use |
|---|---|
| Cancel the order | The API's cancel endpoint — the domain rules run, and §11.4's ownership check applies |
| Confirm, ship, or flag for review | Publish the command to `ordering-commands`; there is no route |
| Advance the saga past a wait | Publish the *event* it is waiting for, not a command |

Whichever ingress, it goes through the domain rather than around it — §5's
`Order` refuses transitions that do not make sense, and that refusal is a
feature here.

**`AwaitingConfirmation` is where that last row bites hardest, so do not take
it literally there.** The event it waits for is `OrderConfirmed`, and
publishing one by hand would be stating a fact the aggregate has not stated —
the order would still be `AwaitingPayment`, the saga would move to `Confirmed`,
and Shipping would be told to despatch an unconfirmed order. Redeliver the
`ConfirmOrder` instead and let the aggregate publish its own acknowledgement.
The distinction the table draws is between commands and events; the rule under
it is that you may replay a message, never invent one.

Record every manual action against the `OrderId`. The next person to read this
order will find a state machine that moved without a message, and the only
explanation will be whatever you wrote down.
