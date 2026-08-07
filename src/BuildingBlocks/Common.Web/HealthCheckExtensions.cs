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
