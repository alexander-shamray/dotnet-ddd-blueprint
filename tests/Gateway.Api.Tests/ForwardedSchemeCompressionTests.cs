using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>
/// The scheme the compression middleware actually reads behind an HTTPS
/// ingress, which is not the scheme of the hop the gateway serves.
/// </summary>
/// <remarks>
/// <para>
/// <b>This suite exists because ADR-020's first argument was backwards, and
/// Copilot caught it.</b> That argument ran: TLS terminates at the ingress
/// (§10.1), so the gateway is served plain HTTP, so <c>EnableForHttps</c>
/// never fires and setting it true merely says out loud what would happen
/// anyway. Every clause is true except the conclusion. §4.2's forwarded-headers
/// block enables <c>XForwardedProto</c>, and <c>UseForwardedHeaders</c>
/// rewrites <c>Request.Scheme</c> from the ingress's header — while the
/// compression middleware takes its decision at the first <b>write</b>, below
/// the whole pipeline, so the scheme it reads is the rewritten one.
/// </para>
/// <para>
/// So the flag is load-bearing in the opposite direction to the one first
/// written down: left at its default, a gateway behind an HTTPS ingress
/// compresses <b>nothing</b>, and nothing in the response says why. The
/// decision is unchanged and its reason is not, which is the whole content of
/// this suite.
/// </para>
/// <para>
/// The factory is <c>ForwardedHeadersTests</c>' shape for its reasons: a
/// trusted loopback network and a startup filter giving the request a peer
/// address, without which the middleware honours no forwarded header at all.
/// </para>
/// </remarks>
public sealed class ForwardedSchemeCompressionTests(StubDestination stub) : IClassFixture<StubDestination>
{
    private const int BodyBytes = 8192;

    /// <summary>From the documentation range (RFC 5737), as §4.2's suite uses.</summary>
    private const string ForwardedClient = "203.0.113.9";

    private static readonly string CompressibleRoute =
        $"/api/v1/catalog/products?{StubDestination.BodySizeQuery}={BodyBytes}";

    /// <summary>
    /// A request arriving over plain HTTP but forwarded as HTTPS is still
    /// compressed — which is only true because <c>EnableForHttps</c> is set.
    /// </summary>
    /// <remarks>
    /// Observed red against that property removed, and it is the *only* test
    /// in the project that goes red there for the deployed topology's reason:
    /// <c>An_https_response_is_compressed_too</c> reaches the same branch
    /// through a client base address, which proves the middleware reads
    /// <c>IsHttps</c> and says nothing about how this platform's requests come
    /// to have it set.
    /// </remarks>
    [Fact]
    public async Task A_request_forwarded_as_https_is_still_compressed()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using BehindHttpsIngressFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        client.BaseAddress.ShouldNotBeNull();
        client.BaseAddress!.Scheme.ShouldBe(
            Uri.UriSchemeHttp,
            "the hop itself is plain — the ingress terminated TLS, which is the topology §10.1 describes");

        using HttpRequestMessage request = new(HttpMethod.Get, CompressibleRoute);
        request.Headers.Add("X-Forwarded-For", ForwardedClient);
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        HttpResponseMessage response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.ShouldBe(
            ["gzip"],
            "UseForwardedHeaders made Request.IsHttps true before the compression decision was taken, so " +
            "the framework default would have refused this response and the edge would compress nothing");
    }

    /// <summary>
    /// The gateway as it runs in Kubernetes (§15.3), with the ingress
    /// forwarding both the client address and the scheme it terminated.
    /// </summary>
    /// <remarks>
    /// Not derived from <c>ForwardedHeadersTests</c>' own factory: that one is
    /// private to its suite, and §4.1 gives the gateway no TestSupport library
    /// to share one through — one suite was the reason, and two classes needing
    /// the same four lines is not yet a second. Stated rather than left to look
    /// like an oversight.
    /// </remarks>
    private sealed class BehindHttpsIngressFactory(string destination) : StubbedGatewayFactory(destination)
    {
        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
        [
            .. base.AdditionalSettings,
            new("Ingress:Enabled", "true"),
            new("Ingress:TrustedNetworks:0", "127.0.0.1/32")
        ];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter, LoopbackPeerStartupFilter>());
        }
    }

    /// <summary>
    /// Gives the request a peer address, which <c>TestServer</c> otherwise
    /// leaves null and which <c>UseForwardedHeaders</c> must match against
    /// <c>KnownIPNetworks</c> before it honours anything.
    /// </summary>
    private sealed class LoopbackPeerStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, following) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Loopback;

                    await following();
                });

                next(app);
            };
    }
}
