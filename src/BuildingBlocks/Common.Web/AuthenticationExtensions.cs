using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Common.Web;

/// <summary>
/// §11.3's JWT bearer registration, composed by <c>AddCommonWebDefaults</c>
/// rather than called directly by a host. Every service validates the token
/// itself: the gateway doing it is not sufficient, because anything reaching a
/// service by another path — a misconfigured network policy, a port-forward,
/// another service — would otherwise be unauthenticated (§11.2). Validation is
/// cheap; assume the network is hostile.
/// </summary>
public static class AuthenticationExtensions
{
    /// <summary>
    /// The audience every host in the platform validates. A constant rather
    /// than configuration: §11.5 settles on one audience for the whole platform
    /// — per-service audiences are a later split — and §15.4's rule is that an
    /// options type needs a member that differs between environments. This one
    /// is the same string in Compose, in the fixture and in production.
    /// </summary>
    public const string Audience = "commerce-api";

    /// <summary>The configuration key the authority is read from (§14.1, §15.4).</summary>
    public const string AuthorityKey = "Identity:Authority";

    public static IHostApplicationBuilder AddJwtAuthentication(this IHostApplicationBuilder builder)
    {
        // Read eagerly and throw naming the key — the posture AddSqlServer and
        // AddMassTransitMessaging already take, so a host that cannot name its
        // identity provider does not start. Deliberately NOT an options type
        // with ValidateOnStart: §15.4 makes ServiceIdentityOptions the only
        // one in the solution and argues why, and a second bag bound to a
        // section holding one value is the shape that rule forbids. §12.4's
        // fixture comment named OptionsValidationException here and was
        // amended in this change — the failure is this line, and it says which
        // key is missing, which the options exception could not.
        //
        // Blank counts as missing, and the distinction is not academic: an
        // environment variable set to the empty string reaches Configuration as
        // "" rather than null, so a null-only guard admits Identity__Authority=
        // — the commonest way a deployment gets this wrong — and hands
        // JwtBearer an authority it cannot build a metadata address from. The
        // host would then start, having promised it would not, and fail the
        // first token-bearing request instead of the deployment.
        string? configured = builder.Configuration[AuthorityKey];

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"'{AuthorityKey}' is not configured. Every host re-validates inbound tokens (§11.2), " +
                "so one that cannot name its identity provider must refuse to start rather than " +
                "answer the first request without a principal.");
        }

        // Blank was only the commonest malformed value, not the only one.
        // `keycloak:8080/realms/commerce` — a scheme somebody dropped — is
        // non-blank and still not an address: JwtBearer builds its metadata
        // URL from it and fails when the handler is first resolved, which is
        // during traffic. The guard is worth exactly as much as the set of
        // wrong values it catches, so it takes the whole shape.
        if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri? parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"'{AuthorityKey}' is '{configured}', which is not an absolute http or https URL. " +
                "It is the base address a discovery document is fetched from (§11.3), so a value " +
                "that cannot be one fails the deployment rather than the first request.");
        }

        // And https everywhere but Development, which is the same rule
        // RequireHttpsMetadata applies below — moved to startup, where it is a
        // deployment error rather than a 500 on the first token. The two read
        // the same environment on purpose: a host that would refuse to fetch
        // metadata over plain HTTP should not start claiming it will.
        if (!builder.Environment.IsDevelopment() && parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"'{AuthorityKey}' is '{configured}', which is plain HTTP outside Development. " +
                "Signing keys fetched over a channel an attacker can rewrite make every " +
                "validation below decorative (§11.3).");
        }

        // A local, because the options lambda below captures it and nullable
        // flow analysis does not reach across that boundary.
        string authority = configured;

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.Audience = Audience;

                // Metadata over plain HTTP is a development affordance only —
                // §14.1's Keycloak is http://keycloak:8080. Anywhere else this
                // is what stops the signing keys being fetched over a channel
                // an attacker can rewrite, which would make every other
                // validation below decorative.
                options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();

                // The framework default, written out because §11.4's whole
                // subject rule rests on it. Keycloak issues `sub`;
                // ICurrentUser.Id reads ClaimTypes.NameIdentifier, and this is
                // the only thing that turns one into the other. Set it false —
                // or have a future release change the default — and every
                // authenticated request throws on a token that is perfectly
                // valid, which is a failure no realm test and no unit test
                // over an injected principal can see.
                options.MapInboundClaims = true;

                // Assigned whole rather than mutated, because the four
                // Validate* flags default to true and writing them out is the
                // point: this block is the checklist a reader audits, and a
                // default is not a decision anybody can see. The post-configure
                // step still fills ValidAudience from Audience above and the
                // issuer from the discovery document, so nothing is lost.
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,

                    // The default is five minutes, which keeps an expired token
                    // working for five minutes past its own exp. Thirty seconds
                    // absorbs real drift between NTP-synced hosts and nothing
                    // else (§11.3). It says nothing about revocation: nbf and
                    // exp are all a lifetime check reads, so a token revoked at
                    // the provider stays valid here until it expires whatever
                    // this value is.
                    ClockSkew = TimeSpan.FromSeconds(30),

                    // A display name, for logs and audit lines. It does NOT
                    // compete with the subject: NameIdentifier stays the stable
                    // identifier ICurrentUser.Id reads, and reading
                    // Identity.Name as the key to a record would work in every
                    // test and break the first time somebody changed their
                    // username (§11.4).
                    NameClaimType = "preferred_username",
                    RoleClaimType = "roles"
                };
            });

        return builder;
    }
}
