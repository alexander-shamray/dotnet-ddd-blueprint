using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>
/// §11.3's validation parameters, read back off the built options. Every value
/// here is one an attacker benefits from being wrong, and none of them fails
/// visibly: a token validated without an audience check is accepted, answers
/// 200, and is indistinguishable from a correct one until somebody presents a
/// token this platform never issued.
/// </summary>
public class JwtAuthenticationTests
{
    private static JwtBearerOptions Options(string? environmentName = null)
    {
        HostApplicationBuilder builder = TelemetryHost.Builder(environmentName);

        builder.AddCommonWebDefaults();

        // Get, not CurrentValue: the post-configure step that fills
        // ValidAudience from Audience runs per named scheme, and reading the
        // unnamed default would report a set of options no request ever uses.
        return builder.Build().Services
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
    }

    [Fact]
    public void The_authority_comes_from_configuration_and_the_audience_does_not()
    {
        JwtBearerOptions options = Options();

        options.Authority.ShouldBe(TelemetryHost.Authority);

        // A constant, not a key. §11.5 settles on one audience for the whole
        // platform — per-service audiences are a later split — and §15.4's rule
        // is that an options type needs a member that differs between
        // environments. Pinning it here is what makes the realm's audience
        // mapper checkable at all: RealmImportTests compares the shipped realm
        // against this same constant.
        options.Audience.ShouldBe(AuthenticationExtensions.Audience);
        options.TokenValidationParameters.ValidAudience.ShouldBe(AuthenticationExtensions.Audience);
    }

