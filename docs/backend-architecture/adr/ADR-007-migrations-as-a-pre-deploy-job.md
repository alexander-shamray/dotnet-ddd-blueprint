# ADR-007 — Migrations as a pre-deploy job

**Decision.** Never call `Database.Migrate()` at application startup.
**Why.** Multiple replicas race; rolling deploys run old code against a new
schema; and the runtime identity would need DDL permissions.
**Consequences.** Every migration must be backward compatible with the running
version. Destructive changes become multi-release sequences.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
