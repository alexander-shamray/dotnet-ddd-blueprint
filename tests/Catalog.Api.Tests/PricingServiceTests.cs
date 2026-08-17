using System.Net.Http.Json;
using Catalog.Pricing.V1;
using Catalog.TestSupport;
using Grpc.Core;
using Grpc.Net.Client;
using Shouldly;
using Xunit;
using PricingGrpc = Catalog.Pricing.V1.Pricing;

namespace Catalog.Api.Tests;

/// <summary>
/// §9.7's server half, driven over the real pipeline: authentication,
/// authorization, the dispatcher, the validator and Dapper against a real
/// database.
/// </summary>
/// <remarks>
/// <para>
/// <b>Over <c>TestServer</c>, deliberately, and that is not a shortcut.</b>
/// PR-27's rule is to drive <c>TestServer</c> for what the <i>application</i>
/// decides and a real server for what the <i>server</i> decides. Everything
/// asserted here is the first kind. The second kind — that a cleartext
/// endpoint has to be declared <c>Http2</c> before a gRPC client can reach it
/// at all — is measured where it belongs, against a real Kestrel, in
/// <c>Web.Bff.Tests</c>.
/// </para>
/// <para>
/// <c>TestServer.CreateHandler()</c> is what makes this work: it hands the
/// channel a handler that bypasses the network entirely, so the h2c
/// negotiation this host would otherwise need never happens.
/// </para>
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class PricingServiceTests(ServiceFixture fixture) : IAsyncLifetime
{
    private HttpClient _client = null!;
    private GrpcChannel _channel = null!;

    public async ValueTask InitializeAsync()
    {
        _client = fixture.Factory.CreateClient();
        _channel = GrpcChannel.ForAddress(
            fixture.Factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = fixture.Factory.Server.CreateHandler() });

        await fixture.ResetAsync();
    }

    public ValueTask DisposeAsync()
    {
        _channel.Dispose();
        _client.Dispose();

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The principal a validated client-credentials token becomes (§11.3), as
    /// call metadata.
    /// </summary>
    /// <remarks>
    /// Passed per call rather than baked into the channel, for
    /// <c>ProductEndpointsTests</c>' reason one transport over: a default
    /// grant is how a suite ends up proving a policy is applied by never once
    /// arriving without it. The anonymous test below is the one that would
    /// silently stop meaning anything.
    /// </remarks>
    private static Metadata Authenticated() =>
        [new Metadata.Entry(TestAuthHandler.UserHeader, "service-account-web-bff")];

    private PricingGrpc.PricingClient Pricing => new(_channel);

    private async Task<Guid> PublishAsync(string name, decimal amount, string currency)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/v1/catalog/products")
        {
            Content = JsonContent.Create(new
            {
                Name = name,
                ThumbnailUrl = (string?)null,
                Amount = amount,
                Currency = currency
            })
        };

        request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        request.Headers.Add(TestAuthHandler.PermissionsHeader, CatalogPermissions.Write);

        HttpResponseMessage response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task It_prices_the_products_it_is_asked_about()
    {
        Guid chair = await PublishAsync("Chair", 49.99m, "GBP");
        Guid desk = await PublishAsync("Desk", 120.50m, "GBP");

        GetPricesRequest request = new() { Currency = "GBP" };
        request.ProductId.Add(chair.ToString());
        request.ProductId.Add(desk.ToString());

        GetPricesReply reply = await Pricing.GetPricesAsync(
            request,
            Authenticated(),
            cancellationToken: TestContext.Current.CancellationToken);

        reply.Price.Count.ShouldBe(2);

        // The invariant form pricing.proto specifies, asserted as TEXT rather
        // than parsed back: the whole reason the contract carries a string is
        // that the two ends have to agree on the spelling, and a test that
        // parsed it would agree with itself. A host under a comma-decimal
        // culture fails here and nowhere else.
        //
        // "49.9900", not "49.99", and the trailing zeros are the column's
        // scale reaching the wire — PriceAmount is decimal(19,4) (§7.2), and
        // .NET's decimal carries scale through, so ToString emits it. Pinned
        // rather than trimmed, because trimming would be presentation logic in
        // a contract; what it obliges a consumer to do is PARSE the field
        // rather than compare it, which is what the BFF does and what
        // pricing.proto now says out loud.
        reply.Price
            .Single(p => p.ProductId == chair.ToString())
            .Amount
            .ShouldBe("49.9900");
    }

    [Fact]
    public async Task A_product_priced_in_another_currency_is_absent_rather_than_zero()
    {
        Guid chair = await PublishAsync("Chair", 49.99m, "GBP");

        GetPricesRequest request = new() { Currency = "USD" };
        request.ProductId.Add(chair.ToString());

        GetPricesReply reply = await Pricing.GetPricesAsync(
            request,
            Authenticated(),
            cancellationToken: TestContext.Current.CancellationToken);

        // Absent, never zero: a zero-amount entry would be a free product,
        // which is a different fact (pricing.proto).
        reply.Price.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_product_is_simply_absent()
    {
        GetPricesRequest request = new() { Currency = "GBP" };
        request.ProductId.Add(Guid.CreateVersion7().ToString());

        GetPricesReply reply = await Pricing.GetPricesAsync(
            request,
            Authenticated(),
            cancellationToken: TestContext.Current.CancellationToken);

        reply.Price.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        GetPricesRequest request = new() { Currency = "GBP" };

        // No principal: the channel with no interceptor. This is what makes
        // §11.5's client credentials load-bearing rather than ceremonial — if
        // this passed, the BFF's whole token mechanism could be missing and
        // every test would still be green.
        RpcException thrown = await Should.ThrowAsync<RpcException>(
            () => Pricing
                .GetPricesAsync(request, cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        thrown.StatusCode.ShouldBe(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task A_malformed_product_id_is_InvalidArgument()
    {
        GetPricesRequest request = new() { Currency = "GBP" };
        request.ProductId.Add("not-a-guid");

        RpcException thrown = await Should.ThrowAsync<RpcException>(
            () => Pricing
                .GetPricesAsync(
                    request,
                    Authenticated(),
                    cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        thrown.StatusCode.ShouldBe(StatusCode.InvalidArgument);

        // The index, and deliberately not the value: it is a caller-supplied
        // string arriving in a message that reaches the logs, and §13.4's
        // redactor cannot see a value interpolated into one.
        thrown.Status.Detail.ShouldContain("product_id[0]");
        thrown.Status.Detail.ShouldNotContain("not-a-guid");
    }

    [Fact]
    public async Task Too_many_products_is_InvalidArgument_rather_than_Unknown()
    {
        GetPricesRequest request = new() { Currency = "GBP" };

        for (int i = 0; i <= Application.Products.GetPrices.GetPricesValidator.MaxProductIds; i++)
            request.ProductId.Add(Guid.CreateVersion7().ToString());

        RpcException thrown = await Should.ThrowAsync<RpcException>(
            () => Pricing
                .GetPricesAsync(
                    request,
                    Authenticated(),
                    cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        // ValidationInterceptor's whole job, and the status matters more than
        // it looks: untranslated this is Unknown, which the BFF's resilience
        // pipeline would treat as a transient fault and spend all three
        // attempts on a request that was malformed the first time.
        thrown.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        thrown.Status.Detail.ShouldContain("ProductIds");
    }
}
