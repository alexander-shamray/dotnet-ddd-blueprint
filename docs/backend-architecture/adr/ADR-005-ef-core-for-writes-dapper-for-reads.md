# ADR-005 — EF Core for writes, Dapper for reads

**Decision.** Aggregates persist through EF Core; queries use Dapper.
**Why.** EF Core's change tracking and rich mapping suit aggregate persistence.
For reads it adds overhead and invites accidental N+1 and over-fetching. Dapper
gives exact control over the SQL for the shapes the API returns.
**Consequences.** Two data access technologies. SQL in query handlers must be
maintained by hand when the schema changes — integration tests catch this.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
