using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Catalog.Pricing.V1;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Web.Bff.TestSupport;

/// <summary>
/// A real gRPC server on an ephemeral loopback port, standing in for Catalog.
/// </summary>
/// <remarks>
/// <para>
/// <b>A real server rather than a substituted client, and the reason is the
/// one PR-27 wrote down.</b> Drive <c>TestServer</c> for what the
/// <i>application</i> decides and a real server for what the <i>server</i>
/// decides — and everything interesting about §9.7's hop is the second kind:
/// the h2c negotiation, the <c>Authorization</c> header that survives to the
/// wire, the retry that opens a second request. Substituting
/// <c>Pricing.PricingClient</c> would assert the endpoint's mapping and prove
/// nothing about the pipeline the whole PR is for.
/// </para>
/// <para>
/// <b>Http2 explicitly, because a cleartext endpoint at the default refuses
/// HTTP/2.</b> Measured on the way in to this PR: a client asking for HTTP/2
/// exactly, as gRPC's does, is answered <c>HTTP_1_1_REQUIRED</c> and the
/// connection is closed. It is the same finding that gave Catalog a second
/// Kestrel endpoint, and this line is where the suite would fail first if
/// anyone reverted it.
/// </para>
/// </remarks>
public sealed class StubCatalog : IAsyncLifetime
{
    private readonly ConcurrentQueue<Call> _calls = new();
    private readonly ConcurrentQueue<GetPricesReply> _replies = new();

    private WebApplication? _app;

    /// <summary>The address the BFF's gRPC client is pointed at.</summary>
    public Uri Address { get; private set; } = null!;

    /// <summary>Every call this stub has answered, in order.</summary>
    public IReadOnlyCollection<Call> Calls => _calls;

    /// <summary>
    /// Every reply this stub has actually sent, in order.
    /// </summary>
    /// <remarks>
    /// <b>A second queue rather than a member of <see cref="Call"/>, because a
    /// call is recorded before it is answered.</b> That order is what lets
    /// <c>UpstreamRetryTests</c> count attempts that failed, aborted or hung —
    /// none of which produces a reply — so a reply field on <c>Call</c> would be
    /// null for exactly the calls that suite is about. What this queue is for is
    /// the other direction: <c>PricingContract.Verify</c> applied to what the
    /// stub sent, which is what establishes that the consumer's suite is driven
    /// by a Catalog the real one could be.
    /// </remarks>
    public IReadOnlyCollection<GetPricesReply> Replies => _replies;

    /// <summary>
    /// Prices this stub knows, by product id. Set by a test before it drives
    /// the endpoint, or by <see cref="Publish"/> from a contract interaction.
    /// </summary>
    /// <remarks>
    /// <b>The currency is per product, because Catalog's is.</b> This stub used
    /// to carry one currency for the whole catalogue, which made the two
    /// interesting cases inexpressible: a basket holding one product priced in
    /// the requested currency and one priced in another is the shape that puts
    /// something in <c>QuoteResponse.Unpriced</c> while still totalling the
    /// rest, and a single-currency stub answers such a request either wholly or
    /// not at all. Catalog stores one price per product and filters rather than
    /// converts (<c>pricing.proto</c>), so the row is where the currency lives.
    /// </remarks>
    public ConcurrentDictionary<Guid, (string Name, decimal Amount, string Currency)> Prices { get; } = new();

    /// <summary>
    /// Statuses to fail the next calls with, one per call, before answering
    /// normally.
    /// </summary>
    /// <remarks>
    /// A queue rather than a flag, because the tests that need it are about
    /// the <i>sequence</i> — attempt one fails, attempt two succeeds — and a
    /// boolean would make "fails once" and "fails always" the same setting.
    /// </remarks>
    public ConcurrentQueue<StatusCode> FailNextWith { get; } = new();

    /// <summary>
    /// Raw amount strings to answer with instead of formatting
    /// <see cref="Prices"/>, so a malformed reply can be driven through the
    /// endpoint's parse.
    /// </summary>
    public string? RawAmount { get; set; }

