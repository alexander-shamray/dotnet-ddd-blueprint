# PR-05 — OpenTelemetry and structured logging defaults: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give `Common.Web` the OpenTelemetry wiring, health endpoints and log
redaction that every service host will compose through one call, per
[§13.2](../../backend-architecture/13-observability.md),
[§13.4](../../backend-architecture/13-observability.md) and
[§13.5](../../backend-architecture/13-observability.md).

**Architecture:** Five new types in `src/BuildingBlocks/Common.Web/`, all
static extension classes except two. `AddObservability` configures the logging
pipeline, metrics, tracing and OTLP export on an `IHostApplicationBuilder`;
`AddCommonWebDefaults` composes it with the problem-details customisation
PR-03 already shipped and an empty liveness health-check registration;
`MapCommonHealthEndpoints` maps the three probes after `builder.Build()`.
`SensitiveDataRedactor` is a log processor on that same pipeline, and
`BuildInfo` supplies the version on the OTel resource.

**Tech Stack:** .NET 10, C# 14, OpenTelemetry .NET 1.17.0, xunit.v3, Shouldly,
`Microsoft.AspNetCore.TestHost`, `OpenTelemetry.Exporter.InMemory`.

**Design spec:**
`docs/superpowers/specs/2026-08-07-pr05-observability-design.md`

## Global Constraints

Every task's requirements implicitly include this section.

- **Never add a `Version=` attribute to a `PackageReference`.** Versions live
  in `Directory.Packages.props` as **exact** pins (central package management).
- **Adding a package means adding its backticked identity to
  `appendix-b-licences.md` in the same change**, or the licence gate fails the
  build before anything compiles.
- **No `#pragma` suppressions.** A warranted suppression goes in
  `Directory.Build.props` with a comment. **A fourth entry there is a decision
  about the policy, not about the file in front of you** — prefer changing the
  code, and raise it rather than taking it.
- `TreatWarningsAsErrors` is on, `AnalysisLevel` is `latest-Recommended`. A
  warning stops the build.
- **British spelling in prose, identifiers keep their real spelling.**
  `behaviour`, `licence`, `normalise` — but `IPipelineBehavior`,
  `AddAuthorization`, `AddCommonWebDefaults`.
- **Prose wraps at 80 columns.** Code may run to 120.
- C# style: file-scoped namespaces with **a blank line after the namespace
  declaration** (IDE0055 makes this a build error), explicit types for locals
  except the four `var` carve-outs, braces optional for a single statement and
  required for two or more, `(` ends the line it opens while `[` and `{` take
  their own line, continuations indent four, operators at end of line,
  materialise with `[.. sequence]` rather than `.ToArray()`/`.ToList()`.
- **Commit messages are semantic and present-tense**, and bodies argue the
  change rather than listing it.
- Run `dotnet build Platform.slnx` and `dotnet test Platform.slnx` from the
  repository root. The baseline before this PR is **91 passing tests** — 21
  in `Common.Domain.Tests`, 46 in `Common.Application.Tests`, 24 in
  `Common.Web.Tests`.

### Verified environment facts

These were established by probe before this plan was written. Do not
re-litigate them; do not "simplify" the workarounds away.

1. **OpenTelemetry 1.13.x cannot be used.** Four NU1902 advisories, promoted to
   errors by `TreatWarningsAsErrors`. 1.17.0 is clean.
2. **A provider must be resolved before the instruments it collects are
   created.** An instrument or `ActivitySource` created before
   `GetRequiredService<MeterProvider>()` / `<TracerProvider>()` has no listener,
   and the test silently measures nothing.
3. **`UseOtlpExporter()` coexists with an in-memory exporter** added afterwards
   through a second `AddOpenTelemetry()` call on the same `IServiceCollection`.
4. **`UseOtlpExporter()` costs 8.3 seconds per host in tests**, blocking on a
   collector that is not running. `OTEL_EXPORTER_OTLP_TIMEOUT=200` supplied
   through `IConfiguration` brings it to 0.44 s. Measured, both numbers.
5. **ASP.NET Core instrumentation produces no server spans under
   `TestServer`.** An end-to-end assertion on the `/health` trace filter is not
   writable; assert the wired `Filter` predicate through
   `IOptionsMonitor<AspNetCoreTraceInstrumentationOptions>` instead.
6. **`ParentProvider.GetResource()` is public**, so a `BaseExporter<Metric>`
   subclass can capture the resource for assertion.
7. **Test-project analyser traps**, all of which are errors here: xUnit1051
   (pass `TestContext.Current.CancellationToken` to anything accepting one),
   CA1848 (`LoggerMessage.Define`, not `logger.LogInformation`), CA1727
   (PascalCase log placeholders), CA1859 (return the concrete type from a
   private helper).

---

## File Structure

**Created — `src/BuildingBlocks/Common.Web/`**

| File | Responsibility |
|---|---|
| `BuildInfo.cs` | The assembly version stamped onto the OTel resource |
| `SensitiveDataRedactor.cs` | Log processor enforcing §13.4's never-log list |
| `HealthCheckExtensions.cs` | `MapCommonHealthEndpoints` — the three probes |
| `ObservabilityExtensions.cs` | `AddObservability` — logging, metrics, tracing, OTLP |
| `CommonWebDefaultsExtensions.cs` | `AddCommonWebDefaults` — the one call a host makes |

**Created — `tests/Common.Web.Tests/`**

| File | Responsibility |
|---|---|
| `BuildInfoTests.cs` | Version is produced and carries no `+sha` |
| `SensitiveDataRedactorTests.cs` | Redaction, substring matching, the null guard, the copy/no-copy pair |
| `HealthEndpointTests.cs` | Probe status codes and anonymity |
| `TelemetryHost.cs` | Test host builder with the OTLP timeout pinned down |
| `ObservabilityTests.cs` | Meter coverage, resource attributes, the trace filter |
| `CommonWebDefaultsTests.cs` | The composition resolves |

**Modified**

| File | Change |
|---|---|
| `Directory.Packages.props` | Telemetry group to 1.17.0; add the InMemory exporter |
| `src/BuildingBlocks/Common.Web/Common.Web.csproj` | Five `PackageReference` entries |
| `tests/Common.Web.Tests/Common.Web.Tests.csproj` | One `PackageReference` |
| `docs/backend-architecture/appendix-b-licences.md` | The InMemory exporter row |
| `docs/backend-architecture/13-observability.md` | §13.2 deferral note; §13.4 test home |
| `docs/backend-architecture/appendix-d-type-inventory.md` | `AddObservability` |
| `CLAUDE.md` | Phase, tree, `Common.Web` scope, test count |

---

## Task 1: Package move — OpenTelemetry 1.17.0

