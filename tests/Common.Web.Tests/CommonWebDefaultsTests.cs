using Common.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
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
    public async Task Authentication_and_the_shared_policy_arrive_together()
    {
        // Neither works alone, which is why one test covers both: a policy
        // requiring an authenticated user, with no scheme registered to
        // authenticate one, rejects every request that reaches it — and the
        // failure surfaces in whichever service first maps an endpoint to it
        // rather than here.
        HostApplicationBuilder builder = TelemetryHost.Builder();

        builder.AddCommonWebDefaults();

        using IHost host = builder.Build();

        AuthenticationScheme? bearer = await host.Services
            .GetRequiredService<IAuthenticationSchemeProvider>()
            .GetSchemeAsync(JwtBearerDefaults.AuthenticationScheme);

        bearer.ShouldNotBeNull("§11.2 — every service re-validates the token itself");

        (await host.Services
            .GetRequiredService<IAuthorizationPolicyProvider>()
            .GetPolicyAsync("authenticated"))
            .ShouldNotBeNull("the one policy every host shares (§13.2), and the gateway's route file names it");
    }

    [Fact]
    public void The_current_user_port_resolves_per_request()
    {
        // §11.4's port and the accessor it depends on. ASP.NET Core registers
        // no IHttpContextAccessor by default, so the pairing is the assertion:
        // ICurrentUser alone would resolve here and fail ValidateOnBuild in
        // every real host, which is the wrong place to find out.
        HostApplicationBuilder builder = TelemetryHost.Builder();

        builder.AddCommonWebDefaults();

        using IHost host = builder.Build();
        using IServiceScope scope = host.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<ICurrentUser>().ShouldBeOfType<HttpContextCurrentUser>();

        // Scoped, not singleton: a caller is per request, and a captured one
        // would answer the previous request's subject to the next.
        ServiceDescriptor descriptor = builder.Services.Single(d => d.ServiceType == typeof(ICurrentUser));
        descriptor.Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    [Fact]
    public void A_host_that_cannot_name_its_identity_provider_does_not_start()
    {
        // The eager read of §11.3, and the posture AddSqlServer and
        // AddMassTransitMessaging already take. Not ValidateOnStart: §15.4
        // keeps ServiceIdentityOptions as the solution's only options type,
        // and §12.4's fixture comment naming OptionsValidationException here
        // was amended in the same change that added this test.
        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.Configuration[AuthenticationExtensions.AuthorityKey] = null;

        InvalidOperationException thrown =
            Should.Throw<InvalidOperationException>(builder.AddCommonWebDefaults);

        // Naming the key is the whole value over an options exception: the
        // message is read by somebody looking at a crash loop in a cluster,
        // and "Identity:Authority" is the search term that ends it.
        thrown.Message.ShouldContain(AuthenticationExtensions.AuthorityKey);
    }
}
