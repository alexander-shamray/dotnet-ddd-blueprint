using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>
/// §10.1's response compression, and the two halves of ADR-020: that the edge
/// compresses a proxied response at all, and that it does so for an HTTPS
/// caller — which is the property <c>EnableForHttps</c> decides and the only
/// one of the two that a default would answer differently.
/// </summary>
/// <remarks>
/// <para>
/// <b>These run over the proxy rather than over an endpoint of the gateway's
/// own, because the gateway has no response of its own to compress.</b> §10.1
/// gives it no domain and no database, so every body a client receives from it
/// either came from a service or is an error — which is exactly the pair
/// asserted below.
/// </para>
/// <para>
/// The client does not decompress, and that is what makes any of this
/// observable. <c>WebApplicationFactory</c>'s handler performs no automatic
/// decompression, so <c>Content-Encoding</c> survives to be read and the bytes
/// counted are the encoded ones. A handler that decompressed would strip the
/// header and expand the payload, which does not weaken these assertions — it
/// makes them unwritable, and a suite over such a client could say nothing
/// stronger than "a body arrived".
/// </para>
/// </remarks>
public sealed class CompressedResponseTests(StubDestination stub) : IClassFixture<StubDestination>
{
    /// <summary>
    /// Large enough that compression cannot fail to help, and small enough
    /// that a hundred of them cost nothing.
    /// </summary>
    private const int BodyBytes = 8192;

    private static readonly string CompressibleRoute =
        $"/api/v1/catalog/products?{StubDestination.BodySizeQuery}={BodyBytes}";

