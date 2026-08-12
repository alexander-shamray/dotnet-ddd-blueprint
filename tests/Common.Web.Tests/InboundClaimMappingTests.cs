using System.Security.Claims;
using Common.Application;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>
/// The one step between a real token and <see cref="ICurrentUser.Id"/> that
/// nothing else in this repository exercises: Keycloak issues <c>sub</c>, the
/// port reads <see cref="ClaimTypes.NameIdentifier"/>, and inbound claim
/// mapping is the only thing that turns one into the other.
/// </summary>
/// <remarks>
/// Every other suite starts one step past the gap. <c>RealmImportTests</c>
/// proves the realm emits <c>sub</c>; <c>HttpContextCurrentUserTests</c> builds
/// a principal that already carries <c>NameIdentifier</c>. Both stay green with
/// <c>MapInboundClaims</c> off, and every authenticated request in the platform
/// throws — a valid token, a correct realm, and a subject nobody can read.
///
/// So this one starts from a signed token carrying a raw <c>sub</c> and asserts
/// what a handler would see. The signing key is supplied directly as
/// <c>options.Configuration</c>, which is what makes it a unit test rather than
/// a container one: <c>JwtBearerHandler</c> uses the configuration it is given
/// and never fetches the discovery document, so the unreachable authority in
/// <c>TelemetryHost</c> stays unreachable and nothing touches the network.
/// </remarks>
public class InboundClaimMappingTests
{
    private const string Issuer = "https://identity.invalid/realms/test";

    private static readonly SymmetricSecurityKey SigningKey =
        new(System.Text.Encoding.UTF8.GetBytes("a-test-signing-key-of-sufficient-length-for-hmac-sha256"));

    [Fact]
    public async Task A_raw_sub_claim_reaches_ICurrentUser_as_the_subject()
    {
        Guid subject = Guid.CreateVersion7();

        using IHost host = await StartAsync();
        HttpClient client = host.GetTestClient();

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token(subject));

        HttpResponseMessage response = await client.GetAsync(
            new Uri("/subject", UriKind.Relative),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.ShouldBe(subject.ToString());
    }

    [Fact]
    public async Task Without_the_mapping_the_same_token_has_no_subject_at_all()
    {
        // The other half, and the reason the line is written out in
        // AddJwtAuthentication rather than left to the framework default: this
        // is what the platform looks like if that default ever changes. The
        // token is identical and still valid — it authenticates, reaches the
        // endpoint, and carries a claim called `sub` that nothing reads.
        Guid subject = Guid.CreateVersion7();

        using IHost host = await StartAsync(mapInboundClaims: false);
        HttpClient client = host.GetTestClient();

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token(subject));

        // The throw itself, rather than a status code: this pipeline carries no
        // exception handler, so TestServer rethrows what the terminal delegate
        // raised. In a real host §10.5 would turn it into a 500 — which is the
        // point being made, since a 500 on every authenticated request is what
        // this option going quiet actually costs.
        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => client.GetAsync(
                new Uri("/subject", UriKind.Relative),
                TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain(ClaimTypes.NameIdentifier);
    }

    private static string Token(Guid subject)
    {
        SecurityTokenDescriptor descriptor = new()
        {
            Issuer = Issuer,
            Audience = AuthenticationExtensions.Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["sub"] = subject.ToString() },
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static Task<IHost> StartAsync(bool mapInboundClaims = true) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();

                web.ConfigureServices(services =>
                {
                    services.AddHttpContextAccessor();
                    services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

                    services
                        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                        .AddJwtBearer(options =>
                        {
                            // The shipped registration's own values (§11.3),
                            // with the signing key handed over directly so no
                            // discovery document is ever fetched.
                            options.Audience = AuthenticationExtensions.Audience;
                            options.MapInboundClaims = mapInboundClaims;
                            options.TokenValidationParameters = new TokenValidationParameters
                            {
                                ValidateIssuer = true,
                                ValidIssuer = Issuer,
                                ValidateAudience = true,
                                ValidateLifetime = true,
                                ValidateIssuerSigningKey = true,
                                IssuerSigningKey = SigningKey,
                                ClockSkew = TimeSpan.FromSeconds(30),
                                NameClaimType = "preferred_username",
                                RoleClaimType = "roles"
                            };
                            options.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                        });
                });

                web.Configure(app =>
                {
                    // Authentication only: this suite is about what the handler
                    // puts on the context, and AddAuthorization drags in
                    // routing services no terminal delegate needs.
                    app.UseAuthentication();
                    app.Run(async context =>
                    {
                        ICurrentUser caller = context.RequestServices.GetRequiredService<ICurrentUser>();

                        await context.Response.WriteAsync(caller.Id.ToString());
                    });
                });
            })
            .ConfigureLogging(logging => logging.ClearProviders().AddProvider(NullLoggerProvider.Instance))
            .StartAsync();
}
