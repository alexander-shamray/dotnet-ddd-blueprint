# ADR-018 — Reactions happen after commit

**Decision.** Nothing subscribes to a domain event inside the write
transaction. The dispatcher stages outbox rows and performs no other I/O;
projections, cache invalidation and integration publishing all run afterwards,
driven by the outbox ([§7.5](../07-persistence.md)).
**Why.** An in-process handler on its own connection is a second transaction
that can commit while the aggregate rolls back. One sharing the `DbContext` is
atomic but not retryable, so a read-model bug becomes a write-path outage.
Either version deadlocks against the locks the outer transaction still holds,
under load and not under test.
**Consequences.** Read models are eventually consistent by construction, and
the lag is visible rather than pretended away. The outbox grows a second
delivery lane (`Local`) so same-service reactions get the same durability and
retry accounting as cross-service ones. A projection can be fixed and replayed
without touching the write path — which is the property that pays for the
staleness.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
