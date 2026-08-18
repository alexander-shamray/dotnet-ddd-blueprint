using System.Net.Http.Json;
using Shouldly;
using Web.Bff.TestSupport;
using Web.Bff.Endpoints;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>
/// §11.5's outbound identity, observed at the wire: that a token is attached
/// at all, that it is the configured scope's, and — the claim §9.7 and §11.5
/// both make and neither could check — that a retried attempt carries a
/// <b>fresh</b> one.
/// </summary>
public sealed class PricingCredentialsTests : IAsyncLifetime
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
    public async Task Every_outbound_call_carries_a_bearer_token()
    {
        using HttpClient client = Caller();

        await client.GetFromJsonAsync<QuoteResponse>(
            $"/v1/checkout/quote?productId={Chair}&currency=GBP",
            TestContext.Current.CancellationToken);

        // Read off the request Catalog actually received, not off the handler.
        // A DelegatingHandler registered and never reached is the failure this
        // is for, and it is invisible from inside the process.
        _catalog.Calls.Single().Authorization.ShouldBe("Bearer token-1");
    }

    [Fact]
    public async Task The_token_is_minted_for_the_configured_scope()
    {
        using HttpClient client = Caller();

        await client.GetFromJsonAsync<QuoteResponse>(
            $"/v1/checkout/quote?productId={Chair}&currency=GBP",
            TestContext.Current.CancellationToken);

        // §11.5: the scope has to become an audience, and a client-credentials
        // token that asked for the wrong scope carries the wrong `aud` and is
        // refused by every service — at the one moment there is no user to
        // blame it on.
        _factory.Tokens.Scopes.ShouldBe([BffFactory.Scope]);
    }

    [Fact]
    public async Task A_retried_attempt_carries_a_fresh_token()
    {
        // The first attempt fails at the TRANSPORT, not with a gRPC status,
        // and that distinction is load-bearing rather than incidental: a gRPC
        // status rides an HTTP 200 with grpc-status in the trailers, so the
        // resilience pipeline sees a successful response and retries nothing.
        // An aborted connection is an HttpRequestException, which it does
        // retry — see UpstreamRetryTests, which is where that pair is measured.
        //
        // With the retry established, this is the only case in which the two
        // orderings produce different bytes — and therefore the only thing
        // that can measure the claim at all. §11.5 used to justify the
        // position with "a retry after a 401", which its own configuration
        // rules out; what is left, and what this drives, is that a repeated
        // attempt carries a freshly fetched token.
        _catalog.AbortNextCalls = 1;

        using HttpClient client = Caller();

        QuoteResponse? quote = await client.GetFromJsonAsync<QuoteResponse>(
            $"/v1/checkout/quote?productId={Chair}&currency=GBP",
            TestContext.Current.CancellationToken);

        quote.ShouldNotBeNull();

        string?[] presented = [.. _catalog.Calls.Select(call => call.Authorization)];

        // Two attempts, two DIFFERENT tokens. Registered the other way round —
        // the credential handler OUTSIDE the resilience pipeline — the handler
        // runs once per logical request rather than once per attempt, so both
        // lines here would read "Bearer token-1" and a token that expired
        // between them could never be replaced.
        //
        // This is what makes the registration order in Program.cs a claim a
        // test is making rather than a comment nobody can check.
        presented.ShouldBe(["Bearer token-1", "Bearer token-2"]);
    }

    [Fact]
    public async Task Two_requests_reuse_the_cache_rather_than_the_token()
    {
        using HttpClient client = Caller();

        await client.GetFromJsonAsync<QuoteResponse>(
            $"/v1/checkout/quote?productId={Chair}&currency=GBP",
            TestContext.Current.CancellationToken);
        await client.GetFromJsonAsync<QuoteResponse>(
            $"/v1/checkout/quote?productId={Chair}&currency=GBP",
            TestContext.Current.CancellationToken);

        // The handler asks the cache on every attempt, deliberately — that is
        // what the test above depends on. Which means the *caching* is
        // entirely ITokenCache's job, and this test says so out loud: two
        // requests here are two asks, and it is CachingTokenClientTests that
        // proves an ask is not a fetch.
        _factory.Tokens.Issued.ShouldBe(2);
    }
}
