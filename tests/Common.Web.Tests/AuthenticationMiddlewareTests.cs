using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Common.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>
/// What the authentication middleware does, and what a composition root's
/// explicit call to it is worth — asserted because the blueprint had the second
/// wrong and no test disagreed.
/// </summary>
/// <remarks>
/// §12.4 called a 401 test "the test that catches <c>UseAuthentication</c>
/// being dropped from the pipeline", and §4.2's ordering table said dropping it
/// leaves "User unpopulated when policies evaluate; every authenticated request
/// 403s". Both were checked by deleting the line from
/// <c>Catalog.Api/Program.cs</c>, and every test in the repository stayed green
/// — because <c>WebApplication</c> adds the authentication and authorization
/// middleware itself whenever the matching services are registered. An explicit
/// call moves them earlier in the pipeline; it is not what puts them there.
///
/// So no status code can catch that deletion in a <c>WebApplication</c> host,
/// and the chapters were amended rather than a test written to prove something
/// untrue. The third test below is the regression guard the other two rest on:
/// were a future release to stop auto-adding it, every service in this platform
/// would hand anonymous callers to its handlers while its authorization kept
/// passing, and this is the only place that would say so.
/// </remarks>
public class AuthenticationMiddlewareTests
{
    // SchemeName, not Scheme: inside ProbeHandler below, `Scheme` binds to
    // AuthenticationHandler<T>'s own inherited property rather than to this
    // constant — the same collision that made TestAuthHandler rename its
    // member, one scope in.
    private const string SchemeName = "Probe";

    private static readonly Guid Subject = Guid.CreateVersion7();

    [Fact]
    public async Task The_middleware_is_what_puts_the_principal_on_the_context()
    {
        // The pairing §11.4 depends on: the middleware populates
        // HttpContext.User and ICurrentUser reads it. Everything downstream —
        // every ownership check, §10.3's rate-limit partition, every audit
        // line — is this one line's output.
        Probe probe = await ExplicitPipelineAsync(useAuthentication: true);

        probe.Status.ShouldBe(HttpStatusCode.OK);
        probe.Authenticated.ShouldBeTrue();
        probe.Id.ShouldBe(Subject.ToString());
    }

    [Fact]
    public async Task Authorization_does_not_authenticate_on_its_own()
    {
        // Where the middleware is genuinely absent, an endpoint behind
        // RequireAuthorization answers 401: the authorization middleware
        // evaluates the policy against HttpContext.User and does not populate
        // it. Worth pinning because the opposite is a natural guess —
        // AuthorizationMiddleware does call IPolicyEvaluator, which does call
        // AuthenticateAsync, and that result only chooses between a challenge
        // and a forbid.
        Probe probe = await ExplicitPipelineAsync(useAuthentication: false);

        probe.Status.ShouldBe(HttpStatusCode.Unauthorized);
        probe.Authenticated.ShouldBeFalse("the handler is never reached");
    }

    [Fact]
    public async Task A_web_application_host_adds_the_middleware_without_being_asked()
    {
        // The finding, and the reason the two tests above build a pipeline by
        // hand: this host calls neither UseAuthentication nor UseAuthorization,
        // and does both. Every service host in the solution is a
        // WebApplication (§4.2), so the explicit lines in each Program.cs are
        // about ORDER — they have to sit above anything that logs the caller —
        // and never about presence.
        //
        // Keep the explicit calls regardless: they are the blueprint's
        // specified shape, they are required by any host that is not a
        // WebApplication, and a pipeline whose order is implicit is one nobody
        // can review.
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        Register(builder.Services);

        await using WebApplication app = builder.Build();
        MapProbe(app);

        await app.StartAsync(TestContext.Current.CancellationToken);

        Probe probe = await SendAsync(app.GetTestClient());

        await app.StopAsync(TestContext.Current.CancellationToken);

        probe.Status.ShouldBe(HttpStatusCode.OK);
        probe.Authenticated.ShouldBeTrue(
            "WebApplication auto-adds the authentication middleware — if this ever fails, every " +
            "service host is handing anonymous callers to its handlers and no other test can see it");
    }

