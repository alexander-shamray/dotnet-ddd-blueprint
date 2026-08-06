# Appendix B — Dependency licence register

Between 2024 and 2026 several long-standing free .NET libraries moved to
commercial licences. The register below is the state at the time of writing;
**verify before adopting**, because this is exactly the category of fact that
goes stale.

**The backticked names in the Package column are read by a machine.** They are
NuGet package identities, and the licence gate PR-01 ships
(`.github/licence-gate/`) matches them against `Directory.Packages.props` — a
pin with no identity here fails the build before anything compiles
([§15.1](15-cicd-deployment.md)). The props file, not the transcription of it in
[§4.4](04-solution-structure.md): the gate checks that the two agree, but it is
the file CI restores that decides what a licence obligation is.

So a product named in prose and a package named in backticks are different
claims: prose says what the thing is, backticks say what `restore` will resolve.
Adding a dependency means adding its identity here, not just its name.

## Chosen — free for commercial use

| Package | Licence | Role |
|---|---|---|
| ASP.NET Core, EF Core (`Microsoft.EntityFrameworkCore.SqlServer`), YARP (`Yarp.ReverseProxy`), HybridCache (`Microsoft.Extensions.Caching.Hybrid`) | MIT | Framework. ASP.NET Core is the one name in this row with no identity beside it, because it is a framework reference rather than a package and has nothing to pin |
| `Dapper` | Apache 2.0 | Read-side data access |
| MassTransit **8.x** (`MassTransit.RabbitMQ`) | Apache 2.0 | Messaging |
| `StackExchange.Redis` | MIT | Redis client |
| `FluentValidation` | Apache 2.0 | Input validation |
| `Scrutor` | MIT | Assembly scanning for handler registration ([§6.2](06-cqrs.md)) |
| OpenTelemetry .NET (`OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Instrumentation.Runtime`, `OpenTelemetry.Instrumentation.EntityFrameworkCore`, `OpenTelemetry.Instrumentation.StackExchangeRedis`) | Apache 2.0 | Telemetry, including logging ([§13.2](13-observability.md)) — there is no separate logging library. All seven written out rather than abbreviated to a prefix, for the reason the health-check row below gives |
| Polly (`Microsoft.Extensions.Http.Resilience`) | MIT | Resilience |
| `AspNetCore.HealthChecks.SqlServer`, `AspNetCore.HealthChecks.Redis`, `AspNetCore.HealthChecks.Rabbitmq` (Xabaril) | Apache 2.0 | The readiness checks §13.5 registers. Written out in full rather than abbreviated to `.Redis`, so a text comparison against the pins in `Directory.Packages.props` matches on the whole name — an abbreviation here is satisfied by `StackExchange.Redis` and the check passes while the row is missing. The one third-party runtime dependency that is easy to leave off this table, because "health checks" sound like framework and are not |
| gRPC for .NET (`Grpc.AspNetCore`, `Grpc.Net.ClientFactory`, `Grpc.Tools`, `Google.Protobuf`) | Apache 2.0 / BSD-3 | The BFF's synchronous hop and its `.proto` contract ([§9.7](09-messaging.md)) |
| `Microsoft.AspNetCore.Mvc.Testing` | MIT | `WebApplicationFactory` for the API-contract tests ([§12.4](12-test-strategy.md)) |
| `Microsoft.AspNetCore.TestHost` | MIT | `TestServer` for the host building blocks, which have no entry point for `WebApplicationFactory` to find ([§10.4](10-api-gateway.md)) |
| xUnit v3 (`xunit.v3`, `xunit.runner.visualstudio`) | Apache 2.0 | Test framework, and the VSTest adapter through which `Microsoft.NET.Test.Sdk` discovers its tests. Two identities rather than one because the adapter is a separate package and omitting it is silent: the build succeeds, `dotnet test` reports no tests and exits zero ([§12.1](12-test-strategy.md)) |
| `NSubstitute` | BSD-3 | Mocking |
| `Shouldly` *or* `AwesomeAssertions` | BSD-3 / Apache 2.0 | Assertions. An either/or row: only the chosen library is pinned in §4.4, and the licence gate excludes the other rather than reporting it as a dropped pin |
| Testcontainers for .NET (`Testcontainers.MsSql`, `Testcontainers.Redis`, `Testcontainers.RabbitMq`) | MIT | Integration test infrastructure |
| `Respawn` | MIT | Test database reset |
| `WireMock.Net` | Apache 2.0 | HTTP stubbing |
| `Microsoft.NET.Test.Sdk` | MIT | The test host every test project needs; pinned because a major bump changes discovery |
| `Microsoft.Extensions.TimeProvider.Testing` | MIT | `FakeTimeProvider` — the clock seam §12.7 requires |
| `NetArchTest.Rules` | MIT | The architecture gates ([§4.2](04-solution-structure.md)) that PR-07 makes a build failure rather than a review comment ([Appendix C](appendix-c-delivery-plan.md)) |
| `Aspire.Hosting.*` (`AppHost`, `SqlServer`, `Redis`, `RabbitMQ`, `Keycloak`) | MIT | Topology for the optional AppHost of [§14.2](14-local-development.md). Registered ahead of use: Compose is the baseline (§14.1) and Aspire is not adopted. **If** it is, `src/AppHost` is the only project taking these, which is what keeps it deletable. **Not pinned in [§4.4](04-solution-structure.md)** — see the carve-out there; adopting Aspire means adding the pins in the same change |
| `Aspire.*` client integrations (one per resource a service consumes) | MIT | The service-side half of §14.2. Registered ahead of use: **if** Aspire is adopted each service takes the integrations for its own resources, which is why backing it out costs a line per resource per service rather than deleting one project. Unpinned on the same terms as the row above |
| Keycloak | Apache 2.0 | Identity provider |
| RabbitMQ | MPL 2.0 | Message broker |

