using Common.Application;
using Microsoft.AspNetCore.Authorization;
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
    /// Composes observability, token validation, the shared problem-details
    /// customisation and liveness health checks.
    /// </summary>
    public static IHostApplicationBuilder AddCommonWebDefaults(this IHostApplicationBuilder builder)
    {
        builder.AddObservability();                            // §13.2
        builder.AddJwtAuthentication();                        // §11.3

        // The scheme and the policy arrive together because neither works
        // alone: a policy requiring an authenticated user, with no scheme
        // registered to authenticate one, rejects every request that reaches
        // it. This is the one policy every host shares and the only one
        // Common.Web may know — "is there a valid token". Permission policies
        // are per-service and are registered by the service (§11.4) or, for
        // the gateway, by the gateway.
        //
        // Deliberately identical to ASP.NET Core's default policy, which YARP
        // would accept as the magic string "default" (§10.2). Naming it costs
        // one line and buys a route file that says what it means — and that
        // file is read by people deciding whether a path is public.
        //
        // SetFallbackPolicy is what makes authorization deny-by-default. Without
        // it UseAuthorization evaluates NOTHING on an endpoint carrying no
        // policy metadata, so a new *Endpoints class that omits the one
        // RequireAuthorization line is reachable with no diagnostic — no
        // compiler error, no ValidateOnBuild failure, no failing test. The
        // fallback inverts that: the omission is a 401, and a public route has
        // to say AllowAnonymous, which is a line a reviewer can see.
        //
        // It reaches the gateway's proxied routes too, which is why
        // appsettings.json now names "anonymous" on catalog-public rather than
        // leaving the key out — a public path by omission and a public path by
        // decision read identically in a route file, and only one of them
        // survives someone else's edit.
        builder.Services
            .AddAuthorizationBuilder()
            .AddPolicy("authenticated", p => p.RequireAuthenticatedUser())
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        // §11.4's port, paired with the accessor it depends on: ASP.NET Core
        // registers no IHttpContextAccessor by default, so omitting the first
        // line fails ValidateOnBuild rather than the first ownership check.
        // Scoped, because a caller is per request.
        //
        // Here rather than in each service's Add*Infrastructure, which is where
        // §4.2 put it: every host that authenticates has a current user, and
        // nothing in either type names a service. §11.4 was amended to match.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

        builder.Services.AddCommonProblemDetails();            // §10.5

        // Liveness only — it must not touch dependencies (§13.5), and
        // Common.Web has no connection strings anyway. Readiness checks are
        // registered by each service's own Infrastructure, which does.
        builder.Services.AddHealthChecks();

        return builder;
    }
}
