using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>
/// The host builds under <c>ValidateOnBuild</c> and answers the three
/// questions an empty service can already be asked: the two probes (§13.5)
/// and the OpenAPI document (Appendix C, PR-07). One factory for the class —
/// each test sends one request, and a host per test buys nothing.
/// </summary>
public class HostSmokeTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Live_probe_returns_200()
    {
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ready_probe_returns_200_when_no_readiness_checks_are_registered()
    {
        // §13.5: a host with no connection string registers no readiness
        // check, and an empty predicate set is a passing predicate set.
        // Catalog acquires its SQL check with its SQL connection in PR-08.
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
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
