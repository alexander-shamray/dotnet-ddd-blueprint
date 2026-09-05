# ADR-013 — Dapr not adopted

**Decision.** No Dapr sidecars. Messaging is MassTransit over RabbitMQ; state is
EF Core over SQL Server; secrets come from the platform's secret store.
**Why.** Dapr's building blocks are genuinely useful in polyglot estates. In an
all-.NET platform they add a sidecar per pod, a control plane to operate, and an
abstraction over a broker the team already programs directly. Its state store
abstraction is the specific concern: it makes "any service can read any state"
mechanically easy, which erodes the data ownership rule this architecture is
built on.
**Consequences.** No portability across message brokers beyond what MassTransit
provides, and no free service invocation with mTLS. Revisit if services in other
languages become first-class.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