The bug first: `Common.Web` cannot reference OpenTelemetry at all on the
current pins.

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `src/BuildingBlocks/Common.Web/Common.Web.csproj`
- Modify: `tests/Common.Web.Tests/Common.Web.Tests.csproj`
- Modify: `docs/backend-architecture/appendix-b-licences.md`

**Interfaces:**
- Consumes: nothing.
- Produces: `Common.Web` compiles against `OpenTelemetry`,
  `OpenTelemetry.Logs`, `OpenTelemetry.Metrics`, `OpenTelemetry.Resources`,
  `OpenTelemetry.Trace` and `OpenTelemetry.Instrumentation.AspNetCore`.
  `Common.Web.Tests` compiles against `OpenTelemetry.Exporter.InMemory`.

- [ ] **Step 1: Add the package references that expose the bug**

In `src/BuildingBlocks/Common.Web/Common.Web.csproj`, add a new `ItemGroup`
after the existing `FrameworkReference` group:

```xml
  <ItemGroup>
    <!-- §13.2's AddObservability. Versionless: central package management
         owns the pins (§4.4). The EF Core and Redis instrumentations are
         deliberately absent — they arrive with the packages they instrument,
         at PR-08 and PR-12. -->
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" />
    <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" />
    <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Http" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Runtime" />
  </ItemGroup>
```

- [ ] **Step 2: Run the build to verify it fails**

Run: `dotnet build Platform.slnx`

Expected: FAIL, four times, with

```
error NU1902: Warning As Error: Package 'OpenTelemetry.Api' 1.13.1 has a known
              moderate severity vulnerability
error NU1902: Warning As Error: Package
              'OpenTelemetry.Exporter.OpenTelemetryProtocol' 1.13.1 ...
```

If it does **not** fail this way, stop and report — the premise of this task
has changed and the versions below may no longer be the right ones.

- [ ] **Step 3: Bump the pins**

In `Directory.Packages.props`, in the `Telemetry` `ItemGroup`, set every
OpenTelemetry version and add the test exporter. Leave the three
`AspNetCore.HealthChecks.*` pins alone — nothing references them yet.

```xml
    <PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="1.17.0" />
    <PackageVersion Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.17.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.17.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.Http" Version="1.17.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.Runtime" Version="1.17.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.EntityFrameworkCore" Version="1.17.0-beta.1" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.StackExchangeRedis" Version="1.17.0-beta.1" />
```

In the `Test` `ItemGroup`, add:

```xml
    <!-- The in-memory reader §13.4's redaction test and §13.2's meter-coverage
         test read. Test-only: nothing in src/ exports in memory. -->
    <PackageVersion Include="OpenTelemetry.Exporter.InMemory" Version="1.17.0" />
```

- [ ] **Step 4: Reference the test exporter**

In `tests/Common.Web.Tests/Common.Web.Tests.csproj`, add to the existing
package `ItemGroup`:

```xml
    <!-- Reads log records and metrics back out of a real OpenTelemetry
         pipeline, so the redactor and the meter list are asserted through the
         path a host actually uses rather than through a stub. -->
    <PackageReference Include="OpenTelemetry.Exporter.InMemory" />
```

- [ ] **Step 5: Add the licence register row**

In `docs/backend-architecture/appendix-b-licences.md`, in the **Chosen — free
for commercial use** table, immediately after the
`Microsoft.Extensions.DependencyInjection` row, add:

```markdown
| `OpenTelemetry.Exporter.InMemory` | Apache 2.0 | Reads log records and metrics back out of a real pipeline for the §13.2 and §13.4 tests. A separate row from the runtime OpenTelemetry entry above rather than an eighth name on it: that row's role is the telemetry every host exports, and this package is referenced by no project under `src/` |
```

