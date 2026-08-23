using System.Net;
using Catalog.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>
/// The host builds under <c>ValidateOnBuild</c> and answers what an empty
/// service can already be asked: the probes (§13.5) and the OpenAPI document
/// (Appendix C, PR-07). One factory for the class — each test sends one
/// request, and a host per test buys nothing.
/// </summary>
/// <remarks>
/// Both connection strings are required rather than optional, and supplying
/// them is not a workaround. §13.5's rule is that a host with a connection
/// string has a readiness check and a host without one does not; Catalog
/// acquired the SQL pair in PR-08 and the bus pair in PR-13, and both
/// registrations throw on a missing key — so a service host with no database
/// or no broker configured does not start, which is the correct behaviour and
/// worth having a class depend on. The values point at names that cannot
/// resolve, because these tests are about wiring rather than about the
/// engines. <c>DatabaseSmokeTests</c> is where real ones answer.
/// </remarks>
public class HostSmokeTests(HostSmokeTests.UnreachableInfrastructureFactory factory)
    : IClassFixture<HostSmokeTests.UnreachableInfrastructureFactory>
{
    /// <summary>
    /// A parameterless factory, because that is what <c>IClassFixture</c> can
    /// construct. <c>.invalid</c> is reserved and never resolves, so both
    /// checks fail on NXDOMAIN rather than on a timeout — and
    /// <c>Connect Timeout=1</c> bounds the case where a resolver answers
    /// anyway. The bus needs no such bound: <c>WaitUntilStarted</c> is false
    /// (the registration argues it), so the host never waits on the broker at
    /// all.
    /// </summary>
    public sealed class UnreachableInfrastructureFactory() : CatalogApiFactory(
        "Server=catalog-sql.invalid,1433;Database=Catalog;User Id=sa;" +
        "Password=not-a-real-password;Encrypt=False;Connect Timeout=1",
        "amqp://guest:guest@catalog-rabbit.invalid:5672")
    {
        /// <summary>
        /// The one host in the repository that keeps the production JWT scheme.
        /// Every other factory swaps in <c>TestAuthHandler</c>, which is what
        /// lets those suites authenticate at all — and precisely why none of
        /// them can say whether its headers mean anything to a real
        /// deployment. A test scheme cannot prove its own absence.
        /// </summary>
        protected override void ConfigureAuthentication(IServiceCollection services)
        {
            // Deliberately empty. Not "not yet" — restoring the base call here
            // would silently delete EndpointSecurityTests, which is the only
            // suite that reads this host as a deployment rather than a fixture.
        }
    }

    [Fact]
    public async Task Live_probe_returns_200()
    {
        // Also the assertion that readiness has not leaked into liveness.
        // §13.5 forbids liveness touching a dependency — "a brief database
        // outage restarts every pod simultaneously" — and both checks this
        // host registers are unreachable, so a liveness probe that consulted
        // either would answer 503 here.
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void Ready_probe_reports_the_sql_redis_and_bus_checks()
    {
        // Registration, read without a network round trip. §13.5's concern is
        // that "reports ready immediately" and "readiness was never wired up"
        // are indistinguishable from outside, so the wiring is asserted
        // directly rather than inferred from a status code.
        HealthCheckServiceOptions options = factory.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value;

        // Four since §8.5's PR, and the count is the assertion rather
        // than a detail of it: an inventory that only ever grows silently
        // is how a readiness check gets dropped without anything going
        // red. This test failed when the two Redis lines were added,
        // which is what it is for.
        options.Registrations.Count.ShouldBe(4);

        HealthCheckRegistration sql = options.Registrations.Single(r => r.Name == "sql");
        sql.Tags.ShouldContain("ready", "an untagged check is invisible to the /health/ready predicate");

        // Registered by AddMassTransit itself, not by AddCatalogInfrastructure
        // — name and tags read from the 8.5.3 source, asserted here so a
        // MassTransit major that changes either fails this test rather than a
        // cluster's readiness.
        // §13.5 prints both lines and the code carried neither until §8.5's
        // PR gave this service its Redis connection strings. The rule that
        // section states is what makes them owed: a host with a connection
        // string has a readiness check. AbortOnConnectFail is false (§8.1), so
        // without these the pod sits Ready while every claim fails closed.
        //
        // Both, because §8.1 gives the two instances different eviction
        // policies and therefore different servers — a healthy cache says
        // nothing about the instance idempotency claims are written to.
        HealthCheckRegistration cache = options.Registrations.Single(r => r.Name == "redis-cache");
        cache.Tags.ShouldContain("ready", "an untagged check is invisible to the /health/ready predicate");

        HealthCheckRegistration coordination =
            options.Registrations.Single(r => r.Name == "redis-coordination");
        coordination.Tags.ShouldContain("ready", "§8.5's claims are written to this instance");

        HealthCheckRegistration bus = options.Registrations.Single(r => r.Name == "masstransit-bus");
        bus.Tags.ShouldContain("ready", "a bus check outside the ready predicate reports to nobody");
        bus.Tags.ShouldContain("masstransit", "both tags are the documented contract (§13.5), so both are pinned");
    }

    [Fact]
    public async Task Ready_probe_returns_503_when_dependencies_are_unreachable()
    {
        // The other half of the pair above. The registration test fails if the
        // AddSqlServer line is deleted; this one fails if the checks are
        // registered but the predicate stops selecting them. Neither alone
        // catches both.
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task OpenApi_document_is_served()
    {
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
    }
}
