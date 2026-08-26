using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

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
    // The tag §13.5 names, spelled once: the two predicates below and the
    // startup guard have to be asking about the same set, and a tag that
    // matched in one place and not the other would fail open in exactly the
    // direction this guard exists to close.
    private const string Ready = "ready";

    /// <summary>
    /// Maps <c>/health/live</c>, <c>/health/ready</c> and
    /// <c>/health/startup</c>. Called by <c>Program.cs</c> after
    /// <c>builder.Build()</c> (§4.2).
    /// </summary>
    /// <remarks>
    /// <b>An empty predicate set is a passing predicate set</b>, so a host that
    /// registers no readiness checks answers <c>/health/ready</c> with 200
    /// while it can reach nothing — and §15.1 removes the smoke stage by name
    /// on the grounds that this probe already gates the rollout. "Forgot to
    /// wire it up" and "has no dependencies" therefore look identical from
    /// outside, and only one of them is a deploy that should proceed.
    /// <para>
    /// <paramref name="ownsNoReadinessDependencies"/> is what separates them. The rule
    /// §13.5 states — a host with a connection string has a readiness check,
    /// and a host without one does not — is mechanised here rather than left as
    /// prose: the default refuses to start a host whose readiness set is empty,
    /// and the gateway and the BFF, which own no database, say so at the call
    /// site. A service that loses its checks in a refactor then fails at
    /// startup instead of reporting ready, which is the direction §13.5's own
    /// restart-storm argument already accepts.
    /// </para>
    /// <para>
    /// This is deliberately a claim made at the call site rather than a count
    /// asserted per service. <c>HostSmokeTests</c> pins Catalog's four
    /// registrations and Ordering's, and nothing generates an equivalent
    /// assertion for a scaffolded service (§4.5) — so the guard that travels
    /// with the building block is the one every future service inherits.
    /// </para>
    /// </remarks>
    /// <param name="app">The route builder to map the three probes on.</param>
    /// <param name="ownsNoReadinessDependencies">
    /// <see langword="true"/> for a host with nothing to be ready for. Passing
    /// it is a written decision; the default is a startup failure.
    /// <para>
    /// <b>Named for the readiness set rather than for dependencies at large,
    /// because the BFF has one and still passes this.</b> §9.7's synchronous
    /// hop to Catalog is a dependency by any ordinary reading, and it is
    /// deliberately not a readiness one — a BFF that reported unready when
    /// Catalog is down would take itself out of rotation for a fault it is
    /// meant to degrade around. The first spelling was
    /// <c>ownsNoDependencies</c>, which made a false claim at exactly that
    /// call site, in a parameter whose whole purpose is to be a written
    /// decision.
    /// </para>
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The host registered no <c>ready</c>-tagged health check and did not
    /// declare that it has none.
    /// </exception>
    public static IEndpointRouteBuilder MapCommonHealthEndpoints(
        this IEndpointRouteBuilder app,
        bool ownsNoReadinessDependencies = false)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!ownsNoReadinessDependencies && !AnyReadinessCheck(app))
        {
            throw new InvalidOperationException(
                "No health check carries the \"ready\" tag, so /health/ready would answer 200 " +
                "while this host can reach nothing (§13.5). Register the service's readiness " +
                "checks in its own Infrastructure, or pass ownsNoReadinessDependencies: true if this host " +
                "genuinely owns none.");
        }

        // AllowAnonymous is required, not cosmetic: the kubelet sends no token,
        // so an authenticated probe fails and the pod is restarted in a loop.
        app
            .MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
            .AllowAnonymous();

        app
            .MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(Ready) })
            .AllowAnonymous();

        app
            .MapHealthChecks("/health/startup", new HealthCheckOptions { Predicate = c => c.Tags.Contains(Ready) })
            .AllowAnonymous();

        return app;
    }

    // Read from the options rather than from HealthCheckService: the service
    // exposes no registration list, and the options are what the predicates
    // above are evaluated against — so this asks the same question the probe
    // will ask, rather than one that merely correlates with it.
    private static bool AnyReadinessCheck(IEndpointRouteBuilder app)
    {
        HealthCheckServiceOptions options = app.ServiceProvider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value;

        return options.Registrations.Any(r => r.Tags.Contains(Ready));
    }
}
