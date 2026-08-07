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