## Avoided — commercial, with the replacement used here

| Package | Change | Replacement in this blueprint |
|---|---|---|
| **MediatR** v13+ | Commercial from 2025 | Hand-rolled dispatcher (§6.2) |
| **AutoMapper** v15+ | Commercial from 2025 | Explicit mapping, by hand (§9.3). A source generator such as Mapperly is the alternative, and is not pinned here because nothing in the blueprint uses one |
| **MassTransit** v9 | Commercial from 2026, $400–1,200/month | Pinned to v8 (Apache 2.0) |
| **FluentAssertions** v8+ | Per-developer-per-year from 2025 | Shouldly, or AwesomeAssertions (a fork of v7) |

## Requires a licence review

| Component | Condition |
|---|---|
| SQL Server | Per-core or CAL licensing; Developer Edition is free for non-production only |
| Duende IdentityServer | Free below a revenue threshold; commercial above it |
| Redis | Redis 7.4+ is under RSALv2/SSPLv1. Valkey (BSD) is a drop-in fork if the terms are a problem |

PR-01 ships the CI step that enforces the first table. `.github/licence-gate/`
fails the build on three things, and the third is the one worth naming because
it runs the other way:

- a pin in `Directory.Packages.props` absent from this register;
- a registered licence outside its allow-list;
- a **registered identity that is pinned nowhere** — a dropped pin, or a row
  that outlived its dependency. The carve-outs of [§4.4](04-solution-structure.md)
  are encoded as exceptions to this one: the Aspire rows are deliberately
  unpinned, and an either/or row expects only its chosen half to appear.

Discovering a licence obligation at renewal time is considerably more expensive
than discovering it at build time.

**The table above is beyond its reach, and deliberately so.** SQL Server, Duende
and Redis are products rather than packages — nothing restores them, so nothing
reading `Directory.Packages.props` can see them, and the client libraries that
talk to them sit in the first table under their own licences. That boundary is a
review obligation and there is no tooling behind it.

---

[← Appendix A](appendix-a-adrs.md) · [Index](README.md) · [Appendix C →](appendix-c-delivery-plan.md)
