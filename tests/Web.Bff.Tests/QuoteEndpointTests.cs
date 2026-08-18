using System.Net;
using System.Net.Http.Json;
using Grpc.Core;
using Shouldly;
using Web.Bff.TestSupport;
using Web.Bff.Endpoints;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>
/// The BFF's one screen, driven end to end over a real gRPC server (§9.7).
/// </summary>
public sealed class QuoteEndpointTests : IAsyncLifetime
{
    private static readonly Guid Chair = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Desk = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Unknown = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly StubCatalog _catalog = new();

    private BffFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        await _catalog.InitializeAsync();

        _factory = new BffFactory { PricingAddress = _catalog.Address };
        _catalog.Prices[Chair] = ("Chair", 49.99m);
        _catalog.Prices[Desk] = ("Desk", 120.50m);
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
    public async Task A_quote_prices_every_product_and_totals_them()
    {
        using HttpClient client = Caller();

        QuoteResponse? quote = await client.GetFromJsonAsync<QuoteResponse>(
            $"/v1/checkout/quote?productId={Chair}&productId={Desk}&currency=GBP",
            TestContext.Current.CancellationToken);

        quote.ShouldNotBeNull();
        quote.Currency.ShouldBe("GBP");
        quote.Lines.Count.ShouldBe(2);
        quote.Total.ShouldBe(170.49m);
        quote.Unpriced.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_product_with_no_price_is_named_rather_than_dropped()
    {
        using HttpClient client = Caller();

        QuoteResponse? quote = await client.GetFromJsonAsync<QuoteResponse>(
            $"/v1/checkout/quote?productId={Chair}&productId={Unknown}&currency=GBP",
            TestContext.Current.CancellationToken);

        // The assertion that matters is the second: a form that silently drops
        // a line the customer chose is worse than one that says it cannot
        // price it, and "the total is right" is true of both.
        quote.ShouldNotBeNull();
        quote.Total.ShouldBe(49.99m);
        quote.Unpriced.ShouldBe([Unknown]);
    }

    [Fact]
    public async Task A_currency_Catalog_does_not_price_in_leaves_everything_unpriced()
    {
        using HttpClient client = Caller();

        QuoteResponse? quote = await client.GetFromJsonAsync<QuoteResponse>(
            $"/v1/checkout/quote?productId={Chair}&productId={Desk}&currency=USD",
            TestContext.Current.CancellationToken);

        // Catalog stores one price per product and filters rather than
        // converts (pricing.proto), so this is the honest answer and not an
        // error — the BFF must not invent a conversion.
        quote.ShouldNotBeNull();
        quote.Lines.ShouldBeEmpty();
        quote.Total.ShouldBe(0m);
        quote.Unpriced.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_repeated_product_is_asked_about_once()
    {
        using HttpClient client = Caller();

        QuoteResponse? quote = await client.GetFromJsonAsync<QuoteResponse>(
            $"/v1/checkout/quote?productId={Chair}&productId={Chair}&currency=GBP",
            TestContext.Current.CancellationToken);

        quote.ShouldNotBeNull();
        quote.Lines.Count.ShouldBe(1);

        // Asserted at the wire and not only in the response, because the
        // dedup's real job is upstream: it is what keeps a caller from
        // spending Catalog's id ceiling on one product repeated a hundred
        // times.
        _catalog.Calls.Single().ProductIds.ShouldBe([Chair.ToString()]);
    }

    [Fact]
    public async Task A_quote_with_no_products_is_refused_without_a_hop()
    {
        using HttpClient client = Caller();

        HttpResponseMessage response = await client.GetAsync("/v1/checkout/quote?currency=GBP", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        // The second half is the point: a request that cannot produce anything
        // must not spend the platform's one synchronous hop finding that out.
        _catalog.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_anonymous_caller_is_challenged_and_never_reaches_Catalog()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(
            $"/v1/checkout/quote?productId={Chair}&currency=GBP", TestContext.Current.CancellationToken);

        // §11.2: this host validates its own tokens, whatever the gateway in
        // front of it did. The group fails closed, so the refusal comes from
        // RequireAuthorization rather than from anything the endpoint does.
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // And problem+json, not an empty body — §10.5's promise covers the two
        // statuses a client meets first, which is what UseStatusCodePages is
        // in the pipeline for. Asserting the status alone would pass just as
        // happily against no body at all.
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        _catalog.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_upstream_refusal_is_the_callers_400_rather_than_the_hosts_500()
    {
        _catalog.FailNextWith.Enqueue(StatusCode.InvalidArgument);

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.GetAsync(
            $"/v1/checkout/quote?productId={Chair}&currency=GBP", TestContext.Current.CancellationToken);

        // Catalog refused a request the BFF built out of the caller's query
        // string, so the caller is who has to change something. Without
        // UpstreamExceptionHandler this is a 500, which sends them to read
        // another service's logs for a mistake in their own URL.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task An_upstream_outage_is_503()
    {
        _catalog.FailNextWith.Enqueue(StatusCode.Unavailable);

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.GetAsync(
            $"/v1/checkout/quote?productId={Chair}&currency=GBP", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        // ONE call, and this test was written expecting three. The count is
        // the finding: an HTTP resilience pipeline cannot retry a gRPC status,
        // because a gRPC status travels as an HTTP 200 with grpc-status in the
        // trailers. UpstreamRetryTests is where both halves of that are
        // measured and argued.
        _catalog.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_reply_priced_in_another_currency_stays_a_500()
    {
        _catalog.RawCurrency = "USD";

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.GetAsync(
            $"/v1/checkout/quote?productId={Chair}&currency=GBP", TestContext.Current.CancellationToken);

        // Without this check the endpoint totalled a USD amount and labelled
        // the quote GBP, because the response's currency came from the REQUEST
        // rather than from the price. pricing.proto echoes the currency so each
        // amount is self-describing, and nothing was reading it.
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task A_price_for_a_product_nobody_asked_about_stays_a_500()
    {
        _catalog.Prices[Desk] = ("Desk", 120.50m);
        _catalog.AlsoAnswerWith.Add(Desk);

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.GetAsync(
            $"/v1/checkout/quote?productId={Chair}&currency=GBP", TestContext.Current.CancellationToken);

        // Untrusted, the Desk would have been priced and added to a total the
        // caller never asked for — and Unpriced would not show it, because that
        // is computed from what came back rather than from what was requested.
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task A_product_priced_twice_stays_a_500()
    {
        _catalog.DuplicateEveryPrice = true;

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.GetAsync(
            $"/v1/checkout/quote?productId={Chair}&currency=GBP", TestContext.Current.CancellationToken);

        // The second copy would have been added and totalled, doubling the
        // quote while every id in it was one the caller asked for — the
        // failure mode with no visible symptom but the arithmetic.
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task A_malformed_upstream_amount_stays_a_500()
    {
        _catalog.RawAmount = "12,50";

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.GetAsync(
            $"/v1/checkout/quote?productId={Chair}&currency=GBP", TestContext.Current.CancellationToken);

        // A contract violation between two services is nobody's caller's
        // fault, and answering 400 would tell the client to fix a request that
        // was correct. This is also the assertion that would catch a
        // culture-sensitive parse: "12,50" is a valid decimal under a
        // comma-decimal locale, so a host that dropped InvariantCulture would
        // answer 200 with a hundredfold price.
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }
}