    [Fact]
    public void Every_validation_is_on_and_the_clock_skew_is_thirty_seconds()
    {
        JwtBearerOptions options = Options();

        options.TokenValidationParameters.ValidateIssuer.ShouldBeTrue();
        options.TokenValidationParameters.ValidateAudience.ShouldBeTrue();
        options.TokenValidationParameters.ValidateLifetime.ShouldBeTrue();
        options.TokenValidationParameters.ValidateIssuerSigningKey.ShouldBeTrue();

        // The framework default is five minutes, which keeps an expired token
        // working for five minutes past its own exp (§11.3). Lifetime only:
        // revocation is not a thing a clock skew can reach.
        options.TokenValidationParameters.ClockSkew.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void The_subject_and_the_display_name_are_different_claims()
    {
        JwtBearerOptions options = Options();

        // NameClaimType sets what Identity.Name returns — a display name, for
        // logs and audit lines. It does not compete with the subject:
        // ICurrentUser.Id reads NameIdentifier, and reading Identity.Name as
        // the key to a record would work in every test and break the first time
        // somebody changed their username (§11.4).
        options.TokenValidationParameters.NameClaimType.ShouldBe("preferred_username");
        options.TokenValidationParameters.RoleClaimType.ShouldBe("roles");

        // And the step that connects the two claim vocabularies at all.
        // Keycloak issues `sub`; ICurrentUser.Id reads NameIdentifier. This is
        // the framework default, pinned because §11.4's whole subject rule
        // rests on it and a changed default would break every authenticated
        // request in the platform without breaking a single assertion above.
        // InboundClaimMappingTests exercises the effect end to end; this line
        // is the cheap guard that says which value the effect depends on.
        options.MapInboundClaims.ShouldBeTrue();
    }

    [Fact]
    public void Metadata_over_plain_http_is_a_development_affordance_only()
    {
        // §14.1's Keycloak is http://keycloak:8080, so Development has to
        // allow it. Anywhere else this is what stops the signing keys being
        // fetched over a channel an attacker can rewrite — which would make
        // every assertion above decorative, because the keys are what the
        // signature is checked against.
        Options(Environments.Development).RequireHttpsMetadata.ShouldBeFalse();
        Options(Environments.Production).RequireHttpsMetadata.ShouldBeTrue();

        // TelemetryHost's own environment is neither, and the default matters:
        // anything that is not Development must require HTTPS, rather than
        // only the one environment somebody remembered to name.
        Options().RequireHttpsMetadata.ShouldBeTrue();
    }

    [Fact]
    public void The_revocation_bound_is_the_lifetime_plus_the_skew()
    {
        // ADR-033's number, composed rather than written down. The two terms
        // are asserted separately above and here; what this pins is that the
        // bound is their SUM, because a literal 330 beside a 300 and a 30 is
        // the arithmetic nobody redoes when one of them moves.
        AuthenticationExtensions.RevocationBound.ShouldBe(
            AuthenticationExtensions.AccessTokenLifetime +
            Options().TokenValidationParameters.ClockSkew);

        AuthenticationExtensions.RevocationBound.ShouldBe(TimeSpan.FromSeconds(330));
    }

    [Fact]
    public async Task A_token_with_more_life_left_than_the_bound_is_refused()
    {
        // #157's control. §11.3's 300 seconds is a realm setting, and every
        // chart points at a realm this repository holds no configuration for —
        // so before this the platform could be handed five-hour access tokens
        // with every test green and ADR-033's bound untrue in the only place
        // it mattered. A token is where the realm's answer is observable
        // without credentials, and this is what reads it.
        TokenValidatedContext context = Validated(
            AuthenticationExtensions.RevocationBound + TimeSpan.FromSeconds(30));

        await context.Options.Events.OnTokenValidated(context);

        context.Result.ShouldNotBeNull();
        context.Result.Failure.ShouldNotBeNull();
        context.Result.Failure.Message.ShouldContain("revocation bound");
    }

    [Fact]
    public async Task A_token_inside_the_bound_is_accepted()
    {
        // The companion, and the one that stops the test above passing for the
        // wrong reason. An OnTokenValidated that failed unconditionally would
        // satisfy it and refuse every request the platform ever serves; this
        // is what says the ceiling is a ceiling rather than a wall. The realm
        // this repository ships issues exactly AccessTokenLifetime, so the
        // value below is the shipped case and not a convenient one.
        TokenValidatedContext context = Validated(AuthenticationExtensions.AccessTokenLifetime);

        await context.Options.Events.OnTokenValidated(context);

        // Null rather than a success result: nothing in the handler has set one
        // at this point, and Fail is the only thing that would.
        context.Result.ShouldBeNull();
    }

    [Fact]
    public async Task A_token_exactly_at_the_bound_is_accepted_and_a_second_more_is_not()
    {
        // The boundary, both sides, because a ceiling asserted only from the
        // outside is a ceiling that could be anywhere below the value tested.
        // The comparison is `<=`, so the bound itself is admitted.
        //
        // A FROZEN CLOCK IS WHAT MAKES THIS THE BOUNDARY, and without it this
        // test was decorative. `ValidTo` used to be computed per access from
        // `DateTime.UtcNow`, and the handler read the clock again afterwards —
        // so the "at bound" token was already a few ticks under the ceiling and
        // changing the production comparison from `<=` to `<` passed anyway.
        // The over-bound case had the mirror defect: a long enough pause
        // between construction and the read would have flipped it. Both sides
        // now derive from one instant that does not move.
        TokenValidatedContext atBound = Validated(AuthenticationExtensions.RevocationBound);
        await atBound.Options.Events.OnTokenValidated(atBound);
        atBound.Result.ShouldBeNull("the comparison is `<=`, so the bound itself is admitted");

        TokenValidatedContext overBound =
            Validated(AuthenticationExtensions.RevocationBound + TimeSpan.FromSeconds(1));
        await overBound.Options.Events.OnTokenValidated(overBound);
        overBound.Result.ShouldNotBeNull();
        overBound.Result.Failure.ShouldNotBeNull("one second past the bound is past it");
    }

    [Fact]
    public void The_longest_window_this_guard_admits_is_the_bound_plus_the_skew()
    {
        // The skew is spent twice and this is where the cost is measured rather
        // than argued. A token admitted at the ceiling has RevocationBound left;
        // ValidateLifetime then accepts it until `exp` plus ClockSkew — so the
        // longest acceptance window is 360 seconds, where ADR-033's bound for a
        // CONFORMING realm is 330. Both numbers are real and they answer
        // different questions, which is why this asserts the sum rather than
        // quietly restating the smaller one.
        //
        // Capping the ceiling at AccessTokenLifetime would make the two equal
        // and was rejected: a host lagging the issuer by any amount reads a
        // fresh 300-second token as having more than 300 left, so every token a
        // correct realm issues would be refused. The assertion below is what
        // makes that trade visible instead of implicit.
        TimeSpan skew = Options().TokenValidationParameters.ClockSkew;

        (AuthenticationExtensions.RevocationBound + skew).ShouldBe(TimeSpan.FromSeconds(360));

        AuthenticationExtensions.RevocationBound.ShouldBeGreaterThan(
            AuthenticationExtensions.AccessTokenLifetime,
            "the ceiling has to exceed the lifetime by the drift a lagging host reads into it, " +
            "or a correct realm's tokens are refused");
    }

    /// <summary>
    /// The context <c>OnTokenValidated</c> is handed, carrying a token with
    /// <paramref name="remaining"/> left to live.
    /// </summary>
    /// <remarks>
    /// <b>Built rather than obtained from a real handshake, because what is
    /// under test is one comparison and a real token would bring a signature,
    /// an issuer and a discovery document with it.</b> §11.5's container-backed
    /// suite is where a real token is minted; this is the arithmetic.
    /// </remarks>
    private static TokenValidatedContext Validated(TimeSpan remaining)
    {
        JwtBearerOptions options = Options();

        // The scheme's own clock, frozen, and the token's expiry derived from
        // the same instant. The handler reads `context.Options.TimeProvider`,
        // so this is the seam it actually uses rather than a clock beside it —
        // and it is why every caller above drives `context.Options.Events`
        // instead of a fresh host's, which would carry a different options
        // instance and therefore the system clock.
        FrozenClock clock = new(DateTimeOffset.UtcNow);
        options.TimeProvider = clock;

        return new TokenValidatedContext(
            new DefaultHttpContext(),
            new AuthenticationScheme(
                JwtBearerDefaults.AuthenticationScheme,
                displayName: null,
                handlerType: typeof(JwtBearerHandler)),
            options)
        {
            SecurityToken = new TokenExpiringIn((clock.GetUtcNow() + remaining).UtcDateTime),
            Principal = new ClaimsPrincipal(new ClaimsIdentity())
        };
    }

    /// <summary>
    /// A token that answers one question — when it expires — and refuses every
    /// other, so a check that read anything else would fail loudly here rather
    /// than quietly in a deployment.
    /// </summary>
    private sealed class TokenExpiringIn(DateTime expires) : SecurityToken
    {
        public override string Id => nameof(TokenExpiringIn);

        public override string Issuer => TelemetryHost.Authority;

        public override SecurityKey SecurityKey => throw new NotSupportedException();

        public override SecurityKey SigningKey
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override DateTime ValidFrom =>
            ValidTo - AuthenticationExtensions.AccessTokenLifetime;

        // A FIXED instant, not `DateTime.UtcNow + remaining` evaluated per
        // access. A property recomputing the clock puts the expiry a few ticks
        // nearer on every read, which is what made the boundary case pass
        // against a `<` the production code does not use.
        public override DateTime ValidTo { get; } = expires;
    }

    /// <summary>
    /// A clock that does not move, so a boundary is a boundary.
    /// </summary>
    /// <remarks>
    /// Hand-written rather than <c>FakeTimeProvider</c>, on the same terms as
    /// <c>ServiceFixture</c>'s skewed clock: the package is pinned centrally,
    /// this project does not reference it, and a licence-register entry to
    /// freeze a clock in four lines is not a trade worth making.
    /// </remarks>
    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
