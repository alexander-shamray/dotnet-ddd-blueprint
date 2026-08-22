# Runbook — orders awaiting review

| | |
|---|---|
| Alert | `OrdersAwaitingReview`, in `deploy/observability/alerts/awaiting-signal.yaml` — **not loaded yet** |
| Condition | Any row in `ordering.OrderReviews` older than 1 hour |
| Signal | **Owed.** `ordering.order_reviews.oldest.age` does not exist ([§13.6](../backend-architecture/13-observability.md)) |
| Owner | The service team ([§13.8](../backend-architecture/13-observability.md)) |

> **This alert cannot fire today.** Nothing publishes an instrument over
> `OrderReviews`, so the rule sits in `awaiting-signal.yaml`, unloaded. The
> table exists and the queries below work now — the queue is real, only the
> pager is missing.

## What it means

A saga reached something it could not compensate and escalated (§9.6) — two
of the three reasons are a wait that ran out, and the third is a cancellation
the platform has no contract to undo. **It has already finalised**, so
[`stuck-saga.md`](stuck-saga.md) will not catch this — the state row is gone
and the only trace is here. That is why §13.6 gives it a row of its own rather
than folding it into the saga-age alert.

A row means "a human still needs to look at this". The table is a **work queue,
not a log**: there is no `ResolvedAt` column, and resolving a review means
deleting the row.

## The queue

```sql
SELECT
    OrderId,
    Reason,
    RaisedAt,
    AgeHours = DATEDIFF(hour, RaisedAt, SYSDATETIMEOFFSET())
FROM ordering.OrderReviews
ORDER BY RaisedAt;
```

The key is `(OrderId, Reason)`, so **one order can legitimately carry two
rows** — a release timeout raises `stock_not_released` beside whatever cancelled
the order. Two rows for one order is not a duplicate; two rows with the same
pair is impossible.

## What each reason means

Three exist, all from `ReviewReasons` in `Common.Contracts`. An unknown code is
a bug, not a new category — `FlagOrderForReviewMapper` refuses one on the
first attempt (§9.8), because `Reason` is half this table's primary key and a
typo opens a second row nobody resolves rather than overwriting the first.

### `not_despatched`

The despatch timeout fired: three days after the order was confirmed, Shipping
had not reported `ShipmentDispatched`.

**Payment has been taken and stock is gone.** This is the wait §9.6 gives no
automatic compensation precisely because there is no safe automatic answer — the
customer has paid for something that has not shipped.

Work it in this order:

1. **Did it actually ship?** Check Shipping for a despatch that happened but
   whose event never arrived. If so this is a messaging failure and the outbox
   runbooks apply on the *Shipping* side; the customer is fine.
2. **Is it still shippable?** If stock is physically there, expedite and let the
   real `ShipmentDispatched` land.
3. **If not**, this becomes a refund and a customer conversation. Neither is
   automatable from here, which is the whole reason a human was paged.

### `stock_not_released`

Compensation itself timed out: the saga cancelled an order, asked Inventory to
release the reservation, and ten minutes later `StockReleased` had not arrived.

**Stock nobody can sell.** The order is already failing — this is the worst
place for a saga to be stuck, which is why §9.6 gives compensation a timeout
like any other wait.

1. Check Inventory for the reservation. If it was released and the event was
   lost, reconcile and move on.
2. If it is still held, release it — through Inventory's own API so its
   invariants run, never by editing its tables.
3. Confirm the order really did cancel: the review row says compensation
   stalled, not that the cancellation failed.

### `cancelled_after_payment`

A customer cancelled an order whose payment had already been authorised. The
saga was in `Confirmed`, waiting on Shipping; it escalated and finalised rather
than compensating, because undoing an authorisation is a **refund** and
[§3.2](../backend-architecture/03-bounded-contexts.md) closes Payments' Accepts
column at `AuthorisePayment` — the platform has no refund contract to send.

**The order is already cancelled. What needs a person is the money**, and
possibly a parcel. Unlike the two above, nothing is stuck: the saga is gone by
design and no timeout is pending.

1. **Refund the authorisation**, through the provider's own console or whatever
   process Payments' team owns. This is the whole reason the row exists, and it
   is the step with a clock on it — an authorisation left standing expires or
   settles depending on the provider.
2. **Stop the despatch if it has not left.** The saga deliberately does *not*
   send `ReleaseStock` here: a reservation being picked is not one Inventory can
   safely be told to drop on a state machine's word. Ask Shipping, then release
   the reservation through Inventory's own API if the parcel is still in the
   warehouse.
3. **If it already shipped**, this is a return rather than a cancellation, and
   the order's own state will say so — §5.4 refuses to cancel a `Shipped`
   order, so a row here means the aggregate was cancelled before despatch.

## Resolving

**Delete the row.** That is the entire mechanism, and the absence of a
`ResolvedAt` is deliberate: a nullable timestamp nothing sets is an alert that
fires once and never clears, and "resolved" and "gone" are the same state for a
queue.

```sql
DELETE FROM ordering.OrderReviews
WHERE OrderId = @OrderId
    AND Reason = @Reason;
```

Delete one `(OrderId, Reason)` pair at a time. A bare `DELETE` over the table
clears the alert and loses the queue, and there is no undo — the audit trail of
what was escalated lives in the event history, not here.

**Do not delete to silence the alert.** The row is the only thing telling anyone
this order needs a human; the age in the alert is a service-level target for
working the queue, not a nuisance to clear.

## If the queue is growing

Many rows with the same reason is not twelve independent incidents — it is one
upstream fault. `not_despatched` in bulk means Shipping stopped despatching or
stopped publishing; `stock_not_released` in bulk means Inventory is not
consuming `ReleaseStock`. Work the upstream service and the queue drains behind
it, but the rows still need deleting — nothing removes them automatically.

`cancelled_after_payment` is the one that does **not** follow that rule: its
upstream is customers, so a spike is a product or pricing signal rather than a
dependency, and there is no service to fix. Look at what confirmed orders are
being cancelled *for* before treating it as an incident.
