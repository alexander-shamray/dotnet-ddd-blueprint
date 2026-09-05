# ADR-002 — Async messaging as the default

**Decision.** Services integrate through events on RabbitMQ. Synchronous calls
are the exception and require an explicit justification.
**Why.** Chained synchronous calls multiply latency and failure probability; a
service that is temporarily down should queue work, not fail requests.
**Consequences.** Eventual consistency everywhere. Debugging requires
distributed tracing. UIs must handle "in progress" states.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
