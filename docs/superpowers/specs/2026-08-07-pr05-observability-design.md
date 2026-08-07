# PR-05 — OpenTelemetry and structured logging defaults

Design for Appendix C's PR-05, `feat(common): OpenTelemetry and structured
logging defaults`, which depends on PR-03 and delivers, in Appendix C's words:
"`Common.Web`: OTLP export, resource attributes, health endpoint wiring, log
redaction policy".

The specification is [§13.2](../../backend-architecture/13-observability.md),
[§13.4](../../backend-architecture/13-observability.md) and
[§13.5](../../backend-architecture/13-observability.md). This document records
only the decisions those chapters do not already settle — the places where the
blueprint describes a finished system and PR-05 arrives at PR-05.

## 1. The package move

PR-01 pinned the Telemetry group at 1.13.1 and 1.13.0 against an empty
repository. Nothing referenced them, so nothing restored them, so nothing
audited them. The moment `Common.Web` takes the first `PackageReference`, four
advisories surface:

```
error NU1902: Package 'OpenTelemetry.Api' 1.13.1 has a known moderate severity
              vulnerability, GHSA-g94r-2vxg-569j
error NU1902: Package 'OpenTelemetry.Exporter.OpenTelemetryProtocol' 1.13.1
              — GHSA-4625-4j76-fww9, GHSA-mr8r-92fq-pj8p, GHSA-q834-8qmm-v933
```

`TreatWarningsAsErrors` (ADR-019) promotes NU1902 to an error, so PR-05 cannot
compile on the current pins. This is not a judgement call and not a
nice-to-have; it is a prerequisite.

**Decision.** The Telemetry group moves to **1.17.0**. The two instrumentation
packages that stay unreferenced move with it, to **1.17.0-beta.1**, rather than
being left three minors behind their siblings for a reason no reader could
reconstruct.

Verified rather than assumed: a probe project at 1.17.0, built under the repo's
own analyser policy — `TreatWarningsAsErrors`, `EnableNETAnalyzers`,
`AnalysisLevel latest-Recommended` — compiles §13.2's block verbatim with zero
warnings. That covers the three API surfaces that could plausibly have been
gated behind an experimental diagnostic and are not: `UseOtlpExporter()`, the
`LogRecord.Attributes` setter §13.4's redactor writes through, and
`AddInMemoryExporter`.

| Package | From | To |
|---|---|---|
| `OpenTelemetry.Extensions.Hosting` | 1.13.1 | 1.17.0 |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol` | 1.13.1 | 1.17.0 |
| `OpenTelemetry.Instrumentation.AspNetCore` | 1.13.0 | 1.17.0 |
| `OpenTelemetry.Instrumentation.Http` | 1.13.0 | 1.17.0 |
| `OpenTelemetry.Instrumentation.Runtime` | 1.13.0 | 1.17.0 |
| `OpenTelemetry.Instrumentation.EntityFrameworkCore` | 1.13.0-beta.1 | 1.17.0-beta.1 |
| `OpenTelemetry.Instrumentation.StackExchangeRedis` | 1.13.0-beta.1 | 1.17.0-beta.1 |
| `OpenTelemetry.Exporter.InMemory` | — | 1.17.0, Test group |

This lands as its own commit. It is a security fix that happens to be a
prerequisite, not part of the feature, and a reader bisecting either one should
not have to read the other.

## 2. What `AddObservability` registers, and what waits

§13.2 names seven meters and six instrumentations. Two of the six instrument
libraries this repository does not yet contain:
`AddEntityFrameworkCoreInstrumentation` (EF Core arrives at PR-08) and
`AddRedisInstrumentation` (Redis arrives at PR-12, and the call needs a
registered `IConnectionMultiplexer` to attach to).

**Decision.** Everything that costs a string lands now; the two calls that cost
a package reference wait for the package they instrument.

