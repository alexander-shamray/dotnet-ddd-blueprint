# ADR-008 — YARP as the gateway

**Decision.** YARP, self-hosted, with routing/auth/rate-limiting only.
**Why.** MIT, actively maintained by Microsoft, configurable in code as well as
JSON, and it runs in the same stack the team already knows. Ocelot is
comparatively quiet; managed gateways add cloud coupling and cost.
**Consequences.** The gateway is a service to operate and scale. Its config must
stay disciplined — aggregation belongs in a BFF, not here.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
