using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>
/// §10.3's limiter, driven until it rejects. Registration without middleware is
/// the quiet failure mode here — <c>AddRateLimiter</c> succeeds and does
/// nothing at all if <c>UseRateLimiter</c> is missing — and nothing else in the
/// solution would notice.
/// </summary>
/// <remarks>
/// <para>
/// One test rather than two, and the reason is cost rather than taste:
/// exhausting the window is a hundred proxied requests, a fixed window is
/// per-host state, and a second test asserting the rejection's shape would
/// have to build a second gateway and spend a second budget to reach the same
/// response. So the budget and the shape are asserted on one exhaustion.
/// </para>
/// <para>
/// The stub behind it is shared with the other proxy tests: it holds no
/// budget.
/// </para>
/// </remarks>
public sealed class RateLimitedRouteTests(StubDestination stub) : IClassFixture<StubDestination>
{
    /// <summary>The <c>anonymous</c> policy's fixed window (§10.3).</summary>
    private const int PermitLimit = 100;

    private const string PublicRoute = "/api/v1/catalog/products";

    /// <summary>
    /// The window admits exactly its budget, and the request past it is
    /// refused in the platform's one error shape (§10.5). §10.3 printed a
    /// <c>WriteAsJsonAsync</c> that produced <c>application/json</c> and none
    /// of the three customisation members; writing through
    /// <c>IProblemDetailsService</c> is what makes a 429 look like every other
    /// problem response, and the chapter was amended.
    /// </summary>
    [Fact]
    public async Task The_anonymous_window_admits_its_budget_and_refuses_the_next_as_problem_json()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        for (int i = 0; i < PermitLimit; i++)
        {
            HttpResponseMessage permitted = await client.GetAsync(PublicRoute, ct);

            permitted.StatusCode.ShouldBe(
                HttpStatusCode.NoContent,
                $"request {i + 1} of {PermitLimit} is inside the window");
        }

        HttpResponseMessage rejected = await client.GetAsync(PublicRoute, ct);

        rejected.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        rejected.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using JsonDocument body = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync(ct));

        body.RootElement.GetProperty("status").GetInt32().ShouldBe(429);
        body.RootElement.GetProperty("title").GetString().ShouldBe("Too many requests");
        body.RootElement.GetProperty("correlationId").GetString().ShouldNotBeNullOrWhiteSpace();
    }
}
