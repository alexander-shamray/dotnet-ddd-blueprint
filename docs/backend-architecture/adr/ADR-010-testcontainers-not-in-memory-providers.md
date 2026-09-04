# ADR-010 — Testcontainers, not in-memory providers

**Decision.** Integration tests run against real SQL Server, Redis and RabbitMQ
in containers.
**Why.** The EF Core in-memory provider does not enforce foreign keys, does not
implement `rowversion` concurrency, and translates LINQ differently. Tests green
against it still fail in production.
**Consequences.** Tests need a Docker daemon and take seconds rather than
milliseconds. Mitigated by sharing containers per collection and resetting with
Respawn.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
