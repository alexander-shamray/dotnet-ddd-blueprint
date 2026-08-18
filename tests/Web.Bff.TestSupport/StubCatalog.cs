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

    private WebApplication? _app;

    /// <summary>The address the BFF's gRPC client is pointed at.</summary>
    public Uri Address { get; private set; } = null!;

    /// <summary>Every call this stub has answered, in order.</summary>
    public IReadOnlyCollection<Call> Calls => _calls;

    /// <summary>
    /// Prices this stub knows, by product id. Set by a test before it drives
    /// the endpoint.
    /// </summary>
    public ConcurrentDictionary<Guid, (string Name, decimal Amount)> Prices { get; } = new();

    /// <summary>
    /// The currency this stub will price in. A product is answered only when
    /// the request's currency matches, which is Catalog's own semantics — one
    /// price per product, filtered rather than converted.
    /// </summary>
    public string Currency { get; set; } = "GBP";

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

    /// <summary>One answered call: what was asked, and what was presented.</summary>
    /// <param name="ProductIds">The ids the request named, verbatim.</param>
    /// <param name="Currency">The currency the request named.</param>
    /// <param name="Authorization">
    /// The <c>Authorization</c> header as it arrived — the only place the
    /// token the credential handler attached is observable, which is what makes
    /// the retry-refreshes-the-token assertion possible at all.
    /// </param>
    public sealed record Call(IReadOnlyList<string> ProductIds, string Currency, string? Authorization);

    private sealed class StubPricingService(StubCatalog stub) : Pricing.PricingBase
    {
        public override async Task<GetPricesReply> GetPrices(
            GetPricesRequest request,
            ServerCallContext context)
        {
            stub._calls.Enqueue(new Call(
                [.. request.ProductId],
                request.Currency,
                context.RequestHeaders.GetValue("authorization")));

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

            GetPricesReply reply = new();

            if (!string.Equals(request.Currency, stub.Currency, StringComparison.Ordinal))
                return reply;

            foreach (string id in request.ProductId)
            {
                if (!Guid.TryParse(id, out Guid productId) ||
                    !stub.Prices.TryGetValue(productId, out (string Name, decimal Amount) price))
                {
                    continue;
                }

                reply.Price.Add(new ProductPrice
                {
                    ProductId = id,
                    Name = price.Name,
                    Amount = stub.RawAmount ?? price.Amount.ToString(CultureInfo.InvariantCulture),
                    Currency = stub.RawCurrency ?? request.Currency
                });
            }

            return reply;
        }
    }
}
