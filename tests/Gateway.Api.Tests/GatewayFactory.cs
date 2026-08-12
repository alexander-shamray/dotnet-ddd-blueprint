using Common.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Api.Tests;

/// <summary>
/// The real gateway host, over the real <c>appsettings.json</c> (§12.4).
/// <see cref="WebApplicationFactory{TEntryPoint}"/> sets the content root to
/// the gateway project's own directory, so the route file these tests read is
/// the one that ships rather than a copy written for them.
/// </summary>
/// <remarks>
/// <para>
/// <b>No test here reaches a destination, and that is a property of the
/// gateway rather than a shortcut.</b> §10.1's gateway owns nothing: every
/// route ends at a service, and the only one of those that exists is reachable
/// only over a Compose network. So the negative paths are driven live —
/// authentication and authorization both answer above the proxy — and the
/// positive ones are asserted against the built host's own routing state and
/// endpoint metadata, which is what YARP actually decided. Driving a request
/// at <c>http://ordering-api:8080</c> from a test host would assert a DNS
/// failure.
/// </para>
/// <para>
/// The authority is fake and unreachable for <c>CatalogApiFactory</c>'s
/// reason: <c>.invalid</c> is reserved and never resolves, so a test that
/// accidentally dials an identity provider fails loudly. It is required rather
/// than optional because <c>AddJwtAuthentication</c> reads the key eagerly and
/// throws naming it (§11.3), the gateway being a host like any other.
/// </para>
/// </remarks>
public class GatewayFactory : WebApplicationFactory<Program>
{
    /// <summary>The authority every host over this <c>Program</c> must name (§11.3).</summary>
    public const string UnreachableAuthority = "https://identity.invalid/realms/test";

    /// <summary>
    /// Extra configuration, layered over the shipped <c>appsettings.json</c>.
    /// Overridden by the subclasses that switch on §4.2's two conditional
    /// blocks and by the one that adds a deliberately unresolvable route.
    /// </summary>
    /// <remarks>
    /// A virtual member rather than a constructor parameter, because xUnit
    /// requires a class fixture to declare exactly one public constructor — so
    /// the type that is shared by a whole test class and the types that are
    /// built per test have to be the same shape.
    /// </remarks>
    protected virtual IEnumerable<KeyValuePair<string, string>> AdditionalSettings => [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(AuthenticationExtensions.AuthorityKey, UnreachableAuthority);

        foreach ((string key, string value) in AdditionalSettings)
            builder.UseSetting(key, value);

        builder.ConfigureServices(ConfigureAuthentication);
    }

    /// <summary>
    /// Replaces the JWT scheme with <see cref="TestAuthHandler"/> (§12.4).
    /// Replacing rather than configuring: the alternative is a fixture that
    /// fetches OIDC metadata over the network from an authority that is
    /// unreachable on purpose.
    /// </summary>
    /// <remarks>
    /// Only the authenticate and challenge schemes are set. Forbid follows the
    /// challenge one — <c>DefaultForbidScheme</c> is unset and
    /// <c>AuthenticationSchemeProvider</c> falls back to
    /// <c>DefaultChallengeScheme</c> before <c>DefaultScheme</c> — so the 403
    /// on the admin route is answered by this handler's inherited forbid.
    /// </remarks>
    private static void ConfigureAuthentication(IServiceCollection services)
    {
        services.Configure<AuthenticationOptions>(o =>
        {
            o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
            o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
        });

        services
            .AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
    }
}