    /// <summary>
    /// The plain proxied case: a service's <c>application/json</c> reaches the
    /// client encoded, and comes back out as what the service sent.
    /// </summary>
    /// <remarks>
    /// Registration without middleware is the quiet failure mode here, the
    /// same shape §10.3's limiter has: <c>AddResponseCompression</c> succeeds
    /// and compresses nothing at all if <c>UseResponseCompression</c> is
    /// missing from the pipeline. Deleting that line is what this test is
    /// watching for, and it was observed red against exactly that deletion.
    /// </remarks>
    [Fact]
    public async Task A_proxied_json_response_is_compressed_and_round_trips()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, CompressibleRoute);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        HttpResponseMessage response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.ShouldBe(["gzip"]);

        // Without this a shared cache in front of the gateway may serve the
        // encoded body to a client that never asked for one.
        response.Headers.Vary.ShouldContain("Accept-Encoding");

        byte[] encoded = await response.Content.ReadAsByteArrayAsync(ct);

        encoded.Length.ShouldBeLessThan(
            BodyBytes,
            "the response is encoded, so the bytes on the wire are the compressed ones");

        // The whole body, not merely a smaller one: a truncating pipeline would
        // satisfy every assertion above and none of this.
        Decompress(encoded).ShouldBe(new string('a', BodyBytes));
    }

    /// <summary>
    /// The same route over HTTPS. <c>EnableForHttps</c> is what this asserts,
    /// and it is the one line in the registration a framework default would
    /// answer the other way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The scheme is the whole test.</b> Left at its default the middleware
    /// declines to compress an HTTPS response, so this goes red while the test
    /// above stays green — which is what makes ADR-020's decision observable
    /// rather than a comment. Observed red against the property removed.
    /// </para>
    /// <para>
    /// <c>TestServer</c> takes the scheme from the client's base address and
    /// terminates no TLS, so this is an assertion about
    /// <c>HttpContext.Request.IsHttps</c> and not about a handshake — which is
    /// the property the middleware reads.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_https_response_is_compressed_too()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using StubbedGatewayFactory factory = new(stub.Address);
        factory.ClientOptions.BaseAddress = new Uri("https://localhost");

        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, CompressibleRoute);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        HttpResponseMessage response = await client.SendAsync(request, ct);

        response.RequestMessage!.RequestUri!.Scheme.ShouldBe(
            Uri.UriSchemeHttps,
            "otherwise this is the previous test again under another name");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.ShouldBe(["gzip"]);
    }

    /// <summary>
    /// §10.5's error shape is not compressed, and that is a property of the
    /// framework's default MIME list rather than of anything this solution
    /// writes — so it is asserted here, from the wire.
    /// </summary>
    /// <remarks>
    /// Two reasons, and ADR-020 carries both. An error body is a couple of
    /// hundred bytes, which is the size range where compression makes a
    /// response bigger rather than smaller; and it is the one body on the
    /// platform that reflects a client-supplied value — §10.4's correlation ID
    /// — back to the caller, which is the input half of the BREACH shape. The
    /// 401 is used because it needs no budget and no token: the ordering route
    /// carries §11.4's authenticated policy, so an anonymous GET is refused
    /// above the proxy.
    /// </remarks>
    [Fact]
    public async Task A_problem_json_error_is_not_compressed()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/orders/018f4c2e");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));

        HttpResponseMessage response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        response.Content.Headers.ContentEncoding.ShouldBeEmpty();

        // Readable as it stands, which is the point of the assertion above.
        (await response.Content.ReadAsStringAsync(ct)).ShouldContain("\"status\":401");
    }

    /// <summary>
    /// A destination that compressed for itself is passed through, not encoded
    /// a second time.
    /// </summary>
    /// <remarks>
    /// The middleware declines any response that already carries a
    /// <c>Content-Encoding</c>. Here that is the double-compression guard; the
    /// test below is the same rule used as an opt-out.
    /// </remarks>
    [Fact]
    public async Task An_already_encoded_response_is_passed_through_once()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        HttpResponseMessage response = await Get(Declaring("gzip"), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.ShouldBe(
            ["gzip"],
            "one encoding — a second would be the destination's gzip inside the gateway's");

        // One decompression yields the body. Two encodings would leave this
        // holding compressed bytes rather than the text.
        Decompress(await response.Content.ReadAsByteArrayAsync(ct)).ShouldBe(new string('a', BodyBytes));
    }

    /// <summary>
    /// A destination declaring its body unencoded is left uncompressed —
    /// ADR-020's opt-out, and the only one there is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The first version of this ADR named the wrong mechanism, and Copilot
    /// caught it.</b> It told PR-19's BFF to protect a secret-bearing response
    /// by encoding it itself, which mitigates nothing: gzip opens the same
    /// length side channel wherever it is applied, and the pass-through test
    /// above only proves the gateway declines to encode a second time. What is
    /// needed is a body that is never compressed at all, and the middleware's
    /// header check supplies one — <c>Content-Encoding: identity</c> means
    /// "no transformation applied", so a response carrying it is skipped and
    /// reaches the client in the clear.
    /// </para>
    /// <para>
    /// Asserted rather than derived from the documentation, because it is now
    /// the whole of what ADR-020 offers a downstream and a decision resting on
    /// an unverified mechanism rests on nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_response_declaring_identity_is_not_compressed()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        HttpResponseMessage response = await Get(Declaring("identity"), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.ShouldBe(
            ["identity"],
            "the declaration survives the hop — a gzip here would mean the opt-out is not one");

        // Readable without decompressing, which is the property the BFF needs.
        (await response.Content.ReadAsStringAsync(ct)).ShouldBe(new string('a', BodyBytes));
    }

    /// <summary>
    /// <c>Cache-Control: no-transform</c> stops compression — ADR-020's
    /// opt-out, and the platform's conformance with RFC 9111 §5.2.2.6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The framework does not do this; <see cref="Gateway.Api"/>'s own
    /// provider does.</b> Measured before that provider existed: an 8 KiB body
    /// sent under the directive came back gzipped at 115 bytes with the
    /// directive intact. The directive "indicates that an intermediary
    /// (regardless of whether it implements a cache) MUST NOT transform the
    /// content", quoted verbatim, and applying a content coding is such a
    /// transformation (RFC 9110 §7.7) — so a YARP gateway compressing past it
    /// is not making a policy choice, it is violating the specification.
    /// </para>
    /// <para>
    /// <b>Which is why this is the opt-out ADR-020 hands PR-19</b>, in place
    /// of <c>Content-Encoding: identity</c>. That one works only as a side
    /// effect of the double-compression guard — a refusal reached by looking
    /// like an already-encoded response — and puts a content coding on the
    /// wire for no reason of the client's. <c>no-transform</c> travels: the
    /// ingress, the CDN and every cache on the path read it, where a content
    /// coding speaks only to whatever reads the response next.
    /// </para>
    /// <para>
    /// This test is red against the provider's registration removed, which is
    /// the only thing standing between the platform and the measured
    /// violation.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_no_transform_directive_stops_compression()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        HttpResponseMessage response = await Get(
            $"{CompressibleRoute}&{StubDestination.NoTransformQuery}=1",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoTransform.ShouldBe(
            true,
            "the directive survived the hop, so this is a statement about the middleware and not about YARP");

        response.Content.Headers.ContentEncoding.ShouldBeEmpty(
            "RFC 9111 forbids an intermediary transforming this content, and the gateway is one");

        // Readable as it stands, which is what a downstream sending the
        // directive is asking for.
        (await response.Content.ReadAsStringAsync(ct)).ShouldBe(new string('a', BodyBytes));
    }

    private static string Declaring(string encoding) =>
        $"{CompressibleRoute}&{StubDestination.ContentEncodingQuery}={encoding}";

    private async Task<HttpResponseMessage> Get(string route, CancellationToken ct)
    {
        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, route);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        return await client.SendAsync(request, ct);
    }

    private static string Decompress(byte[] encoded)
    {
        using MemoryStream source = new(encoded);
        using GZipStream decompressor = new(source, CompressionMode.Decompress);
        using StreamReader reader = new(decompressor, Encoding.UTF8);

        return reader.ReadToEnd();
    }
}