Do **not** append this identity to the existing OpenTelemetry row — that row's
sentence counts its own contents ("All seven written out rather than
abbreviated to a prefix"), and adding an eighth without touching the sentence
falsifies it.

- [ ] **Step 6: Run the build and the licence gate to verify they pass**

```bash
dotnet build Platform.slnx
cd .github/licence-gate && python -m unittest && python licence_gate.py
```

Expected: build succeeds with 0 warnings; the gate's own tests pass and
`licence_gate.py` exits 0.

- [ ] **Step 7: Run the tests to verify nothing regressed**

Run: `dotnet test Platform.slnx`
Expected: PASS, 91 tests.

- [ ] **Step 8: Commit**

```bash
git add Directory.Packages.props src/BuildingBlocks/Common.Web/Common.Web.csproj \
        tests/Common.Web.Tests/Common.Web.Tests.csproj \
        docs/backend-architecture/appendix-b-licences.md
git commit -F - <<'MSG'
chore: move OpenTelemetry to 1.17.0 ahead of referencing it

PR-01 pinned the Telemetry group against an empty repository. Nothing
referenced those packages, so nothing restored them and nothing audited
them; the first PackageReference surfaces four known advisories, and
TreatWarningsAsErrors makes NU1902 an error rather than a log line.

1.17.0 is clean and compiles §13.2's block verbatim under the repo's own
analyser policy. The two instrumentation packages that stay unreferenced
move with it rather than sitting three minors behind their siblings for a
reason no later reader could reconstruct.

The in-memory exporter is test-only and takes its own licence row. The
runtime OpenTelemetry row counts its own contents, and an eighth name
added there without rewriting the sentence would make it wrong.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
MSG
```

---

## Task 2: `BuildInfo`

**Files:**
- Create: `src/BuildingBlocks/Common.Web/BuildInfo.cs`
- Test: `tests/Common.Web.Tests/BuildInfoTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public static class Common.Web.BuildInfo` with
  `public static string Version { get; }` — never null, never empty, never
  containing `+`. Consumed by `AddObservability` in Task 5.

- [ ] **Step 1: Write the failing test**

Create `tests/Common.Web.Tests/BuildInfoTests.cs`:

```csharp
using Common.Web;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

public class BuildInfoTests
{
    [Fact]
    public void A_version_is_always_produced()
    {
        BuildInfo.Version.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_source_revision_suffix_is_stripped()
    {
        // A deterministic build stamps AssemblyInformationalVersion as
        // "1.2.3+<sha>". The sha belongs in neither service.version nor any
        // dashboard grouping by it — it would make every rebuild a new series.
        BuildInfo.Version.ShouldNotContain("+");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Platform.slnx`
Expected: FAIL to compile, `error CS0103: The name 'BuildInfo' does not exist`.

- [ ] **Step 3: Write the implementation**

Create `src/BuildingBlocks/Common.Web/BuildInfo.cs`:

```csharp
using System.Reflection;

namespace Common.Web;

/// <summary>
/// The version stamped onto the OpenTelemetry resource as
/// <c>service.version</c> (§13.2), so a trace or a metric says which build
/// produced it.
/// </summary>
public static class BuildInfo
{
    /// <summary>
    /// The entry assembly's informational version, without the source-revision
    /// suffix, or <c>0.0.0</c> when there is no entry assembly to ask.
    /// </summary>
    public static string Version { get; } = Read();

    private static string Read()
    {
        // The ENTRY assembly, not this one: the version that matters is the
        // host's, and Common.Web is a library every host references (§4.1).
        string? informational = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
            return "0.0.0";

        // Deterministic builds append "+<sha>" (SourceRevisionId, §4.4). It is
        // dropped rather than kept: service.version is grouped on, and a value
        // that changes every commit turns one series into thousands.
        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Platform.slnx`
Expected: PASS, 93 tests.

- [ ] **Step 5: Commit**

```bash
git add src/BuildingBlocks/Common.Web/BuildInfo.cs tests/Common.Web.Tests/BuildInfoTests.cs
git commit -F - <<'MSG'
feat(common): BuildInfo for the OpenTelemetry resource

§13.2 stamps service.version onto the resource and Appendix D lists
BuildInfo under D.5 — referenced but deliberately not shown — so the type
is specified by its use and defined here for the first time.

The entry assembly rather than this one: Common.Web is the library every
host references, and the version that identifies a running service is the
host's. The "+<sha>" a deterministic build appends is stripped, because
service.version is a dimension people group by and a value that changes
every commit turns one series into thousands.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
MSG
```

---

## Task 3: `SensitiveDataRedactor`

**Files:**
- Create: `src/BuildingBlocks/Common.Web/SensitiveDataRedactor.cs`
- Test: `tests/Common.Web.Tests/SensitiveDataRedactorTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public sealed class Common.Web.SensitiveDataRedactor :
  BaseProcessor<LogRecord>` with `public override void OnEnd(LogRecord)`.
  Constructed by `AddObservability` in Task 5.

- [ ] **Step 1: Write the failing test**

Create `tests/Common.Web.Tests/SensitiveDataRedactorTests.cs`. Note the two
different ways a record is produced, and why — this is not incidental:

```csharp
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

public class SensitiveDataRedactorTests
{
    // CA1848 is enforced repo-wide (ADR-019) and does not exempt test
    // projects, so the template goes through LoggerMessage.Define exactly as
    // production logging does. §13.4's point survives intact: the attribute
    // keys still come from a message template, read through ILogger.
    private static readonly Action<ILogger, string, string, Exception?> Login =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(1, nameof(Login)),
            "Login for {User} with {Password}");

    private static readonly Action<ILogger, string, Exception?> Plain =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(2, nameof(Plain)),
            "Customer {Customer} signed in");

    private static IReadOnlyList<KeyValuePair<string, object?>> Emit(Action<ILogger> write)
    {
        List<LogRecord> exported = [];

        // Built exactly as AddObservability builds it (§13.2) — ILoggingBuilder,
        // the same extension — so the test covers the seam the host uses. Not
        // the Logs Bridge API: that is behind an experimental diagnostic and is
        // not how any host here produces a record.
        using (ILoggerFactory factory = LoggerFactory.Create(b =>
            b.AddOpenTelemetry(o =>
            {
                o.AddProcessor(new SensitiveDataRedactor());
                o.AddInMemoryExporter(exported);
            })))
        {
            write(factory.CreateLogger("test"));
        }

        return exported.Single().Attributes!;
    }

    [Fact]
    public void Sensitive_attributes_are_redacted()
    {
        IReadOnlyList<KeyValuePair<string, object?>> attributes =
            Emit(logger => Login(logger, "ada", "hunter2", null));

        attributes.Single(a => a.Key == "Password").Value.ShouldBe("[redacted]");

        // The other half, and the one that catches a deny-list grown careless:
        // everything not on it survives intact.
        attributes.Single(a => a.Key == "User").Value.ShouldBe("ada");
    }

    [Fact]
    public void Matching_is_by_substring_and_ignores_case()
    {
        // ILogger.Log with an explicit state rather than a template, because
        // CA1727 requires PascalCase placeholders and `card_number` — the exact
        // key the deny list carries an entry for — cannot be written as one.
        // The state is what the processor actually sees, so this reaches the
        // same seam by the only route the analyser leaves open.
        KeyValuePair<string, object?>[] state =
        [
            new("NewPassword", "a"),
            new("card_number", "b"),
            new("Authorization", "c"),
            new("Customer", "ada")
        ];

        IReadOnlyList<KeyValuePair<string, object?>> attributes = Emit(logger =>
            logger.Log(LogLevel.Information, new EventId(3), state, null, (_, _) => "signed in"));

        attributes.Single(a => a.Key == "NewPassword").Value.ShouldBe("[redacted]");
        attributes.Single(a => a.Key == "card_number").Value.ShouldBe("[redacted]");
        attributes.Single(a => a.Key == "Authorization").Value.ShouldBe("[redacted]");
        attributes.Single(a => a.Key == "Customer").Value.ShouldBe("ada");
    }

    [Fact]
    public void A_record_with_no_attributes_at_all_is_left_alone()
    {
        // The guard clause. Reachable through ILogger with a null state, and
        // worth pinning because the loop below it dereferences Attributes
        // twice per record on every request.
        List<LogRecord> exported = [];

        using (ILoggerFactory factory = LoggerFactory.Create(b =>
            b.AddOpenTelemetry(o =>
            {
                o.AddProcessor(new SensitiveDataRedactor());
                o.AddInMemoryExporter(exported);
            })))
        {
            factory.CreateLogger("test").Log<object?>(
                LogLevel.Information, new EventId(4), null, null, (_, _) => "no state");
        }

        exported.Single().Attributes.ShouldBeNull();
    }

    // Pins the `scrubbed ??=` fast path, which exists because this runs on
    // every log record on every request. Nothing else would catch its removal
    // — the redaction tests above pass whether or not it copies.
    //
    // Measured either side of the redactor by two capturing processors, with
    // NO exporter in the pipeline. AddInMemoryExporter cannot be used for an
    // identity assertion: its export path calls LogRecord.Copy(), which
    // unconditionally reallocates the attribute list as the SDK's defence
    // against record pooling. Verified by decompiling
    // OpenTelemetry.Exporter.InMemory 1.17.0, after an earlier version of this
    // test failed against it for that reason and nothing to do with the code
    // under test.
    private (object? Before, object? After) AttributesEitherSideOf(Action<ILogger> write)
    {
        object? before = null;
        object? after = null;

        using (ILoggerFactory factory = LoggerFactory.Create(b =>
            b.AddOpenTelemetry(o =>
            {
                o.AddProcessor(new CapturingProcessor(r => before = r.Attributes));
                o.AddProcessor(new SensitiveDataRedactor());
                o.AddProcessor(new CapturingProcessor(r => after = r.Attributes));
            })))
        {
            write(factory.CreateLogger("test"));
        }

        return (before, after);
    }

    [Fact]
    public void A_record_with_nothing_sensitive_is_not_copied()
    {
        (object? before, object? after) =
            AttributesEitherSideOf(logger => Plain(logger, "ada", null));

        // Same instance, not merely an equal one: the processor returned
        // without allocating.
        after.ShouldBeSameAs(before);
    }

    [Fact]
    public void A_record_with_something_sensitive_is_copied()
    {
        // The control. Without it the test above passes against a redactor
        // that never copies anything — including one that never redacts.
        (object? before, object? after) =
            AttributesEitherSideOf(logger => Login(logger, "ada", "hunter2", null));

        after.ShouldNotBeSameAs(before);
    }

    private sealed class CapturingProcessor(Action<LogRecord> capture) : BaseProcessor<LogRecord>
    {
        public override void OnEnd(LogRecord record) => capture(record);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Platform.slnx`
Expected: FAIL to compile, `error CS0246: The type or namespace name
'SensitiveDataRedactor' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/BuildingBlocks/Common.Web/SensitiveDataRedactor.cs`, §13.4 verbatim:

```csharp
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Common.Web;

/// <summary>
/// §13.4's never-log rule, given a mechanism. Added to the OpenTelemetry
/// logging pipeline by <c>AddObservability</c> (§13.2), which is the point:
/// every host calls it, so the rule applies to all of them. In a service's own
/// project it would protect that service alone.
/// </summary>
/// <remarks>
/// Two limits worth stating rather than discovering. The processor sees
/// <em>attributes</em>, not the formatted message, so a value is redacted by
/// its key alone — which is the argument for naming a placeholder
/// <c>{Token}</c> and never interpolating. And it cannot help with a whole
/// object logged as one attribute; that is what the "never log full request
/// bodies" half of the rule is for.
/// </remarks>
public sealed class SensitiveDataRedactor : BaseProcessor<LogRecord>
{
    // Substring match, not equality: the field that leaks is never named
    // exactly "password" — it is "NewPassword", "card_number", "id_token".
    private static readonly string[] Sensitive =
        ["password", "secret", "token", "authorization", "cardnumber", "card_number", "ssn", "nationalid"];

    /// <inheritdoc />
    public override void OnEnd(LogRecord record)
    {
        if (record.Attributes is null)
            return;

        List<KeyValuePair<string, object?>>? scrubbed = null;

        for (int i = 0; i < record.Attributes.Count; i++)
        {
            KeyValuePair<string, object?> attribute = record.Attributes[i];
            if (!IsSensitive(attribute.Key))
                continue;

            // Copy only when something actually matches — the common case is
            // no match, and this runs on every log record on every request.
            scrubbed ??= [.. record.Attributes];
            scrubbed[i] = new(attribute.Key, "[redacted]");
        }

        if (scrubbed is not null)
            record.Attributes = scrubbed;
    }

    private static bool IsSensitive(string key) =>
        Sensitive.Any(s => key.Contains(s, StringComparison.OrdinalIgnoreCase));
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Platform.slnx`
Expected: PASS, 98 tests.

- [ ] **Step 5: Commit**

```bash
git add src/BuildingBlocks/Common.Web/SensitiveDataRedactor.cs \
        tests/Common.Web.Tests/SensitiveDataRedactorTests.cs
git commit -F - <<'MSG'
feat(common): redact sensitive attributes on the logging pipeline

§13.4's "never log a secret" is a rule of a shape that needs a mechanism,
or it is a request that every future developer remember it. The processor
sits on the pipeline §13.2 builds, so a property named Password is
redacted by default rather than by discipline.

Three tests, and the third is the one worth arguing. Redaction and
survival are the obvious pair; the fast path that avoids copying when
nothing matches is invisible to both, and it is the reason the loop is
written the way it is rather than as a Select. Asserting the exported
attribute list is the same instance is what pins it.

The substring case goes through ILogger.Log with an explicit state rather
than a template. CA1727 requires PascalCase placeholders, and card_number
— the exact key the deny list carries an entry for — cannot be spelled as
one. The state is what the processor sees, so the seam is the same.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
MSG
```

---

## Task 4: `MapCommonHealthEndpoints`

**Files:**
- Create: `src/BuildingBlocks/Common.Web/HealthCheckExtensions.cs`
- Test: `tests/Common.Web.Tests/HealthEndpointTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public static IEndpointRouteBuilder
  MapCommonHealthEndpoints(this IEndpointRouteBuilder app)` in
  `Common.Web.HealthCheckExtensions`. Maps `/health/live`, `/health/ready`
  and `/health/startup`.

- [ ] **Step 1: Write the failing test**

Create `tests/Common.Web.Tests/HealthEndpointTests.cs`:

```csharp
using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

public class HealthEndpointTests
{
    private sealed class Always(HealthStatus status) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct) =>
            Task.FromResult(new HealthCheckResult(status));
    }

    private static Task<IHost> StartAsync(Action<IHealthChecksBuilder> checks) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    checks(services.AddHealthChecks());
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapCommonHealthEndpoints());
                });
            })
            .ConfigureLogging(logging => logging.ClearProviders())
            .StartAsync(TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> GetAsync(IHost host, string path) =>
        host.GetTestClient().GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Liveness_passes_with_no_checks_registered()
    {
        using IHost host = await StartAsync(_ => { });

        HttpResponseMessage response = await GetAsync(host, "/health/live");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Liveness_ignores_a_failing_dependency_that_readiness_reports()
    {
        // §13.5's rule, and the one whose failure mode is a restart storm: if
        // liveness checked the database, a brief outage would restart every pod
        // simultaneously and the storm would outlast the outage.
        using IHost host = await StartAsync(checks =>
            checks.AddCheck("sql", new Always(HealthStatus.Unhealthy), tags: ["ready"]));

        HttpResponseMessage live = await GetAsync(host, "/health/live");
        HttpResponseMessage ready = await GetAsync(host, "/health/ready");

        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        ready.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Readiness_passes_when_its_checks_pass()
    {
        using IHost host = await StartAsync(checks =>
            checks.AddCheck("sql", new Always(HealthStatus.Healthy), tags: ["ready"]));

        HttpResponseMessage ready = await GetAsync(host, "/health/ready");

        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readiness_ignores_an_observe_tagged_check()
    {
        // The outbox is tagged observe, scraped and alerted on (§13.6), and
        // deliberately not part of any probe: gating readiness on a backlog
        // turns a delivery delay into a total outage.
        using IHost host = await StartAsync(checks =>
            checks.AddCheck("outbox", new Always(HealthStatus.Unhealthy), tags: ["observe"]));

        HttpResponseMessage ready = await GetAsync(host, "/health/ready");

        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Every_probe_allows_anonymous()
    {
        // Asserted on the metadata rather than by an unauthenticated request:
        // there is no authentication scheme to be anonymous against until
        // PR-16, and a request would pass for the wrong reason today and stop
        // meaning anything the moment one exists.
        using IHost host = await StartAsync(_ => { });

        IReadOnlyList<Endpoint> endpoints = host.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints;

        endpoints.Count.ShouldBe(3);
        foreach (Endpoint endpoint in endpoints)
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldNotBeNull(endpoint.DisplayName);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Platform.slnx`
Expected: FAIL to compile, `error CS1061: 'IEndpointRouteBuilder' does not
contain a definition for 'MapCommonHealthEndpoints'`.

- [ ] **Step 3: Write the implementation**

Create `src/BuildingBlocks/Common.Web/HealthCheckExtensions.cs`, §13.5 verbatim:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Common.Web;

/// <summary>
/// The three probes Kubernetes asks three distinct questions through (§13.5).
/// Mapped once here rather than per service, since the tag predicates are
/// identical everywhere and need no configuration — unlike the checks
/// themselves, which need connection strings and are registered by each
/// service's own Infrastructure.
/// </summary>
public static class HealthCheckExtensions
{
    /// <summary>
    /// Maps <c>/health/live</c>, <c>/health/ready</c> and
    /// <c>/health/startup</c>. Called by <c>Program.cs</c> after
    /// <c>builder.Build()</c> (§4.2).
    /// </summary>
    /// <remarks>
    /// A host that registers no readiness checks reports ready immediately.
    /// That is correct for the gateway and the BFF, which own no database, and
    /// for none of the six services — which is why the rule is that a host with
    /// a connection string has a readiness check and a host without one does
    /// not. An empty predicate set is a passing predicate set, so "forgot to
    /// wire it up" and "has no dependencies" look identical from outside.
    /// </remarks>
    public static IEndpointRouteBuilder MapCommonHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // AllowAnonymous is required, not cosmetic: the kubelet sends no token,
        // so an authenticated probe fails and the pod is restarted in a loop.
        app
            .MapHealthChecks("/health/live", new() { Predicate = _ => false })
            .AllowAnonymous();

        app
            .MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") })
            .AllowAnonymous();

        app
            .MapHealthChecks("/health/startup", new() { Predicate = c => c.Tags.Contains("ready") })
            .AllowAnonymous();

        return app;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Platform.slnx`
Expected: PASS, 103 tests.

- [ ] **Step 5: Commit**

```bash
git add src/BuildingBlocks/Common.Web/HealthCheckExtensions.cs \
        tests/Common.Web.Tests/HealthEndpointTests.cs
git commit -F - <<'MSG'
feat(common): map the three health probes

§13.5's endpoints, mapped once in Common.Web because the tag predicates
are identical for every service and need no configuration. The checks are
the other half and stay with each service's Infrastructure, which has the
connection strings.

Two of the five tests are the ones that matter, and both assert a
non-obvious negative. Liveness stays 200 while a ready-tagged check is
failing — if it did not, a brief database outage would restart every pod
at once and the storm would outlast the outage. Readiness ignores an
observe-tagged check, so a growing outbox backlog cannot pull every pod
out of the load balancer and convert a delivery delay into an outage.

Anonymity is asserted on endpoint metadata rather than by an
unauthenticated request. There is no scheme to be anonymous against until
PR-16, so a request would pass today for a reason that stops holding the
moment one exists.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
MSG
```

---

## Task 5: `AddObservability`

**Files:**
- Create: `src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs`
- Create: `tests/Common.Web.Tests/TelemetryHost.cs`
- Test: `tests/Common.Web.Tests/ObservabilityTests.cs`

**Interfaces:**
- Consumes: `BuildInfo.Version` (Task 2), `SensitiveDataRedactor` (Task 3).
- Produces: `public static IHostApplicationBuilder AddObservability(this
  IHostApplicationBuilder builder)` in `Common.Web.ObservabilityExtensions`.
  Consumed by `AddCommonWebDefaults` in Task 6.
  `internal static class Common.Web.Tests.TelemetryHost` with
  `internal static HostApplicationBuilder Builder()`.

- [ ] **Step 1: Write the test host helper**

Create `tests/Common.Web.Tests/TelemetryHost.cs`. This is infrastructure for
the test below, not a test:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Common.Web.Tests;

/// <summary>
/// A host builder shaped like a service host, for asserting what
/// <c>AddObservability</c> registers.
/// </summary>
internal static class TelemetryHost
{
    internal const string ServiceName = "Probe.Service";
    internal const string EnvironmentName = "Testing";

    internal static HostApplicationBuilder Builder()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                ApplicationName = ServiceName,
                EnvironmentName = EnvironmentName
            });

        // AddObservability calls UseOtlpExporter, which blocks for its full
        // ten-second timeout trying to reach a collector no test runs. 200 ms
        // takes a host from 8.3 s to 0.44 s — both measured. The exporter stays
        // wired and simply gives up quickly, which is the point: omitting it
        // would stop testing the line the PR exists to add.
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["OTEL_EXPORTER_OTLP_TIMEOUT"] = "200" });

        // CreateEmptyApplicationBuilder registers nothing. IMeterFactory is
        // what the meters below are created through.
        builder.Services.AddMetrics();

        return builder;
    }
}
```

- [ ] **Step 2: Write the failing test**

Create `tests/Common.Web.Tests/ObservabilityTests.cs`:

```csharp
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

public class ObservabilityTests
{
    // The test holds its own copy of §13.2's list on purpose. Sharing a
    // constant with the registration would make this vacuous: deleting a name
    // from one place would delete it from the assertion too, and the whole
    // point is that an unregistered meter is collected by nothing and alerted
    // on in vain.
    private static readonly string[] Required =
    [
        "Ordering.Orders",
        "Ordering.Outbox",
        "Commerce.Requests",
        "Commerce.Messaging",
        "MassTransit",
        "Microsoft.Extensions.Caching.Hybrid",
        "StackExchange.Redis"
    ];

    [Fact]
    public void Every_meter_an_alert_reads_from_is_collected()
    {
        List<Metric> exported = [];

        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.AddObservability();
        builder.Services
            .AddOpenTelemetry()
            .WithMetrics(m => m.AddInMemoryExporter(exported));

        using IHost host = builder.Build();

        // Resolve the provider BEFORE creating any instrument. An instrument
        // created first has no listener, is never subscribed to, and the test
        // silently measures nothing while appearing to pass its setup.
        MeterProvider provider = host.Services.GetRequiredService<MeterProvider>();
        IMeterFactory factory = host.Services.GetRequiredService<IMeterFactory>();

        foreach (string name in Required)
            factory.Create(name).CreateCounter<long>("probe.counter").Add(1);

        provider.ForceFlush();

        // Asserted as a subset, not exact equality. AddRuntimeInstrumentation
        // and the OTLP exporter's own (failing, localhost) export attempt add
        // "System.Runtime", "System.Net.Http" and "System.Net.NameResolution"
        // to this list too — real .NET diagnostics meters that ARE genuinely
        // wanted in production. An exact match would only pass by dropping
        // telemetry the same PR turns on. The guard survives: a MeterProvider
        // only subscribes to names AddMeter registered, so deleting any one of
        // the seven still fails this.
        Required.ShouldBeSubsetOf(exported.Select(m => m.MeterName).Distinct());
    }

    [Fact]
    public void The_resource_names_the_service_its_version_and_its_environment()
    {
        ResourceCapturingExporter exporter = new();

        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.AddObservability();
        builder.Services
            .AddOpenTelemetry()
            .WithMetrics(m => m.AddReader(new BaseExportingMetricReader(exporter)));

        using IHost host = builder.Build();

        MeterProvider provider = host.Services.GetRequiredService<MeterProvider>();
        host.Services
            .GetRequiredService<IMeterFactory>()
            .Create("Commerce.Requests")
            .CreateCounter<long>("probe.counter")
            .Add(1);
        provider.ForceFlush();

        Dictionary<string, object> attributes = exporter.Captured
            .ShouldNotBeNull()
            .Attributes
            .ToDictionary(a => a.Key, a => a.Value);

        attributes["service.name"].ShouldBe(TelemetryHost.ServiceName);
        attributes["service.version"].ShouldBe(BuildInfo.Version);
        attributes["deployment.environment"].ShouldBe(TelemetryHost.EnvironmentName);
    }

    [Fact]
    public void Health_probes_are_filtered_out_of_traces()
    {
        // Asserted on the registered predicate, not end to end. TestServer
        // produces no ASP.NET Core server spans at all, so an end-to-end
        // version of this test would pass while filtering nothing — verified
        // by probe before it was written this way.
        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.AddObservability();

        using IHost host = builder.Build();

        AspNetCoreTraceInstrumentationOptions options = host.Services
            .GetRequiredService<IOptionsMonitor<AspNetCoreTraceInstrumentationOptions>>()
            .Get(Options.DefaultName);

        options.Filter.ShouldNotBeNull();

        // At a ten-second probe interval across a dozen pods these would
        // otherwise dominate both trace volume and storage cost.
        options.Filter(Request("/health/live")).ShouldBeFalse();
        options.Filter(Request("/health/ready")).ShouldBeFalse();
        options.Filter(Request("/health/startup")).ShouldBeFalse();
        options.Filter(Request("/orders")).ShouldBeTrue();
    }

    private static DefaultHttpContext Request(string path)
    {
        DefaultHttpContext context = new();
        context.Request.Path = path;
        return context;
    }

    // ParentProvider.GetResource() is the only public route to the resource a
    // provider was configured with; no exported metric carries it.
    private sealed class ResourceCapturingExporter : BaseExporter<Metric>
    {
        public Resource? Captured { get; private set; }

        public override ExportResult Export(in Batch<Metric> batch)
        {
            Captured = ParentProvider?.GetResource();
            return ExportResult.Success;
        }
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test Platform.slnx`
Expected: FAIL to compile, `error CS1061: 'HostApplicationBuilder' does not
contain a definition for 'AddObservability'`.

- [ ] **Step 4: Write the implementation**

Create `src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs`. This is
§13.2 with the two deferred instrumentation calls replaced by the comment that
says which PR brings each:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Common.Web;

/// <summary>
/// The three signals of §13.1, configured once here and referenced by every
/// service host (§13.2). Composed by <c>AddCommonWebDefaults</c> rather than
/// called directly by a host.
/// </summary>
public static class ObservabilityExtensions
{
    /// <summary>
    /// Registers the logging pipeline, metrics, tracing and OTLP export.
    /// </summary>
    public static IHostApplicationBuilder AddObservability(this IHostApplicationBuilder builder)
    {
        string serviceName = builder.Environment.ApplicationName;

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;

            // §13.4's "never log a secret" rule, given a mechanism. Registered
            // here because this is the only logging pipeline the host has — a
            // redaction policy configured on a library nobody installed
            // redacts nothing, and reads in review as though it does.
            logging.AddProcessor(new SensitiveDataRedactor());
        });

        builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(serviceName, serviceVersion: BuildInfo.Version)
                .AddAttributes([new("deployment.environment", builder.Environment.EnvironmentName)]))
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                // Every meter an alert or SLO reads from. A condition whose
                // signal is not registered here cannot fire — it looks
                // configured and is silent, which is worse than having no
                // alert at all. Registered ahead of the instruments: a name is
                // a string and costs nothing, and the alternative is spreading
                // one block's edits across six later pull requests.
                .AddMeter("Ordering.Orders")                       // §13.3, §13.6
                .AddMeter("Ordering.Outbox")                       // §13.6 per-lane
                // Shared names, not service-prefixed: every service emits the
                // same instruments and the service.name resource attribute
                // separates them. One dashboard query then works for all of
                // them, and a new service appears on it without anyone editing
                // a panel.
                .AddMeter("Commerce.Requests")                     // §13.3, §13.7
                .AddMeter("Commerce.Messaging")                    // §13.3, §13.7
                .AddMeter("MassTransit")
                .AddMeter("Microsoft.Extensions.Caching.Hybrid")   // cache hit ratio
                .AddMeter("StackExchange.Redis"))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(o =>
                    o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                // AddEntityFrameworkCoreInstrumentation and
                // AddRedisInstrumentation land with the packages they
                // instrument, at PR-08 and PR-12. Unlike a meter name, each
                // costs a package reference — a claim about the dependency
                // graph that nothing here would yet make true.
                .AddSource("MassTransit"))
            .UseOtlpExporter();

        return builder;
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Platform.slnx`
Expected: PASS, 106 tests. The three new tests should complete in well under a
second each — if any takes eight seconds, `TelemetryHost`'s OTLP timeout is not
reaching the exporter and the run will get slower with every host added later.

- [ ] **Step 6: Commit**

```bash
git add src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs \
        tests/Common.Web.Tests/TelemetryHost.cs \
        tests/Common.Web.Tests/ObservabilityTests.cs
