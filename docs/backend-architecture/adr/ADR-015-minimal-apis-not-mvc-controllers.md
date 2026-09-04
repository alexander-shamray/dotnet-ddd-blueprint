# ADR-015 — Minimal APIs, not MVC controllers

**Decision.** Endpoints are Minimal API groups.
**Why.** The endpoint layer translates HTTP to a command or query and does
nothing else. Controllers bring a base class, action filters and binding
conventions to do that, and the filter pipeline duplicates the dispatcher
pipeline.
**Consequences.** Endpoint classes must be organised deliberately — a single
`Program.cs` of two hundred `MapPost` calls is worse than controllers were. One
static class of extension methods per aggregate, registered from the composition
root.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
