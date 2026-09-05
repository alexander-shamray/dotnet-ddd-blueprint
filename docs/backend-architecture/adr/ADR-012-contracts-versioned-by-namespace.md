# ADR-012 — Contracts versioned by namespace

**Decision.** Integration events live in `Common.Contracts.<Service>.V<n>`.
Breaking changes create a new version; both are published during a deprecation
window.
**Why.** It is the only mechanism that lets producers and consumers deploy
independently, which is the entire point of the architecture.
**Consequences.** Contract changes require deliberate planning. Consumer
adoption must be tracked with telemetry before a version is retired.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
