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

    /// <summary>The <c>authenticated</c> policy's token bucket (§10.3).</summary>
    private const int TokenLimit = 300;

    private const string PublicRoute = "/api/v1/catalog/products";

    /// <summary>Authenticated at the edge, and rate-limited per subject (§10.2).</summary>
    private const string AuthenticatedRoute = "/api/v1/orders/018f4c2e";

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

        // The header is sent at all, which nothing asserted before this round.
        // It does NOT pin the rounding: this window is a minute long, so a
        // rejection here carries tens of seconds and the truncating cast the
        // handler used to hold satisfies the floor exactly as the ceiling does
        // — measured, after a comment here claimed otherwise. The rounding is
        // pinned in RetryAfterHeaderTests, on the sub-second input this path
        // cannot produce without holding a window open for fifty-nine seconds.
        rejected.Headers.RetryAfter.ShouldNotBeNull();
        rejected.Headers.RetryAfter!.Delta.ShouldNotBeNull();
        rejected.Headers.RetryAfter.Delta!.Value.ShouldBeGreaterThan(TimeSpan.Zero);

        using JsonDocument body = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync(ct));

        body.RootElement.GetProperty("status").GetInt32().ShouldBe(429);
        body.RootElement.GetProperty("title").GetString().ShouldBe("Too many requests");
        body.RootElement.GetProperty("correlationId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// §10.3's <c>authenticated</c> policy partitions on the subject claim, so
    /// one caller spending its whole bucket must not touch another's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing exercised this policy at all until Copilot said so.</b> The
    /// anonymous window was driven to rejection and the authenticated bucket
    /// was not, so the subject partition — the thing that makes a per-user
    /// quota per-user — had no test behind it. What this asserts is the claim
    /// itself: A spends its whole bucket and B, asking once afterwards, is
    /// still served. The discriminator is B's request rather than a rejection,
    /// because <c>QueueLimit</c> is ten rather than zero: on a shared bucket B
    /// would not get a 429 at all, it would be *queued* until the next
    /// replenishment a minute away, so the client carries a timeout and the
    /// assertion is that an answer arrives inside it.
    /// </para>
    /// <para>
    /// <b>It does not catch §4.2's ordering rule, and that was measured rather
    /// than assumed.</b> Moving <c>UseRateLimiter</c> above
    /// <c>UseAuthentication</c> — which §4.2 says degrades the subject key to
    /// the shared address fallback — leaves this test green, and every other
    /// test in this project with it. The anonymous window still rejects
    /// under the reversed pipeline, so the limiter is demonstrably live; why
    /// the authenticated bucket is not shared under it is unexplained here,
    /// and an unexplained pass is not a guard. This is the same shape as
    /// PR-16's finding that deleting <c>app.UseAuthentication()</c> broke no
    /// test: the honest record is the negative result, not a comment claiming
    /// a coverage that does not exist.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_authenticated_policy_gives_each_subject_its_own_bucket()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        // Generous against a loopback 204 and an order of magnitude under the
        // one-minute replenishment a shared bucket would make B wait for.
        client.Timeout = TimeSpan.FromSeconds(15);

        for (int i = 0; i < TokenLimit; i++)
        {
            HttpResponseMessage spent = await Get(client, "subject-a", ct);

            spent.StatusCode.ShouldBe(
                HttpStatusCode.NoContent,
                $"request {i + 1} of {TokenLimit} is inside subject-a's bucket");
        }

        HttpResponseMessage other = await Get(client, "subject-b", ct);

        other.StatusCode.ShouldBe(
            HttpStatusCode.NoContent,
            "subject-b holds its own bucket — a shared one would queue this request until replenishment");
    }

    private static async Task<HttpResponseMessage> Get(HttpClient client, string subject, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, AuthenticatedRoute);
        request.Headers.Add(TestAuthHandler.UserHeader, subject);

        return await client.SendAsync(request, ct);
    }
}