git commit -F - <<'MSG'
feat(common): OpenTelemetry defaults for every host

§13.2's AddObservability: the logging pipeline with §13.4's redactor on
it, metrics, tracing with health probes filtered out, and OTLP export.

All seven meters register now even though only Commerce.Requests has an
instrument today. A meter name is a string and costs nothing, and §13.2
argues the case: a condition whose signal is not registered cannot fire,
and looks configured while being silent. The two instrumentation calls
that are not here — EF Core and Redis — are the ones that would cost a
package reference for a library nothing in the repository uses, and they
arrive at PR-08 and PR-12 with what they instrument.

The meter test keeps its own copy of the list rather than sharing a
constant with the registration. Sharing one would make it vacuous:
deleting a name would delete the assertion with it.

The trace filter is asserted on the registered predicate rather than end
to end, because TestServer produces no ASP.NET Core server spans at all —
an end-to-end test would have passed while filtering nothing. Checked
against the runtime rather than assumed, as was the OTLP timeout the test
host pins: UseOtlpExporter blocks for ten seconds per host on a collector
no test runs, and 200 ms takes that to under half a second.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
MSG
```

---

## Task 6: `AddCommonWebDefaults`

**Files:**
- Create: `src/BuildingBlocks/Common.Web/CommonWebDefaultsExtensions.cs`
- Test: `tests/Common.Web.Tests/CommonWebDefaultsTests.cs`

**Interfaces:**
- Consumes: `AddObservability` (Task 5), `AddCommonProblemDetails` (PR-03,
  already present in `ProblemDetailsExtensions`), `TelemetryHost.Builder()`
  (Task 5).
- Produces: `public static IHostApplicationBuilder AddCommonWebDefaults(this
  IHostApplicationBuilder builder)` in
  `Common.Web.CommonWebDefaultsExtensions` — the single call every
  `Program.cs` makes.

- [ ] **Step 1: Write the failing test**

Create `tests/Common.Web.Tests/CommonWebDefaultsTests.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

