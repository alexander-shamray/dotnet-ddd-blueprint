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
of the four reasons are a wait that ran out, and the other two are an
authorisation **Ordering** has no command to undo, split by the state each
was raised from because the procedures differ.

**"The platform has no contract to undo it" is what this said, and it is
wider than the truth.** Payments consumes `OrderCancelled` and voids an
authorisation already taken (§3.2); what Ordering lacks is a refund *command*
to send. The difference decides step 1 below, which is why the page cannot
open by collapsing it.

**Check whether the saga is still running before you work the row**, because
that is not the same answer for every reason and an earlier version of this
page assumed it was:

| Reason | State of the saga |
|---|---|
| `not_despatched` | Finalised. The state row is gone and this row is the only trace |
| `stock_not_released` | Finalised, on the release timeout |
| `cancelled_after_confirmation` | Finalised — cancelled after despatch was being waited for |
| `payment_authorised_during_compensation` | **The only one that can still be running — but usually is not.** Raised mid-wait when an authorisation lands after compensation has BEGUN — which is not the same as after a cancellation: `Compensating` is also reached from `PaymentDeclined` and the fifteen-minute payment timeout, where no `OrderCancelled` exists yet. The instance stays until `StockReleased` or the ten-minute `ReleaseTimeout`; both are well inside the hour this alerts on, so by the time you read the row it has normally finalised. Check, and branch. A `stock_not_released` row may join it |

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

### `cancelled_after_confirmation` and `payment_authorised_during_compensation`

An order is being cancelled and its payment is authorised. Undoing an
authorisation is a **refund**, and
[§3.2](../backend-architecture/03-bounded-contexts.md) closes Payments' Accepts
column at `AuthorisePayment` — Ordering has no refund *command* to send.

**That is not the same as no automatic refund, and this page read it that
way.** §3.2 also gives Payments a `Refund` aggregate, has it publish
`PaymentRefunded`, and lists `OrderCancelled` in its **Consumes** column — and
`OrderCancelled`'s own contract says in as many words that "an authorisation
that was taken is voided". Payments refunds off the event, autonomously.
Ordering simply has no way to *ask*, which is a different sentence from
nobody doing it. **So the first act is never to refund.**

**Whether the void has already happened is not knowable from the code, and
that is the whole reason step 1 is a check.** An earlier revision of this
paragraph said `cancelled_after_confirmation` has its refund on the way while
`payment_authorised_during_compensation` is beyond the automatic path's
reach. Neither half survives
[§9.4](../backend-architecture/09-messaging.md):

