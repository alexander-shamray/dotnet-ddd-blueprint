# ADR-006 — Redis for cache and coordination, never as a store of record

**Decision.** Redis holds cached read models, idempotency keys, distributed
locks and the token denylist. Nothing that must survive its loss. These are
split across **two instances with different eviction policies**: a cache
instance under `allkeys-lru`, and a coordination instance under `noeviction`
for locks, idempotency keys and the denylist. Shared rate-limit counters belong
on the coordination instance when they are built; the gateway's v1 limiter is
in-process and per-replica, and [§10.3](../10-api-gateway.md) states what that costs.
**Why.** Redis is fast and its durability guarantees are weaker than SQL
Server's. Treating it as authoritative for anything means accepting data loss.
The split exists because eviction policy is a property of the whole keyspace:
an `allkeys-lru` instance under memory pressure will evict a held lock or a
revoked-token entry with no error and no log line ([§8.1](../08-caching-redis.md)).
**Consequences.** Two Redis instances to run, two connection strings, and a
keyed-service registration so choosing the wrong one is a visible decision.
Every cached value must be reconstructible from SQL Server. A cold cache causes
a load spike on the databases, which capacity planning must allow for; a lost
coordination instance is more serious and is why it runs with persistence
enabled.

> **The token denylist named twice above is withdrawn by
> [ADR-033](ADR-033-revocation-is-bounded-by-the-token-lifetime-and-no-denylist-exists.md),
> and nothing here has been edited.** An ADR is superseded and never rewritten:
> the decision this record actually took — Redis is cache and coordination and
> never a store of record, split across two instances by eviction policy — is
> untouched and still binding, and so is every argument for it. What moved is
> one item in a list of what the coordination instance *holds*.
>
> **The Why paragraph is the part worth reading with that in mind.** It
> justifies `noeviction` partly by "a revoked-token entry" being evicted
> silently, and there has never been such an entry to evict: nothing has ever
> written the `{service}:denylist:` keyspace, and §11.3's `AddJwtBearer` reads
> no revocation list. The conclusion survives its example — a held lock and an
> idempotency claim are both real and both must not be evicted — but the
> example was doing more work than it could carry, which is how a keyspace with
> no reader kept reading as a control for four PRs.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
