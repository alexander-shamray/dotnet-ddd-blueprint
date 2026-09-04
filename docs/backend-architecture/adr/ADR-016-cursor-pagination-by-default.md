# ADR-016 — Cursor pagination by default

**Decision.** Collection endpoints use opaque keyset cursors. `page`/`pageSize`
is not the default.
**Why.** `OFFSET n ROWS` costs proportionally to `n`, and results shift under
concurrent inserts, so a user paging through a live list sees duplicates and
skips.
**Consequences.** No "jump to page 47" and no cheap total count. Where a UI
genuinely needs either — an admin table over a bounded set — offset pagination
is an explicit, documented exception.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
