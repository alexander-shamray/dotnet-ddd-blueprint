using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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
}