public class CommonWebDefaultsTests
{
    [Fact]
    public void The_one_call_a_host_makes_registers_all_three_pieces()
    {
        HostApplicationBuilder builder = TelemetryHost.Builder();

        builder.AddCommonWebDefaults();

        using IHost host = builder.Build();

        // Observability (§13.2).
        host.Services.GetService<MeterProvider>().ShouldNotBeNull();
        host.Services.GetService<TracerProvider>().ShouldNotBeNull();

        // The RFC 9457 customisation PR-03 shipped (§10.5). Registered through
        // AddProblemDetails, whose observable effect here is the configured
        // options rather than a service of its own.
        host.Services
            .GetRequiredService<IOptions<ProblemDetailsOptions>>()
            .Value
            .CustomizeProblemDetails
            .ShouldNotBeNull();

        // Liveness only (§13.5). Common.Web has no connection strings, so it
        // registers no readiness check — those come from each service's own
        // Infrastructure.
        host.Services.GetService<HealthCheckService>().ShouldNotBeNull();
    }

    [Fact]
    public void No_authentication_is_registered_yet()
    {
        // PR-16 adds AddJwtBearer and the "authenticated" policy. Pinning the
        // absence keeps the gap deliberate: registering the policy without a
        // scheme would reject every request that reached it, and the failure
        // would surface in whichever service first mapped an endpoint to it
        // rather than here.
        HostApplicationBuilder builder = TelemetryHost.Builder();

        builder.AddCommonWebDefaults();

        builder.Services
            .Any(d => d.ServiceType.FullName?.Contains("Authentication", StringComparison.Ordinal) == true)
            .ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Platform.slnx`
Expected: FAIL to compile, `error CS1061: 'HostApplicationBuilder' does not
contain a definition for 'AddCommonWebDefaults'`.

- [ ] **Step 3: Write the implementation**

Create `src/BuildingBlocks/Common.Web/CommonWebDefaultsExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Common.Web;

/// <summary>
/// The single call every <c>Program.cs</c> makes (§4.2, §13.2). It covers what
/// every host needs identically, and nothing else: anything needing a
/// connection string — the SQL, Redis, broker and outbox checks of §13.5 —
/// belongs in a service's own <c>Add*Infrastructure</c>, because
/// <c>Common.Web</c> cannot know them.
/// </summary>
public static class CommonWebDefaultsExtensions
{
    /// <summary>
    /// Composes observability, the shared problem-details customisation and
    /// liveness health checks.
    /// </summary>
    public static IHostApplicationBuilder AddCommonWebDefaults(this IHostApplicationBuilder builder)
    {
        builder.AddObservability();                            // §13.2

        // PR-16 adds AddAuthentication/AddJwtBearer (§11.3) and the one policy
        // every host shares, "authenticated" (§13.2). The two arrive together
        // because neither works alone: a policy requiring an authenticated
        // user, with no scheme registered to authenticate one, rejects every
        // request that reaches it.

        builder.Services.AddCommonProblemDetails();            // §10.5

        // Liveness only — it must not touch dependencies (§13.5), and
        // Common.Web has no connection strings anyway. Readiness checks are
        // registered by each service's own Infrastructure, which does.
        builder.Services.AddHealthChecks();

        return builder;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Platform.slnx`
Expected: PASS, 108 tests.

- [ ] **Step 5: Commit**

```bash
git add src/BuildingBlocks/Common.Web/CommonWebDefaultsExtensions.cs \
        tests/Common.Web.Tests/CommonWebDefaultsTests.cs
git commit -F - <<'MSG'
feat(common): compose the defaults every host calls

AddCommonWebDefaults is the one call Program.cs makes (§4.2). It lands
with three of §13.2's five pieces: observability, the problem-details
customisation PR-03 shipped, and liveness health checks.

The authentication block is the gap, and it is deliberate rather than
forgotten. Appendix C gives JWT validation in Common.Web to PR-16, and
the "authenticated" policy cannot go in without it — a policy requiring
an authenticated user, with no scheme registered to authenticate one,
rejects every request that reaches it. The second test pins the absence
so the gap stays a decision.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
MSG
```

---

## Task 7: Reconcile the blueprint

The code now contradicts three documents. CLAUDE.md's one rule says a code
change that contradicts a chapter is not done until the chapter is amended in
the same PR.

**Files:**
- Modify: `docs/backend-architecture/13-observability.md`
- Modify: `docs/backend-architecture/appendix-d-type-inventory.md`
- Modify: `CLAUDE.md`

**Interfaces:**
- Consumes: everything above.
- Produces: no code.

- [ ] **Step 1: Add the deferral note to §13.2**

In `docs/backend-architecture/13-observability.md`, immediately after the
`AddObservability` code block (which ends with the closing fence following
`return builder;` / `}`), and **before** the paragraph beginning "Filtering
health checks out of traces", insert:

```markdown
Three of the lines above arrive later than the rest, and each is named here so
that a reader comparing this block against `Common.Web` does not read a gap as
a mistake. `AddEntityFrameworkCoreInstrumentation` and `AddRedisInstrumentation`
land with the packages they instrument, at **PR-08** and **PR-12** — unlike a
meter name, which is a string, each costs a package reference, and a reference
to a library nothing uses is a claim about the dependency graph that is not yet
true. The authentication block in `AddCommonWebDefaults` lands at **PR-16**,
with the scheme that makes its policy mean anything.
```

- [ ] **Step 2: Rewrite §13.4's test placement**

In the same file, find this paragraph:

```markdown
Assert it, because a redactor that silently stops matching is worse than none.
The test lives in `Ordering.Api.Tests` — a `Common.Web` behaviour tested once
rather than once per host, in the suite that already owns host-level concerns
([§12.1](12-test-strategy.md)). Every host calls `AddObservability`, so a second copy in Catalog's
suite would re-assert the same processor over the same pipeline and only add a
place to forget.
```

Replace it with:

```markdown
Assert it, because a redactor that silently stops matching is worse than none.
The test lives in `Common.Web.Tests` — a `Common.Web` behaviour tested once
rather than once per host, in the suite that already owns this project's
behaviour ([§12.1](12-test-strategy.md)). Every host calls `AddObservability`, so a copy in a
service's own suite would re-assert the same processor over the same pipeline
and only add a place to forget — and a building block asserted in Ordering's
suite is one that moves house if Ordering ever does.
```

- [ ] **Step 3: Check no other site names the old location**

```bash
grep -rn "Ordering.Api.Tests" docs/ CLAUDE.md
```

Every remaining hit must be about something other than the redactor. If one is
about the redactor, reconcile it here rather than leaving it.

- [ ] **Step 4: Add `AddObservability` to Appendix D**

In `docs/backend-architecture/appendix-d-type-inventory.md`, find the row:

```markdown
| `UseCorrelationId`, `MapCommonHealthEndpoints`, `AddCommonWebDefaults` | §10.4, §13.5, §13.2 | `Common.Web` host extensions |
```

Replace it with:

```markdown
| `UseCorrelationId`, `MapCommonHealthEndpoints`, `AddObservability`, `AddCommonWebDefaults` | §10.4, §13.5, §13.2 | `Common.Web` host extensions |
```

- [ ] **Step 5: Update CLAUDE.md**

Four edits, all in `CLAUDE.md`.

First, the tree entry for `Common.Web` — replace:

```
  Common.Web/                    UseCorrelationId, AddCommonProblemDetails,
                                 ToHttpResult — the only project referencing
                                 another, and the only one with a
                                 FrameworkReference
```

with:

```
  Common.Web/                    UseCorrelationId, AddCommonProblemDetails,
                                 ToHttpResult, AddObservability,
                                 MapCommonHealthEndpoints, SensitiveDataRedactor,
                                 BuildInfo and the AddCommonWebDefaults that
                                 composes them — the only project referencing
                                 another, and the only one with a
                                 FrameworkReference
```

Second, in *Which phase are you in*, replace the paragraph beginning
"`Platform.slnx` holds six projects and `dotnet test` runs 88 tests" with:

```markdown
`Platform.slnx` holds six projects and `dotnet test` runs 108 tests, so the
build rules and the drift rules below are live and a green run now means
something. **PR-06 is next** (`feat(dev): Docker Compose — SQL Server, Redis,
RabbitMQ, Keycloak, OTel`), which depends only on PR-01 and gives the OTLP
export PR-05 just wired somewhere to send to.
```

Third, replace the sentence:

```markdown
`Common.Web` now does exist, and the same rule applies
inside it: it holds §10.4 and §10.5 and nothing else until PR-05 adds
observability and PR-16 adds JWT validation.
```

with:

```markdown
`Common.Web` now does exist, and the same rule applies
inside it: it holds §10.4, §10.5, §13.2, §13.4 and §13.5, and nothing else
until PR-16 adds JWT validation — which is also the one gap inside
`AddCommonWebDefaults`, three of §13.2's five pieces today.
```

Fourth, in the Telemetry discussion of `Directory.Packages.props` — if any
sentence in CLAUDE.md states an OpenTelemetry version, update it to 1.17.0.
Check with:

```bash
grep -n "1\.13\|OpenTelemetry" CLAUDE.md
```

If there are no hits, this sub-step is complete with no edit.

- [ ] **Step 6: Verify the whole thing**

```bash
dotnet build Platform.slnx
dotnet test Platform.slnx
cd .github/licence-gate && python -m unittest && python licence_gate.py
```

Expected: build clean with 0 warnings; 108 tests pass; the gate exits 0.

Then run `/validate-blueprint` and `/check-links`, and fix anything either
reports before committing.

- [ ] **Step 7: Commit**

```bash
git add docs/backend-architecture/13-observability.md \
        docs/backend-architecture/appendix-d-type-inventory.md CLAUDE.md
git commit -F - <<'MSG'
docs: reconcile §13.2, §13.4 and Appendix D with the code

The blueprint and the solution are one artefact with two representations,
and PR-05 moved one of them.

§13.2 gains a paragraph naming the three lines that arrive later and the
PR that brings each, so a reader diffing the block against Common.Web
reads a sequencing decision rather than an omission.

§13.4 is corrected rather than extended. It placed the redactor test in
Ordering.Api.Tests, which will not exist until PR-18, and argued for it on
grounds — tested once rather than once per host — that Common.Web.Tests
satisfies better, having existed since PR-03. The paragraph was written
before that suite was in the plan.

Appendix D's Common.Web row listed three host extensions and §13.2 defines
a fourth; D.4's own rule requires it to appear.

CLAUDE.md's test count read 88 against an actual 91 before this PR even
started — PR-04's last two commits landed after that sentence was written.
It now reads 108.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
MSG
```

---

## Done means

- `dotnet build Platform.slnx` clean, **with no new entry in
  `Directory.Build.props`**. A fourth suppression is a decision about the
  policy; if one seems necessary, stop and raise it.
- `dotnet test Platform.slnx` green at **108** tests, and the observability
  tests each complete in well under a second.
- The licence gate passes.
- `/validate-blueprint` and `/check-links` clean.
- `git log` shows seven commits, each independently reviewable, with the
  package bump separate from the feature.
