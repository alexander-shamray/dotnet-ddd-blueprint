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
/// The connection string is required rather than optional, and supplying one
/// is not a workaround. §13.5's rule is that a host with a connection string
/// has a readiness check and a host without one does not; Catalog acquired both
/// in PR-08, and <c>AddSqlServer</c> throws on a null connection string — so a
/// service host with no database configured does not start, which is the
/// correct behaviour and worth having a class depend on. The value points at a
/// name that cannot resolve, because these tests are about wiring rather than
/// about SQL Server. <c>DatabaseSmokeTests</c> is where a real engine answers.
/// </remarks>
public class HostSmokeTests(HostSmokeTests.UnreachableSqlFactory factory)
    : IClassFixture<HostSmokeTests.UnreachableSqlFactory>
{
    /// <summary>
    /// A parameterless factory, because that is what <c>IClassFixture</c> can
    /// construct. <c>.invalid</c> is reserved and never resolves, so the
    /// readiness check fails on NXDOMAIN rather than on a timeout — and
    /// <c>Connect Timeout=1</c> bounds the case where a resolver answers
    /// anyway.
    /// </summary>
    public sealed class UnreachableSqlFactory() : CatalogApiFactory(
        "Server=catalog-sql.invalid,1433;Database=Catalog;User Id=sa;" +
        "Password=not-a-real-password;Encrypt=False;Connect Timeout=1");

    [Fact]
    public async Task Live_probe_returns_200()
    {
        // Also the assertion that readiness has not leaked into liveness.
        // §13.5 forbids liveness touching a dependency — "a brief database
        // outage restarts every pod simultaneously" — and the SQL check this
        // host registers is unreachable, so a liveness probe that consulted it
        // would answer 503 here.
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void Ready_probe_reports_the_sql_check()
    {
        // Registration, read without a network round trip. §13.5's concern is
        // that "reports ready immediately" and "readiness was never wired up"
        // are indistinguishable from outside, so the wiring is asserted
        // directly rather than inferred from a status code.
        HealthCheckServiceOptions options = factory.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value;

        HealthCheckRegistration sql = options.Registrations.ShouldHaveSingleItem();

        sql.Name.ShouldBe("sql");
        sql.Tags.ShouldContain("ready", "an untagged check is invisible to the /health/ready predicate");
    }

    [Fact]
    public async Task Ready_probe_returns_503_when_sql_is_unreachable()
    {
        // The other half of the pair above. The registration test fails if the
        // AddSqlServer line is deleted; this one fails if the check is
        // registered but the predicate stops selecting it. Neither alone
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