    /// <summary>
    /// A currency to answer with instead of the one the request asked for, so
    /// a reply that contradicts its own request can be driven through the
    /// endpoint.
    /// </summary>
    public string? RawCurrency { get; set; }

    /// <summary>
    /// Product ids to price in every reply whether or not the request named
    /// them — an upstream answering a question it was not asked.
    /// </summary>
    public List<Guid> AlsoAnswerWith { get; } = [];

    /// <summary>Answer every price twice, which the contract forbids.</summary>
    public bool DuplicateEveryPrice { get; set; }

    /// <summary>
    /// How many of the next calls to answer by aborting the connection instead
    /// of replying.
    /// </summary>
    /// <remarks>
    /// <b>A transport fault, which is a different thing from
    /// <see cref="FailNextWith"/>, and the difference is the whole reason both
    /// exist.</b> A gRPC status travels as an HTTP 200 with <c>grpc-status</c>
    /// in the trailers, so an HTTP-level resilience pipeline sees a successful
    /// response and does not retry it. An aborted connection is an
    /// <c>HttpRequestException</c>, which it does. Only this one exercises
    /// §9.7's retry.
    /// </remarks>
    public int AbortNextCalls { get; set; }

    /// <summary>
    /// How long the next calls should hang before answering, so the resilience
    /// pipeline's own timeout fires.
    /// </summary>
    /// <remarks>
    /// A third failure mode rather than a variant of the two above, and the
    /// three are distinguished by what the CLIENT ends up throwing: a gRPC
    /// status, an <c>HttpRequestException</c>, and Polly's
    /// <c>TimeoutRejectedException</c>. All three reach
    /// <c>UpstreamExceptionHandler</c> differently, and it mapped only the
    /// first until they were measured.
    /// </remarks>
    public TimeSpan HangFor { get; set; }