The split is not arbitrary. `AddMeter` and `AddSource` take bare names, so all
seven meters and `AddSource("MassTransit")` register today at no cost — and
§13.2 argues at length that the meter list must be complete, because a
condition whose signal is not registered "looks configured and is silent, which
is worse than having no alert at all". The two deferred calls are different in
kind: each would add a package reference to `Common.Web` for a library nothing
in the repository uses. That is the rule PR-02 already applied when it declined
to give `Common.Application` an unused reference to `Common.Domain` — a package
reference is a claim about the dependency graph, and this one would not yet be
true.

The third deferral is `AddCommonWebDefaults`' authentication block. Appendix C
gives JWT validation in `Common.Web` to **PR-16**, and the `"authenticated"`
policy cannot be registered without it: a policy requiring an authenticated
user, with no scheme registered to authenticate one, rejects every request that
reaches it.

So `AddCommonWebDefaults` lands with three of §13.2's five pieces —
`AddObservability`, `AddCommonProblemDetails` and `AddHealthChecks` — and a
comment naming what PR-16 adds.

## 3. Types

Five files in `src/BuildingBlocks/Common.Web/`, following the existing
`*Extensions` naming.

| File | Contents |
|---|---|
| `BuildInfo.cs` | The version stamped onto the OTel resource. Reads the entry assembly's `AssemblyInformationalVersionAttribute`, strips the `+<sha>` source-revision suffix a deterministic build appends, and falls back to `0.0.0` |
| `ObservabilityExtensions.cs` | `AddObservability` — §13.2 minus the two deferred lines |
| `SensitiveDataRedactor.cs` | §13.4 verbatim |
| `HealthCheckExtensions.cs` | `MapCommonHealthEndpoints` — §13.5 verbatim: three endpoints, all `AllowAnonymous` |
| `CommonWebDefaultsExtensions.cs` | `AddCommonWebDefaults` — the three pieces above |

`BuildInfo` gets no chapter block. Appendix D lists it under **D.5 — referenced
but deliberately not shown**, whose stated rule is that such types are
"ordinary, and writing them out would add length without insight". Defining it
in a chapter now would contradict that decision rather than fill a gap.

`Common.Web.csproj` gains five `PackageReference` entries, versionless as
central package management requires: `OpenTelemetry.Extensions.Hosting`,
`OpenTelemetry.Exporter.OpenTelemetryProtocol` and the AspNetCore, Http and
Runtime instrumentations.

## 4. Documentation reconciled in the same PR

CLAUDE.md's one rule spans code and blueprint from the moment code lands, so
every disagreement this PR creates is closed inside it.

| Where | Change |
|---|---|
| `Directory.Packages.props` | The table in §1 |
| `appendix-b-licences.md` | A new row for `OpenTelemetry.Exporter.InMemory` beside the other test packages in the *Chosen* table. A separate row rather than an eighth name on the runtime OpenTelemetry row: that row's role reads "Telemetry, including logging", which does not describe a test exporter, and appending would falsify its own "All seven written out" sentence for no gain |
| §13.2 | One paragraph naming the three deferred lines and the PRs that add them. Citing a PR number in a chapter is established — §4.1 cites PR-07 and PR-01, §6.5 cites PR-09 |
| §13.4 | The test-placement paragraph moves from `Ordering.Api.Tests` to `Common.Web.Tests` — see §5 below |
| `appendix-d-type-inventory.md` | Add `AddObservability` to the `Common.Web` host-extensions row, which lists `UseCorrelationId`, `MapCommonHealthEndpoints` and `AddCommonWebDefaults` but not the extension §13.2 defines. D.4's own rule requires it |
| `CLAUDE.md` | The phase section (PR-06 next), the tree's `Common.Web` line, and the sentence claiming `Common.Web` "holds §10.4 and §10.5 and nothing else until PR-05 adds observability" — which PR-05 is precisely what falsifies. Also the test count, which already reads 88 against an actual 91: PR-04's last two commits landed tests after that sentence was written |

### Why §13.4 is the document that is wrong

§13.4 places the redactor test in `Ordering.Api.Tests`, which will not exist
until PR-18, and justifies it as "a `Common.Web` behaviour tested once rather
than once per host, in the suite that already owns host-level concerns".

