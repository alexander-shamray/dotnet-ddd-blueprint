# ADR-001 — Database per service

**Decision.** Each service owns a SQL Server database. No shared tables, no
cross-database queries.
**Why.** A shared database couples deployment, schema evolution and scaling.
Any change to a shared table requires coordinating every service that reads it,
which reintroduces the constraint microservices exist to remove.
**Consequences.** No cross-service joins or foreign keys. Some data is
duplicated. Reporting needs a separate approach — read replicas or a warehouse
fed by events.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
