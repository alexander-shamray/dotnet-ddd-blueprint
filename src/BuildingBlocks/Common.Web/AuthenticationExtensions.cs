using Microsoft.AspNetCore.Authentication.JwtBearer;
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

    /// <summary>
    /// §11.3's access-token lifetime — 300 seconds, normative for the platform
    /// and the larger of the two terms in ADR-033's revocation bound.
    /// </summary>
    /// <remarks>
    /// <b>It is a constant here because something now reads it, and it was a
    /// literal in one test until something did.</b> <c>RealmImportTests</c>
    /// carried the 300 and said outright why it was not declared in this
    /// assembly: "a constant nothing reads would be a registration standing in
    /// for a control, which is the shape ADR-033 was written to withdraw."
    /// That was exactly right while the number was only ever asserted against
    /// the realm this repository ships. <see cref="RevocationBound"/> is what
    /// changed it — that ceiling is composed from this value and every host
    /// holds an inbound token's remaining life to it, so a single declaration
    /// is what keeps the control and the realm assertion from disagreeing
    /// (<see href="https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/157">#157</see>,
    /// ADR-040).
    /// </remarks>
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromSeconds(300);

    /// <summary>
    /// The drift a lifetime check absorbs by accepting a token until <c>exp</c>
    /// plus this — thirty seconds, and the smaller term of the same bound.
    /// </summary>
    private static readonly TimeSpan AllowedClockSkew = TimeSpan.FromSeconds(30);

    /// <summary>
    /// ADR-033's revocation bound: <see cref="AccessTokenLifetime"/> plus
    /// <see cref="AllowedClockSkew"/>, 330 seconds — and, since #157, the
    /// ceiling every host holds an inbound token's <em>remaining</em> life to.
    /// <b>That bounds what a non-conforming realm costs; it does not check the
    /// lifetime such a realm issued</b>, because a long-lived token becomes
    /// admissible as it approaches expiry (ADR-040).
    /// </summary>
    /// <remarks>
    /// <b>Composed rather than written down, because 330 is a sum and a sum
    /// written as a literal is two numbers with one place to drift.</b> Both
    /// terms are above; changing either moves this and moves what the platform
    /// accepts, which is the property ADR-033 is about.
    /// </remarks>
    public static TimeSpan RevocationBound => AccessTokenLifetime + AllowedClockSkew;

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

        // A query or a fragment is absolute, http, and still not a base
        // address. JwtBearer builds its metadata address by appending
        // `/.well-known/openid-configuration` to this string, and appending to
        // `…/realms/commerce#x` puts the suffix inside the fragment — which is
        // never sent to a server. The host would start and the first bearer
        // request would fetch the realm's own page instead of the discovery
        // document, which is exactly the deferred failure the whole guard
        // exists to convert into a deployment error.
        if (parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
        {
            throw new InvalidOperationException(
                $"'{AuthorityKey}' is '{configured}', which carries a query or fragment. It is a " +
                "base address that a well-known path is appended to (§11.3), so anything after " +
                "the path makes the discovery document unreachable.");
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

                // ADR-033's bound, enforced rather than stated (ADR-040).
                // Every sentence in §11.2, §11.3, ADR-033 and ADR-034 reads as
                // a platform guarantee, and the settings behind it live in a
                // realm: `accessTokenLifespan`, and a client-level
                // `access.token.lifespan` that overrides it. RealmImportTests
                // pins both — in §14.1's Compose realm, the only one this
                // repository owns. Every chart points at an externally
                // provisioned authority, so a deployed realm could issue
                // five-hour access tokens with the suite green and the bound
                // untrue everywhere it mattered.
                //
                // A token is the one place the realm's answer is observable
                // WITHOUT A CREDENTIAL AT A HOST, so the check is here:
                // whatever the realm was configured to do, a token that
                // reaches a host carries how long it has left. Since ADR-042
                // the realm is also asked directly, by a deploy-time gate with
                // a credential — a different question at a different moment,
                // and this one is what holds continuously.
                //
                // REMAINING life against this host's clock, not `exp - iat`.
                // The exact form is sharper and was not taken: it needs `iat`,
                // which is optional in RFC 7519, so an issuer omitting it would
                // switch the control off by omission — and reading it means
                // naming a token type from a package this assembly does not
                // pin. `ValidTo` is on SecurityToken itself and `exp` is
                // already mandatory here, because ValidateLifetime below
                // refuses a token without one before this runs.
                //
                // The cost of measuring against our own clock is stated rather
                // than hidden: a host lagging the issuer sees a fresh token as
                // having more life left than it has, so the ceiling is the
                // BOUND — lifetime plus skew — and not the lifetime. A realm at
                // 330 seconds passes and a realm at 320 passes; a fresh token
                // from a realm set to five hours, thirty minutes or six minutes
                // does not.
                //
                // GATING REMAINING LIFE IS NOT ENFORCING THE ISSUED LIFETIME,
                // and the difference is why this is not the whole answer. That
                // five-hour token is refused for four hours and fifty-four
                // minutes and then admitted for its last 330 seconds — so a
                // non-conforming realm is CONTAINED rather than detected. What
                // detects it is ADR-042's gate, which reads the realm at a
                // rollout, and since ADR-043 on a nominally hourly schedule
                // between rollouts — so a realm edited after a rollout is seen
                // at the next scheduled run, as reliably as GitHub runs a
                // schedule (#176), and this is what bounds the window inside
                // it. What this buys is that no token from any realm is ever
                // accepted for more than the window below.
                //
                // THE SKEW IS THEREFORE SPENT TWICE, and that is a real cost
                // rather than an oversight. A token admitted at the ceiling has
                // 330 seconds left, and ValidateLifetime below then accepts it
                // until `exp` plus another 30 — so the longest window this
                // guard admits is 360 seconds, not ADR-033's 330. That bound is
                // unchanged for a CONFORMING realm, where it is produced by the
                // realm's 300 and this ClockSkew and this check never binds at
                // all; what the 360 bounds is a non-conforming token's
                // acceptance, where the alternative was hours.
                //
                // Capping at AccessTokenLifetime would make it exactly 330 and
                // was rejected, because it is a knife-edge rather than a
                // tighter bound. A host whose clock lags the issuer's by δ
                // reads a fresh 300-second token as having 300 + δ left, so any
                // δ above zero refuses every token a correct realm issues —
                // and the 30 seconds that would absorb it is the very term the
                // cap removes. The exact form needs `exp - iat`, which the
                // paragraph above declines for a reason that has not changed.
                //
                // Measured rather than reasoned about: the two boundary cases
                // and the 360 are pinned by JwtAuthenticationTests, so the
                // number in this comment fails a test when it stops being true.
                //
                // Refused, not logged. The posture is the one the authority
                // guard above already takes for metadata over plain HTTP: a
                // platform that accepts what it says it does not accept has a
                // decorative guarantee. A realm that violates the bound fails
                // loudly at every host instead of quietly widening the window
                // between a revocation and its effect.
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        // The scheme's own clock where a host substituted one,
                        // and the system clock otherwise — the same seam the
                        // rest of the authentication stack reads its time from,
                        // so a test can move it without a second mechanism.
                        TimeProvider clock = context.Options.TimeProvider ?? TimeProvider.System;

                        // SpecifyKind rather than a plain conversion: ValidTo is
                        // a DateTime whose Kind the handler is not contracted to
                        // set, and DateTimeOffset reads an Unspecified one as
                        // LOCAL — which on a host east of UTC would subtract
                        // hours from the remaining life and pass everything.
                        DateTimeOffset expires =
                            new(DateTime.SpecifyKind(context.SecurityToken.ValidTo, DateTimeKind.Utc));

                        if (expires - clock.GetUtcNow() <= RevocationBound)
                            return Task.CompletedTask;

                        // Two causes, and naming only the first sends an
                        // operator to change a realm that is correct. The
                        // comparison reads this host's clock, so an issuer
                        // running more than the skew AHEAD of it makes a
                        // conforming 300-second token look long-lived here.
                        context.Fail(
                            $"The token has more than {RevocationBound.TotalSeconds} seconds of life " +
                            "left, which is longer than the revocation bound this platform states " +
                            "(ADR-033). Either the realm that issued it sets an access-token " +
                            "lifetime, or a client-level override, above what §11.3 requires — or " +
                            "this host's clock is running behind the issuer's by more than the " +
                            "skew, which makes a conforming token read as a long-lived one. Check " +
                            "the clocks before changing the realm.");

                        return Task.CompletedTask;
                    }
                };

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
                    //
                    // Read from the field rather than written again, because
                    // RevocationBound above is this plus the lifetime and a 30
                    // in two places is a sum that agrees until one of them is
                    // edited.
                    ClockSkew = AllowedClockSkew,

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