The argument is right and the conclusion no longer follows. `Common.Web.Tests`
exists as of PR-03, already owns `Common.Web`'s behaviour, and satisfies
"tested once" better than any service suite can — a `Common.Web` behaviour
asserted in Ordering's suite is one that moves house if Ordering ever does.
The paragraph was written before `Common.Web.Tests` was in the plan. It is a
blueprint bug, fixed in the blueprint.

## 5. Tests

Four files in `tests/Common.Web.Tests/`, which gains one `PackageReference`:
`OpenTelemetry.Exporter.InMemory`.

**`SensitiveDataRedactorTests`**

- §13.4's test verbatim: `Password` is redacted and `User` survives intact.
- Substring matching, which is the reason the list is matched by `Contains`
  rather than equality: `NewPassword`, `card_number` and `Authorization`.
- A record with no sensitive attribute is not copied. This pins the
  `scrubbed ??=` fast path, which exists because the processor runs on every
  log record on every request — nothing else would catch its removal.
- A record whose `Attributes` is null does not throw.

**`HealthEndpointTests`**

- `/health/live` returns 200 with no checks registered at all.
- `/health/live` stays 200 while a `ready`-tagged check is unhealthy. This is
  §13.5's "liveness must not check dependencies" rule made executable, and it
  is the one whose failure mode is a restart storm.
- `/health/ready` returns 503 for that same check, and 200 when it passes.
- `/health/ready` ignores an `observe`-tagged unhealthy check — §13.5's rule
  that the outbox backlog must not gate readiness.
- All three endpoints carry `AllowAnonymous` metadata. Asserted on the metadata
  rather than by making an unauthenticated request, because there is no
  authentication scheme to be anonymous against until PR-16.

**`ObservabilityTests`**

- A host calling `AddObservability` builds and starts, and its `MeterProvider`
  and `TracerProvider` resolve.
- Every meter name §13.2 requires is collected: record on each, assert each
  reaches an in-memory reader.
- The `/health` tracing filter suppresses a probe's server span, and a request
  to any other path still produces one.
- Resource attributes carry `service.name` and `deployment.environment`.

**`BuildInfoTests`**

- A version is always produced, never null or empty.
- The `+<sha>` suffix a deterministic build appends is stripped.

### Two risks, neither assumed away

**The meter-coverage test needs a second exporter alongside
`UseOtlpExporter()`, and OpenTelemetry documents restrictions on combining that
call with others.** This is verified by probe before the test is written. If
the combination is rejected, the fallback is to assert against the configured
provider state — not to weaken the test into one that still passes with an
`AddMeter` line deleted. A test that cannot fail on the thing §13.2 calls
silent is not worth the file it costs.

**`UseOtlpExporter()` will attempt localhost:4317 during tests and flush on
shutdown with a ten-second default timeout.** If that shows up as slow tests,
the fix is to pin the timeout down through `OTEL_EXPORTER_OTLP_TIMEOUT` in the
test host, not to omit the exporter — omitting it would stop testing the line
the PR exists to add.

## 6. Out of scope

Named here because each is a plausible misreading of "observability defaults",
and each belongs to a PR that already owns it:

- **JWT bearer validation and the `"authenticated"` policy** — PR-16.
- **EF Core and Redis instrumentation** — PR-08 and PR-12, with their packages.
- **`MetricsInitialiser`, `OutboxMetrics`, `MessagingMetrics`** — §13.6 types
  in `Ordering.Infrastructure`, which does not exist.
- **`AddCommonWebDefaults` being called by a host** — no host exists until
  PR-07. The tests build a host themselves.
- **Runbooks, dashboards and the SLO run** — PR-24.

## 7. Done means

- `dotnet build Platform.slnx` clean, with no new suppression in
  `Directory.Build.props`. A fourth entry there is a decision about the policy,
  not about this PR.
- `dotnet test Platform.slnx` green. The baseline measured before this PR is
  **91** — 21 domain, 46 application, 24 web — and every one of them still
  passes alongside the new ones.
- `/validate-blueprint` clean.
- The licence gate passes, which it will not until the InMemory exporter
  reaches Appendix B in this same change.
