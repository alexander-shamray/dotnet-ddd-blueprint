using System.Net;
using Grpc.Core;
using Shouldly;
using Web.Bff.TestSupport;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>
/// What §9.7's retry actually covers on a gRPC client, measured from both
/// sides — because the answer is not the one the chapter's configuration
/// implies, and the difference decides how a Catalog outage presents.
/// </summary>
/// <remarks>
/// <para>
/// <b>An HTTP resilience pipeline cannot see a gRPC status.</b> gRPC carries
/// its outcome in <c>grpc-status</c>, a trailer on an HTTP <b>200</b> —
/// or a header on a trailers-only response, still a 200 — so
/// <c>AddStandardResilienceHandler</c>, which decides on the HTTP status line
/// and on <c>HttpRequestException</c>, sees a successful response and passes
/// it straight back. A server that answers <c>Unavailable</c> is therefore
/// asked exactly once, whatever <c>MaxRetryAttempts</c> says.
/// </para>
/// <para>
/// <b>What it does retry is a transport fault</b> — a refused connection, a
/// reset, a DNS failure, a 502 from an intermediary — which is the shape a
/// service that is genuinely down produces. So the configuration is not inert;
/// it covers the outage case and not the deliberate-refusal case.
/// </para>
/// <para>
/// <b>The fix is deliberately NOT a second retry loop.</b> gRPC has its own
/// retry, configured on the channel through <c>ServiceConfig</c>, and it does
/// understand status codes — but it sits <i>outside</i> the
/// <c>HttpClient</c>, so each of its attempts would get a fresh
/// <c>TotalRequestTimeout</c> and three of them would spend fifteen seconds
/// against a five-second ceiling. §9.7's whole point is that the budgets
/// nest, and stacking two retry mechanisms is the one change that breaks the
/// hierarchy the chapter exists to protect. One mechanism, documented limits.
/// </para>
/// </remarks>
public sealed class UpstreamRetryTests : IAsyncLifetime
{
    private static readonly Guid Chair = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly StubCatalog _catalog = new();

    private BffFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        await _catalog.InitializeAsync();

        _factory = new BffFactory { PricingAddress = _catalog.Address };
        _catalog.Prices[Chair] = ("Chair", 49.99m);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _catalog.DisposeAsync();
    }

    private HttpClient Caller()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "customer-1");

        return client;
    }

    [Fact]
    public async Task A_transport_fault_is_retried_and_the_request_recovers()
    {
        // Two aborts, one good answer: inside the three attempts §9.7
        // configures, so the caller never learns anything went wrong.
        _catalog.AbortNextCalls = 2;

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.GetAsync(
            $"/v1/checkout/quote?productId={Chair}&currency=GBP", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _catalog.Calls.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_transport_fault_past_the_budget_exhausts_the_attempts()
    {
        // One more than the budget allows, so the retries are spent and the
        // request fails — which is what pins the attempt COUNT rather than
        // just "it retries". A configuration of five attempts would pass the
        // test above and fail this one.
        _catalog.AbortNextCalls = 4;

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.GetAsync(
            $"/v1/checkout/quote?productId={Chair}&currency=GBP", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        _catalog.Calls.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_grpc_status_is_answered_once_and_never_retried()
    {
        // Queue four refusals. If the pipeline retried gRPC statuses, three of
        // them would be consumed; it is asked once.
        for (int i = 0; i < 4; i++)
            _catalog.FailNextWith.Enqueue(StatusCode.Unavailable);

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.GetAsync(
            $"/v1/checkout/quote?productId={Chair}&currency=GBP", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        // The measurement this whole file exists for. Written the other way
        // round first — expecting three — and the one that arrived is what
        // sent us to read how gRPC reports a status.
        _catalog.Calls.Count.ShouldBe(1);
    }
}
