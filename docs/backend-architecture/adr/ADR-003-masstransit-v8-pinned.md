# ADR-003 — MassTransit v8, pinned

**Decision.** Use MassTransit 8.x (Apache 2.0) and pin the major version.
**Why.** MassTransit v9 moved to a commercial licence in 2026 at $400–1,200 per
month. v8 remains Apache 2.0 and maintained into 2026, and its abstraction over
RabbitMQ keeps the broker replaceable.
**Consequences.** A migration decision is required when v8 maintenance ends.
Options: pay for v9, move to Wolverine, adopt a community fork, or use
`RabbitMQ.Client` directly. What preserves all four is that **no Application or
Domain code touches a MassTransit type**: publication goes through
`IIntegrationEventPublisher` ([§9.3](../09-messaging.md)) and the only MassTransit surface is the
outbox dispatcher, the consumer classes and the bus configuration — all in
Infrastructure. Note that `IPublishEndpoint` and `IBus` are MassTransit types
and so are *not* the abstraction; using them as the seam would mean abstracting
MassTransit behind MassTransit.
**Review by.** Q4 2026.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
