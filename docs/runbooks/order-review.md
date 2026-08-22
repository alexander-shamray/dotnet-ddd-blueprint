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
of the four reasons are a wait that ran out, and the other two are a
cancellation the platform has no contract to undo, split by the state it was
raised from because the procedures differ.

**Check whether the saga is still running before you work the row**, because
that is not the same answer for every reason and an earlier version of this
page assumed it was:

| Reason | State of the saga |
|---|---|
| `not_despatched` | Finalised. The state row is gone and this row is the only trace |
| `stock_not_released` | Finalised, on the release timeout |
| `cancelled_after_confirmation` | Finalised — cancelled after despatch was being waited for |
| `cancelled_after_payment` | **The only one that can still be running — but usually is not.** Raised mid-wait when an authorisation lands after a cancellation, and the instance stays until `StockReleased` or the ten-minute `ReleaseTimeout`; both are well inside the hour this alerts on, so by the time you read the row it has normally finalised. Check, and branch. A `stock_not_released` row may join it |

For the finalised cases [`stuck-saga.md`](stuck-saga.md) will not catch this,
which is why §13.6 gives it a row of its own rather than folding it into the
saga-age alert. **For the last row the saga-age alert usually will not fire
either, and an earlier version of this page said it would.** Both thresholds
are an hour, but `Compensating` normally ends within the ten-minute
`ReleaseTimeout` — so by the time this row alerts the instance is long gone.
The two alerts coincide only when `StockReleased` and that timeout have *both*
failed to end the wait, and then they are the same incident.

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

Four exist, all from `ReviewReasons` in `Common.Contracts`. An unknown code is
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

### `cancelled_after_confirmation` and `cancelled_after_payment`

An order is being cancelled and its payment is authorised. Undoing an
authorisation is a **refund**, and
[§3.2](../backend-architecture/03-bounded-contexts.md) closes Payments' Accepts
column at `AuthorisePayment` — the platform has no refund contract to send. So
the money is always the reason these rows exist.

**Only one of the two is necessarily a customer cancelling**, and this
paragraph said both were. `cancelled_after_confirmation` is raised from
`Confirmed`, which the saga reaches only on an `OrderCancelled` — so something
cancelled the aggregate. `cancelled_after_payment` is raised when an
authorisation arrives while the saga is *already compensating*, and
compensation starts on a cancellation, a decline **or** a fifteen-minute
payment timeout. A slow PSP that authorises after the timeout produces that row
with nobody having cancelled anything.

**Step 1 is the same either way. Steps 2 and 3 differ, and the code is what
tells you which** — `cancelled_after_confirmation` from `Confirmed`,
`cancelled_after_payment` from `Compensating`.

**These used to be one code, and this page selected on a saga state instead.**
That does not work: `ordering.OrderReviews` persists `(OrderId, Reason,
RaisedAt)` and nothing else, and by the hour the alert measures the saga has
usually finalised — so the state the table above asks you to check is gone. An
earlier version had only the `Confirmed` procedure, which sent an on-call
looking for a despatch that does not exist; keying it on a vanished state was
the same defect one step less obvious.

1. **Refund the authorisation**, through the provider's own console or whatever
   process Payments' team owns. This is the whole reason the row exists, and it
   is the step with a clock on it — an authorisation left standing expires or
   settles depending on the provider.

#### From `Confirmed` — the saga has finalised

The order was confirmed and the saga was waiting on Shipping. Nothing is
stuck: the state row is gone by design.

**The three-day despatch timeout IS still pending**, and this line said it was
not. ADR-021's scheduler cannot cancel a delayed message, so the saga's
`Unschedule` is a no-op and `DespatchExpired` is still in the broker. It is
harmless — it will find no instance and be discarded — but "no timeout is
pending" is the kind of claim an on-call checks against a queue depth.

2. **Stop the despatch if it has not left.** The saga deliberately does *not*
   send `ReleaseStock` here: a reservation being picked is not one Inventory can
   safely be told to drop on a state machine's word. Ask Shipping, then release
   the reservation through Inventory's own API if the parcel is still in the
   warehouse.
3. **If it already shipped**, this is a return rather than a cancellation, and
   the order's own state will say so — §5.4 refuses to cancel a `Shipped`
   order, so a row here means the aggregate was cancelled before despatch.

#### From `Compensating` — the saga may still be running, and usually is not

An authorisation landed while the saga was compensating. **There is no
despatch to stop** — the order never reached `Confirmed`.

**Compensation started one of three ways and the row does not say which**: a
customer cancellation, a declined payment, or the fifteen-minute payment
timeout. The last is the one worth ruling out first, because it means
Payments answered late rather than a customer changing their mind.

**This row is raised mid-wait, which is what makes it the only one where the
instance can still exist — but the wait is short and the alert is not.** Both
of `Compensating`'s exits, `StockReleased` and the ten-minute `ReleaseTimeout`,
land well inside the hour this alerts on, so the ordinary case is a finalised
saga. An earlier version of this section asserted a live instance in its
heading and then explained two paragraphs down that there would not be one.
**So step 2 is a branch, not an instruction**:

2. **Look for the instance**, by `CorrelationId = OrderId` in the saga state
   table.
   - **Gone** — the ordinary case, but it does **not** by itself prove the
     stock came back. `Compensating` has two exits and both finalise:
     `StockReleased`, which is the reservation actually released, and the
     ten-minute `ReleaseTimeout`, which gives up on it and raises a
     `stock_not_released` row. **Check for that second row before deciding
     stock needs nothing** — if it is there, the reservation may still be
     held and [that section](#stock_not_released) is the procedure. With no
     such row, step 1's refund is the whole of the work.
   - **Still there** — both exits failed, which is its own incident. **Leave
     the reservation alone**: the machine is waiting on `StockReleased` and
     will cancel the order when it arrives, and releasing by hand races it.
3. **A live instance at this age has already missed its own timeout — do not
   wait for it again.** This row alerts at one hour and `ReleaseTimeout` is
   ten minutes, so an instance still here means the timeout never arrived,
   which is a scheduler incident rather than a slow release:
   [`stuck-saga.md`](stuck-saga.md) is the procedure, and **the saga-age
   alert will have fired too** — the saga predates this row, so at one hour
   it is over that alert's threshold as well. Two earlier versions of this
   step were wrong in opposite directions: the first said the saga-age alert
   would fire in the ordinary case, and the correction said it would not fire
   at all. It does not fire for a finalised saga and it does for a live one,
   which is the only case this step is about.

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

`cancelled_after_confirmation` is the one that does **not** follow that rule:
its upstream is customers, so a spike is a product or pricing signal rather
than a dependency, and there is no service to fix. Look at what confirmed
orders are being cancelled *for* before treating it as an incident.

**`cancelled_after_payment` follows BOTH rules, and this section used to file
it with the customer-driven one.** It is raised when an authorisation lands
while the saga is compensating, and compensation starts three ways: a
customer cancelling, a declined payment, and a **fifteen-minute payment
timeout**. That last one is an upstream fault wearing a customer-shaped code —
a PSP slower than the timeout that then authorises anyway. So a spike here is
a Payments latency signal until the orders say otherwise, and the cheap
discriminator is whether the orders carry a customer cancellation at all.