    [Fact]
    public async Task But_it_does_not_repair_the_two_being_written_in_the_wrong_order()
    {
        // The limit of the finding above, and §4.2's ordering table promised
        // the opposite until this test was written. Auto-insertion is
        // suppressed by the markers the explicit calls set, and it repairs an
        // OMISSION rather than an ordering: with both present and reversed,
        // authorization evaluates against a User nothing has populated, and
        // every authenticated request 401s.
        //
        // So the framework protects a host from forgetting a line and not from
        // misplacing one — which is the arrangement a reader would not guess,
        // and the reason the table now separates the two cases.
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        Register(builder.Services);

        await using WebApplication app = builder.Build();

        app.UseAuthorization();
        app.UseAuthentication();
        MapProbe(app);

        await app.StartAsync(TestContext.Current.CancellationToken);

        Probe probe = await SendAsync(app.GetTestClient());

        await app.StopAsync(TestContext.Current.CancellationToken);

        probe.Status.ShouldBe(HttpStatusCode.Unauthorized);
        probe.Authenticated.ShouldBeFalse("the handler is never reached");
    }

    /// <summary>
    /// One request through a pipeline built by hand, with or without the
    /// authentication middleware. A <see cref="HostBuilder"/> rather than a
    /// <see cref="WebApplication"/>, and the third test above is exactly why: a
    /// <c>WebApplication</c> would add the line back and the second test would
    /// assert nothing.
    /// </summary>
    private static async Task<Probe> ExplicitPipelineAsync(bool useAuthentication)
    {
        using IHost host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    Register(services);
                });

                web.Configure(app =>
                {
                    app.UseRouting();

                    // The one line under test.
                    if (useAuthentication)
                        app.UseAuthentication();

                    app.UseAuthorization();

                    app.UseEndpoints(MapProbe);
                });
            })
            .ConfigureLogging(logging => logging.ClearProviders())
            .StartAsync(TestContext.Current.CancellationToken);

        return await SendAsync(host.GetTestClient());
    }

    private static void Register(IServiceCollection services)
    {
        services
            .AddAuthentication(SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ProbeHandler>(SchemeName, _ => { });
        services.AddAuthorization();

        // The pairing AddCommonWebDefaults registers (§11.4). Named here rather
        // than calling that helper, so these hosts hold the moving parts under
        // test and the observability pipeline is not one of them.
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
    }

    /// <summary>
    /// The endpoint reports what <see cref="ICurrentUser"/> saw.
    /// <c>RequireAuthorization</c> so it is genuinely behind the default
    /// policy — without one to evaluate, an anonymous caller would prove
    /// nothing.
    /// </summary>
    private static void MapProbe(IEndpointRouteBuilder endpoints) =>
        endpoints
            .MapGet("/", (ICurrentUser user) => Results.Ok(
                new ProbeBody(user.IsAuthenticated, user.IsAuthenticated ? user.Id.ToString() : null)))
            .RequireAuthorization();

    private static async Task<Probe> SendAsync(HttpClient client)
    {
        using (client)
        {
            HttpRequestMessage request = new(HttpMethod.Get, "/");
            request.Headers.Add(ProbeHandler.SubjectHeader, Subject.ToString());

            HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

            if (response.StatusCode is not HttpStatusCode.OK)
                return new Probe(response.StatusCode, false, null);

            ProbeBody body = (await response.Content.ReadFromJsonAsync<ProbeBody>(
                TestContext.Current.CancellationToken))!;

            return new Probe(response.StatusCode, body.Authenticated, body.Id);
        }
    }

    /// <summary>What one request saw: the wire's answer, and the handler's.</summary>
    private sealed record Probe(HttpStatusCode Status, bool Authenticated, string? Id);

    private sealed record ProbeBody(bool Authenticated, string? Id);

    /// <summary>
    /// A scheme that authenticates whatever subject the request names — the JWT
    /// handler's shape without the signature. What matters here is that a
    /// scheme exists and succeeds, not what it validates.
    /// </summary>
    private sealed class ProbeHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string SubjectHeader = "X-Probe-Subject";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            Claim[] claims = [new(ClaimTypes.NameIdentifier, Request.Headers[SubjectHeader].ToString())];
            ClaimsPrincipal principal = new(new ClaimsIdentity(claims, SchemeName));

            return Task.FromResult(
                AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