    public async ValueTask InitializeAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        // Listen rather than ListenLocalhost: the localhost overload refuses
        // port 0 outright — "dynamic port binding is not supported when binding
        // to localhost", because it opens two sockets and could not give them
        // the same OS-assigned port. One loopback address, one port, knowable
        // after Start.
        builder.WebHost.ConfigureKestrel(o =>
            o.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));

        builder.Services.AddGrpc();
        builder.Services.AddSingleton(this);

        _app = builder.Build();
        _app.MapGrpcService<StubPricingService>();

        await _app.StartAsync();

        // The port is only knowable after Start — 0 asks the OS to pick one.
        string url = _app.Urls.Single();
        Address = new Uri(url);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }

    /// <summary>
    /// Realises an interaction's <c>Given</c> state, and answers the id each
    /// product was published under.
    /// </summary>
    /// <remarks>
    /// The consumer's half of the same job <c>PricingContractVerificationTests</c>
    /// does against the real provider by posting to
    /// <c>/v1/catalog/products</c>. Neither side can be handed ids by the
    /// contract, because both mint their own — which is why an interaction names
    /// its products by alias and both sides answer with a binding.
    /// </remarks>
    public IReadOnlyDictionary<string, Guid> Publish(PricingInteraction interaction)
    {
        Dictionary<string, Guid> published = [];

        foreach (ContractProduct product in interaction.Given)
        {
            // Version 7, as Catalog mints them — which is also what keeps these
            // clear of PricingContract.UnknownId's all-but-zero shape.
            Guid id = Guid.CreateVersion7();

            Prices[id] = (product.Name, product.Amount, product.Currency);
            published.Add(product.Alias, id);
        }

        return published;
    }

    /// <summary>One answered call: what was asked, and what was presented.</summary>
    /// <param name="ProductIds">The ids the request named, verbatim.</param>
    /// <param name="Currency">The currency the request named.</param>
    /// <param name="Authorization">
    /// The <c>Authorization</c> header as it arrived — the only place the
    /// token the credential handler attached is observable, which is what makes
    /// the per-attempt assertion possible at all.
    /// </param>
    /// <param name="CorrelationId">
    /// The <c>X-Correlation-Id</c> header as it arrived, or <c>null</c> if the
    /// hop carried none. §10.4's promise is only checkable from the receiving
    /// end: everything on the sending side would pass just as well against a
    /// handler that set the header on a message nobody sent.
    /// </param>
    public sealed record Call(
        IReadOnlyList<string> ProductIds,
        string Currency,
        string? Authorization,
        string? CorrelationId);

    private sealed class StubPricingService(StubCatalog stub) : Pricing.PricingBase
    {
        public override async Task<GetPricesReply> GetPrices(
            GetPricesRequest request,
            ServerCallContext context)
        {
            stub._calls.Enqueue(new Call(
                [.. request.ProductId],
                request.Currency,
                context.RequestHeaders.GetValue("authorization"),
                // Lower-cased: HTTP/2 header names are lower-case on the wire,
                // and Grpc.Core's Metadata is an ordinal list rather than a
                // case-insensitive lookup — asking for "X-Correlation-Id"
                // returns null however faithfully the client sent it.
                context.RequestHeaders.GetValue("x-correlation-id")));

            if (stub.HangFor > TimeSpan.Zero)
                await Task.Delay(stub.HangFor, context.CancellationToken);

            if (stub.AbortNextCalls > 0)
            {
                stub.AbortNextCalls--;
                context.GetHttpContext().Abort();

                throw new RpcException(new Status(StatusCode.Aborted, "connection aborted"));
            }

            if (stub.FailNextWith.TryDequeue(out StatusCode failure))
                throw new RpcException(new Status(failure, "stubbed failure"));

            // Everything below this line is Catalog's OWN behaviour rather than
            // a test artifice, and PricingContract is what says so: a divergence
            // here is a suite proving the BFF works against a Catalog that does
            // not exist.

            // GetPricesValidator's ceiling, which the stub did not have until
            // the contract named it. CheckoutEndpoints deliberately holds no
            // copy of the number and relies on this refusal reaching the caller
            // as a 400 — a stub that served a hundred and one products was
            // proving that reliance was safe by never testing it.
            if (request.ProductId.Count > PricingContract.MaxProductIds)
            {
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument,
                    $"A request may name at most {PricingContract.MaxProductIds} products."));
            }

            GetPricesReply reply = new();

            List<string> answering = [.. request.ProductId, .. stub.AlsoAnswerWith.Select(id => id.ToString())];

            if (stub.DuplicateEveryPrice)
                answering = [.. answering, .. answering];

            foreach (string id in answering)
            {
                if (!Guid.TryParse(id, out Guid productId) ||
                    !stub.Prices.TryGetValue(
                        productId,
                        out (string Name, decimal Amount, string Currency) price))
                {
                    continue;
                }

                // OrdinalIgnoreCase, because Catalog's is: GetPricesValidator
                // accepts [A-Za-z]{3} and Money.Of upper-cases on the way in, so
                // "gbp" prices the same products "GBP" does. Ordinal here made
                // the stub STRICTER than the provider — the safe direction for a
                // false pass, and still a model of a service that does not
                // exist.
                if (!string.Equals(price.Currency, request.Currency, StringComparison.OrdinalIgnoreCase))
                    continue;

                reply.Price.Add(new ProductPrice
                {
                    ProductId = id,
                    Name = price.Name,
                    // "F4", not the default: Catalog's PriceAmount column is
                    // decimal(19,4) (§7.2) and .NET's decimal carries scale
                    // through, so 49.99 leaves the real provider as "49.9900".
                    // Formatting at the test's own scale made the stub the one
                    // producer in the platform whose amounts a consumer could
                    // safely string-compare — which is exactly what
                    // pricing.proto tells a consumer never to do.
                    Amount = stub.RawAmount ?? price.Amount.ToString("F4", CultureInfo.InvariantCulture),
                    // The STORED spelling, not the request's. Catalog projects
                    // its own column, so a "gbp" request is answered "GBP";
                    // echoing the request made every reply agree with its own
                    // question by construction, and CheckoutEndpoints'
                    // OrdinalIgnoreCase comparison was never once given two
                    // spellings to reconcile.
                    Currency = stub.RawCurrency ?? price.Currency
                });
            }

            stub._replies.Enqueue(reply);

            return reply;
        }
    }
}
