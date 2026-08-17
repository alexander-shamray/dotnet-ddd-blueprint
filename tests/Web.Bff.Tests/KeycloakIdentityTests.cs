using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using Common.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>
/// §11.5's whole argument, against a real Keycloak: that the scope becomes an
/// audience, that the audience is what a service validates, and that neither
/// is granted to a client the realm merely happens to hold.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is realm configuration, which is exactly why it earns a test rather
/// than a paragraph.</b> Nothing in the solution compiles differently when the
/// audience mapper is missing: a client-credentials token would carry
/// <c>scope: commerce-api</c> and an <c>aud</c> of <c>account</c>, and every
/// service would reject the platform's one permitted synchronous hop.
/// </para>
/// <para>
/// The negative half matters more than the positive, and §11.5 says so: a
/// mapper that added the audience to <i>every</i> token would pass the first
/// test here and hand the platform to any client the realm holds. That is what
/// the unrelated client is for.
/// </para>
/// </remarks>
[Collection(nameof(KeycloakCollection))]
public sealed class KeycloakIdentityTests(KeycloakFixture keycloak)
{
    private const string BffClient = "web-bff";
    private const string BffSecret = "local-dev-secret";

    private static readonly JwtSecurityTokenHandler Tokens = new();

    [Fact]
    public async Task The_BFF_client_credentials_token_carries_the_platform_audience()
    {
        (bool granted, string token) = await keycloak.ClientCredentialsAsync(BffClient, BffSecret);

        granted.ShouldBeTrue("the realm must hold web-bff with service accounts enabled (§11.5).");

        JwtSecurityToken jwt = Tokens.ReadJwtToken(token);

        // The one claim the whole of §11.5 is about. `scope: commerce-api` and
        // `aud: commerce-api` are NOT the same claim, and nothing makes one
        // imply the other but the realm's audience mapper.
        jwt.Audiences.ShouldContain(AuthenticationExtensions.Audience);
    }

    [Fact]
    public async Task The_service_account_carries_no_permission_claim()
    {
        (_, string token) = await keycloak.ClientCredentialsAsync(BffClient, BffSecret);

        JwtSecurityToken jwt = Tokens.ReadJwtToken(token);

        // §11.4's vocabulary belongs to people, not to hosts. A service account
        // arriving with permissions would make every ownership and policy check
        // in the platform satisfiable by a host — which is why Catalog's gRPC
        // service requires authentication and deliberately not a permission.
        jwt.Claims.ShouldNotContain(c => c.Type == PermissionClaim.Type);
    }

    [Fact]
    public async Task A_service_validating_the_realm_accepts_that_token()
    {
        (_, string token) = await keycloak.ClientCredentialsAsync(BffClient, BffSecret);

        await using WebApplication service = await ServiceValidatingTheRealm();
        using HttpClient client = service.GetTestClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/protected");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        // The real AddJwtAuthentication, the real discovery document, the real
        // signing keys, the real audience constant. This is the assertion
        // §12.4's fixture structurally cannot make.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_client_without_the_scope_is_refused_by_that_service()
    {
        const string Unrelated = "unrelated-client";
        const string Secret = "unrelated-secret";

        await keycloak.CreateUnrelatedClientAsync(Unrelated, Secret);

        (bool granted, string token) = await keycloak.ClientCredentialsAsync(Unrelated, Secret);

        // It gets a perfectly valid token — same realm, same issuer, same
        // signing key. What it does not get is the audience.
        granted.ShouldBeTrue();
        Tokens.ReadJwtToken(token).Audiences.ShouldNotContain(AuthenticationExtensions.Audience);

        await using WebApplication service = await ServiceValidatingTheRealm();
        using HttpClient client = service.GetTestClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/protected");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A minimal host running the platform's real token validation against the
    /// container.
    /// </summary>
    /// <remarks>
    /// §11.5 prints this half as a call to Catalog, and a service is what it
    /// stands for; what is actually under test is <c>AddJwtAuthentication</c>
    /// plus the realm, and neither of those is Catalog's. Driving a real
    /// service here would add a SQL container and a migrator run to a suite
    /// whose subject is a token — and it would still be asserting this.
    /// </remarks>
    private async Task<WebApplication> ServiceValidatingTheRealm()
    {
        // Development, because the container speaks plain HTTP:
        // AddJwtAuthentication refuses a non-https authority outside
        // Development and RequireHttpsMetadata would stop the discovery
        // document being fetched at all (§11.3). Both are the same rule, and
        // the container is the case they carve out.
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });

        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Configuration[AuthenticationExtensions.AuthorityKey] = keycloak.Authority;

        // The platform's own registration, not a copy of it. A hand-rolled
        // AddJwtBearer here would validate whatever this file decided to
        // validate and prove nothing about what a service does.
        builder.AddJwtAuthentication();
        builder.Services.AddAuthorizationBuilder();

        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/protected", () => Results.Ok()).RequireAuthorization();

        await app.StartAsync(TestContext.Current.CancellationToken);

        return app;
    }
}
