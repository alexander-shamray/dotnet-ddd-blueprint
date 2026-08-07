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
