using System.Net;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>
/// What the BFF's own pipeline answers, as opposed to what its handlers do.
/// </summary>
/// <remarks>
/// <b>Composition is the subject, not behaviour.</b> `Common.Web` owns
/// <c>UseSecurityHeaders</c> (§10.6) and <c>MapCommonHealthEndpoints</c>
/// (§13.5), and both are proved in `Common.Web.Tests` — what neither of those
/// suites can say is whether *this* host calls them. Delete either line from
/// `Program.cs` and every building-block test stays green, which is the exact
/// failure mode the commit that added the header named and then left uncovered
/// on this host and the gateway.
/// <para>
/// No <see cref="BffFactory.PricingAddress"/> is set, deliberately: neither
/// question reaches Catalog, and a fixture that stood one up would make these
/// two assertions depend on a stub they have nothing to do with.
/// </para>
/// </remarks>
public sealed class HostPipelineTests
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/health/startup")]
    public async Task Health_probes_answer_without_a_token(string path)
    {
        // Ready and startup are healthy with an empty check set, which this
        // host declares at the call site with ownsNoReadinessDependencies (§13.5) — and
        // the probes must stay anonymous because the kubelet carries no token.
        // Every other route here is behind the group's RequireAuthorization,
        // so a probe that lost its AllowAnonymous would 401 and the pod would
        // be restarted in a loop.
        using BffFactory factory = new();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Every_response_carries_nosniff()
    {
        using BffFactory factory = new();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
    }
}