- The saga seeing `OrderCancelled` before an authorisation says nothing about
  when **Payments** consumed it. They are two independent consumers of two
  independent messages, and §9.4 orders nothing between them — the same fact
  that makes a release land before its own reserve (#125).
- And on the decline and payment-timeout doors **no `OrderCancelled` has been
  published yet** when the row is raised: `CancelOrder` goes on
  `Compensating`'s exit, so the cancellation — and the void that follows
  it — is still to come.

So the event is published on every path that raises either code, and on no
path can you infer from the code whether Payments has acted. **Check, on
both.**

**"Published on every path" assumes the saga gets out of `Compensating`,
and step 2 is where you find out whether it did.** Both exits finalise —
`StockReleased`, and the ten-minute `ReleaseTimeout` — so an instance still
live at the hour this alerts on has had neither, which means the cancellation
has not been sent and will not be until someone intervenes.

**Deal with the saga before the money, and this page said the opposite.** It
read "refund by hand and treat the saga as the separate incident it is",
which is a double refund by a slower route: a live instance that later
resumes — because `StockReleased` finally arrives, or because someone
restarts the scheduler — sends `CancelOrder`, the aggregate publishes
`OrderCancelled`, and Payments voids off it. A manual refund on a live
instance is therefore a refund **ahead of** an automatic one, not instead of
it. Nothing in the platform suppresses that second refund: §3.2 gives
Ordering no refund command, so it has no way to say "already done" either.

So drive the instance to an exit first — [`stuck-saga.md`](stuck-saga.md), a
live instance past its own timeout being a scheduler incident — and let the
cancellation it sends do the refunding. **If the authorisation genuinely
cannot wait**, refunding by hand is still available and you own the
reconciliation: record it, and check for `PaymentRefunded` again once the
saga finally exits, because the second one is coming and this page cannot
stop it.

**What actually separates the two is Shipping.**
`cancelled_after_confirmation` means the order reached `Confirmed`, so a
despatch may be in motion and stopping it comes first;
`payment_authorised_during_compensation` is raised from `Compensating`, which
cannot
despatch. That is the saga-state distinction this page has always drawn, and
the money is what both rows have in common rather than what tells them apart.

**Only one of the two is necessarily a customer cancelling**, and this paragraph
said both were. `cancelled_after_confirmation` is raised only when an
`OrderCancelled` reaches the saga in `Confirmed` — so something did cancel the
order. `payment_authorised_during_compensation` is raised when an authorisation
arrives while the saga is *already compensating*, and compensation starts on a
cancellation, a decline **or** a fifteen-minute payment timeout. A slow PSP that
authorises after the timeout produces that row with nobody having cancelled
anything.

**Step 1 is the same either way and its answer is not predictable from the
code. Steps 2 and 3 differ, and the code is what tells you which** —
`cancelled_after_confirmation` from `Confirmed`,
`payment_authorised_during_compensation`
from `Compensating`.

**These used to be one code, and this page selected on a saga state instead.**
That does not work: `ordering.OrderReviews` persists `(OrderId, Reason,
RaisedAt)` and nothing else, and by the hour the alert measures the saga has
usually finalised — so the state the table above asks you to check is gone. An
earlier version had only the `Confirmed` procedure, which sent an on-call
looking for a despatch that does not exist; keying it on a vanished state was
the same defect one step less obvious.

1. **Find out whether Payments already refunded it. This decides which
   branch you are in, and never on its own that you should pay.** Look for
   a `PaymentRefunded` for this order, or read the provider's own console.
   This is the same act on both codes and neither answer is the expected
   one — see above — so the check is the work, not a formality before the
   refund.

   **"Then refund only if it did not" is what this step said**, and it
   contradicted the two paragraphs directly below it: finding no refund is
   the start of the saga check, not a licence to pay. Every refund
   instruction on this page now sits after the saga's state is known, for
   the reason those paragraphs give.

   **A cancellation may still be in flight**, which is the case worth
   naming: on `payment_authorised_during_compensation` reached from a
   decline or a timeout, the `CancelOrder` that triggers the void has not
   been sent yet when this row appears. If you find no refund and no
   cancellation, **check the saga before doing anything else** — step 2 of
   the `Compensating` procedure below.

   **A gone instance is not an answer on its own; a live one means fix the
   saga, not the money.** Both exits finalise, so an instance still live at
   the hour this alerts on has missed its own ten-minute timeout and no
   `CancelOrder` is coming without intervention. Waiting there waits for
   ever — but refunding there races an automatic void that arrives the
   moment the saga is unstuck, so the fix is the saga.

   **And a gone one still has to be checked**, because #128's crash window
   can delete the instance with its `CancelOrder` never sent: look for the
   cancellation itself, not for the missing saga row. Step 2's `Gone`
   branch is the procedure. Three earlier revisions of this step were
   wrong in three different directions — one said "wait for one"
   unconditionally, the next said refund by hand and treat the saga
   separately, and the third read a deleted instance as proof the
   cancellation had been sent.

   An earlier version of this step said "refund the authorisation" with no
   check at all, which is a double refund whenever Payments got there first.

   It is still the step with a clock on it — an authorisation left standing
   expires or settles depending on the provider — so escalate rather than
   sit on it.

#### From `Confirmed` — the saga has finalised

The saga confirmed the order and was waiting on Shipping. Nothing is stuck:
the state row is gone by design.

**Check the order actually confirmed before working this as a post-despatch
cancellation.** The saga enters `Confirmed` when it *sends* `ConfirmOrder`,
not when that command commits, and Shipping is told nothing until the
aggregate publishes `OrderConfirmed`. A cancellation that beat the command to
the aggregate produces this row for an order that was never confirmed — and
then there is no despatch to stop, **and the reservation is stranded**,
because the saga withheld `ReleaseStock` expecting a picking that never
started. The order's own status is the check: `Cancelled` with no
`OrderConfirmed` ever published is that race, filed as
[#126](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/126).
Release the reservation through Inventory's API and expect a `ConfirmOrder`
in the error queue for the same order.

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
     such row, the stock needs nothing and the money is all that is left.

     **A gone instance is not proof that the cancellation was sent**, and
     this branch said it was. `SetCompletedWhenFinalized` deletes the row
     inside the transaction that commits the exit, while
     `UseInMemoryOutbox` flushes the buffered `CancelOrder` only after the
     consume pipeline returns — [#128](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/128),
     and §9.6's crash-window callout. A crash between the two leaves
     exactly this evidence: no instance, and no `CancelOrder` ever sent.
     **So check for the cancellation itself, not for the absence of a
     saga.** The order's own state, or an `OrderCancelled` row in
     Ordering's outbox, is what settles it.

     **Cancelled, and no `PaymentRefunded`** — the automatic path fired and
     produced nothing, which is a failure on Payments' side rather than a
     missing instruction on Ordering's. Check that service's error queue
     and replay the consumer, because one that succeeds on retry voids the
     authorisation and duplicates anything paid by hand. Refund manually
     only if the authorisation cannot wait for that, record it, and own the
     reconciliation — the same terms as every other manual refund here.

     **Not cancelled** — the crash window above. The order is still open,
     the money is still authorised, and nothing further is coming, because
     the only thing that was going to send `CancelOrder` no longer exists.
     **Recover the cancellation rather than the money**: send `CancelOrder`
     (§11.4's endpoint), which publishes `OrderCancelled` and gives
     Payments the void it consumes. Refunding by hand here is the same
     duplicate the live-instance branch warns about, arriving by a
     different route.

     **`PaymentRefunded` already there** — the workflow finished. Nothing
     to do.

     **This branch has now been corrected six times, and three of those
     corrected the previous correction.** Round 25 deleted step 1's refund
     instruction because it was wrong for the live branch; round 26 put it
     back here, where it is right, and asserted that a gone instance means
     the cancellation was sent; round 30 found that assertion refuted by
     this branch's own #128. The pattern is worth the lines it costs: every
     revision reasoned from *what the saga does* and each was falsified by
     *what the saga does when it stops halfway*.
   - **Still there** — both exits failed, which is its own incident. **Leave
     the reservation alone**: the machine is waiting on `StockReleased` and
     will cancel the order when it arrives, and releasing by hand races it.
     **The same is true of the money**, and it is the less obvious half:
     that cancellation publishes the `OrderCancelled` Payments voids off,
     so a manual refund now is one the automatic path will duplicate when
     the instance is unstuck. Fix the saga first.
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

**`payment_authorised_during_compensation` follows BOTH rules, and this section
used to file it with the customer-driven one.** It is raised when an
authorisation lands while the saga is compensating, and compensation starts
three ways: a customer cancelling, a declined payment, and a **fifteen-minute
payment timeout**. That last one is an upstream fault wearing a customer-shaped
code — a PSP slower than the timeout that then authorises anyway. So a spike
here is a Payments latency signal until the orders say otherwise, and the cheap
discriminator is whether the orders carry a customer cancellation at all.
