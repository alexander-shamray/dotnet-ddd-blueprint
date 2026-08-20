# Runbook — abandoned outbox rows

| | |
|---|---|
| Alert | `OutboxAbandonedRows`, in `deploy/observability/alerts/platform-alerts.yaml` |
| Condition | `sum(outbox.abandoned.count)` > 0 |
| Signal | `OutboxMetrics`, `Ordering.Infrastructure/Observability` ([§13.6](../backend-architecture/13-observability.md)) |
| Owner | The service team ([§13.8](../backend-architecture/13-observability.md)) |

## What it means

**Silent, permanent data loss.** A message has exceeded §9.4's attempt cap of
ten, so the dispatcher's claim — `WHERE Attempts < @MaxAttempts` — skips it for
ever. Nothing will retry it, nothing else will notice, and the backlog graph
goes *green* precisely because the message was given up on.

This is why the alert has no `for` clause. Every other rule waits out a
transient; an abandoned row is already permanent by the time it is counted, so
waiting only delays the page.

## The lane says whose loss it is

Read the label before anything else, because the blast radius and the recovery
are different:

- **`Broker`** — other services never learned something. The event is gone from
  their world. Whatever they would have done on receipt has not happened, and
  will not, until this row is delivered or the effect is produced another way.
- **`Local`** — this service's own read model is permanently wrong. Users are
  looking at a listing that is missing a row and will stay missing.

```promql
sum by (service_name, lane) (outbox_abandoned_count)
```

## Read the rows before touching them

```sql
SELECT
    Id,
    MessageId,
    CorrelationId,
    MessageType,
    Lane,
    Attempts,
    OccurredAt,
    LastError = LEFT(LastError, 2000)
FROM ordering.OutboxMessages
WHERE ProcessedAt IS NULL
    AND Attempts >= 10
ORDER BY OccurredAt;
```

`LastError` is the first 2000 characters of the exception from the final
attempt, written by the dispatcher's fail statement. `CorrelationId` ties the
row back to the request that produced it, so the original trace and logs are
reachable ([§10.4](../backend-architecture/10-api-gateway.md), §13.4).

**Do not reset `Attempts` before reading `LastError`.** The reset does not clear
it, but a successful redelivery will, and then the only record of why it failed
ten times is gone.

## Deciding: repair, replay, or discard

Work out which of three things happened.

### The message is fine and the world was broken

A broker that was unreachable for longer than the backoff took to exhaust —
ten attempts with `POWER(2, min(Attempts, 8)) * 5` seconds between them is a
little over forty minutes to the cap. This is the commonest cause and the
easiest: the payload is valid, the destination is now up, and a replay is
correct.

### The message is malformed

A payload the consumer cannot deserialise, or a `MessageType` that no longer
maps. Replaying it will fail ten more times. Fix the mapping or the payload
first — and note that a payload edit is a hand-write into a table, so it wants a
second pair of eyes.

Check the type still resolves; §9.4's `MessageTypeMap` is what turns the column
into a CLR type, and `MessageTypeMapValidator` fails the host at startup on a
duplicate but says nothing about a name that has since been deleted.

### The effect already happened another way

Somebody compensated by hand during the incident. Then a replay would double
it — and whether that is harmless depends entirely on whether the consumer is
idempotent. §9.5's inbox filter makes *redelivery of the same `MessageId`*
safe, so a replay of this row is safe even if it partly succeeded before; a
manual action taken outside the system is not covered by it.

## Replaying

```sql
-- One row at a time. Named by MessageId, never by a bare predicate over the
-- whole abandoned set: the set may hold rows in all three categories above.
UPDATE ordering.OutboxMessages
SET Attempts = 0,
    LockedUntil = NULL
WHERE MessageId = @MessageId
    AND ProcessedAt IS NULL;
```

The dispatcher picks it up on the next tick. Watch it rather than assuming:

```sql
SELECT MessageId, Attempts, ProcessedAt, LEFT(LastError, 500) AS LastError
FROM ordering.OutboxMessages
WHERE MessageId = @MessageId;
```

A `ProcessedAt` that fills in is a delivery. An `Attempts` that starts climbing
again is the second category above, and stopping to fix the cause is cheaper
than watching it reach ten a second time.

## Discarding

Only when the effect is confirmed produced another way, and only with the
decision written down somewhere durable.

```sql
UPDATE ordering.OutboxMessages
SET ProcessedAt = SYSDATETIMEOFFSET()
WHERE MessageId = @MessageId
    AND ProcessedAt IS NULL;
```

Marking processed rather than deleting is deliberate: the row stays readable
until the retention purge removes it, so the decision remains auditable for the
retention window. **Never `DELETE`** — §9.4's purge predicate refuses to touch
unprocessed rows for exactly this reason, and a hand-written delete steps around
the one guard protecting them.

## Afterwards

An abandoned row means the retry policy did not fit the failure. Worth asking in
the postmortem: was forty minutes to the cap right for this dependency? The cap
lives once, as `OutboxDispatcher.MaxAttempts`, and §13.6's gauge reads that same
constant — so changing it moves the alert with it rather than leaving the two
disagreeing.
