using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>
/// §10.1's request size limit, over a real Kestrel.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the one suite in the project that cannot run on
/// <c>TestServer</c>, and the reason is the thing being tested.</b> The limit
/// is a Kestrel option, and <c>TestServer</c> is not Kestrel — it implements
/// none of the body-size features, so <c>ConfigureKestrel</c> is a no-op under
/// it and the ceiling simply does not exist. <c>UseKestrel(0)</c> takes an
/// ephemeral loopback port for the same reason <see cref="StubDestination"/>
/// does, so parallel classes never collide.
/// </para>
/// <para>
/// <b>Run over <c>TestServer</c> this suite goes red, not green, and the
/// difference is the both-sides assertion.</b> Measured after a Copilot review
/// said so: two of the three fail — the oversized bodies reach the stub and
/// come back 204 where 413 was expected — and exactly one passes, the one
/// asserting that a body <i>at</i> the ceiling is forwarded. So the silent
/// outcome the seam is worth guarding against is a suite that tests only the
/// acceptance: that suite passes on a gateway with no limit at all and proves
/// nothing. Asserting the refusal is what turns a silent no-op into a loud
/// one.
/// </para>
/// <para>
/// Both cases carry a token. The ceiling is enforced where the body is read,
/// which at the edge is inside the forwarder — below authentication and
/// authorization, neither of which touches the body — so an anonymous
/// oversized request is answered 401 and its size is never considered.
/// Measured rather than reasoned: without the header this suite asserts the
/// challenge and learns nothing about the limit.
/// </para>
/// </remarks>
public sealed class RequestSizeLimitTests(StubDestination stub) : IClassFixture<StubDestination>
{
    /// <summary>
    /// One of the three §10.2 routes that accept a body — only
    /// <c>catalog-public</c> restricts its methods — and the one reachable
    /// with an ordinary authenticated principal, since <c>inventory-admin</c>
    /// wants a permission and the body ceiling has nothing to do with
    /// authorization.
    /// </summary>
    private const string Route = "/api/v1/orders";

    /// <summary>
    /// Exactly the ceiling passes. Kestrel refuses what exceeds the limit
    /// rather than what reaches it, and asserting the boundary from both sides
    /// is what separates a configured limit from a limit of zero — every
    /// oversize test alone would pass against one.
    /// </summary>
    [Fact]
    public async Task A_body_at_the_ceiling_is_forwarded()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        HttpResponseMessage response = await Post(GatewayLimits.MaxRequestBodyBytes, stub.Address, ct);

        response.StatusCode.ShouldBe(
            HttpStatusCode.NoContent,
            "the stub answers 204, so this reached the destination");
    }

    /// <summary>
    /// One byte past it is refused, in §10.5's shape and with §10.5's status.
    /// </summary>
    /// <remarks>
    /// <b>No exception handler was needed for this, which is worth stating
    /// because the 400 and 409 rows each needed one.</b> Kestrel throws
    /// <c>BadHttpRequestException</c> carrying 413, and
    /// <c>ExceptionHandlerMiddleware</c> takes the status off that exception
    /// instead of its own 500 default — so the response is already
    /// <c>application/problem+json</c> with the <c>correlationId</c> and
    /// <c>traceId</c> members <c>AddCommonProblemDetails</c> adds. Verified by
    /// running it, because the reverse — YARP absorbing a client-body fault
    /// into its own 400 — is the outcome the forwarder's error handling makes
    /// plausible.
    /// </remarks>
    [Fact]
    public async Task A_body_past_the_ceiling_is_refused_as_problem_json()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        HttpResponseMessage response = await Post(GatewayLimits.MaxRequestBodyBytes + 1, stub.Address, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        body.RootElement.GetProperty("status").GetInt32().ShouldBe(413);
        body.RootElement.GetProperty("instance").GetString().ShouldBe($"POST {Route}");
        body.RootElement.GetProperty("correlationId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// A chunked body — no <c>Content-Length</c> for Kestrel to read up front —
    /// is refused on the same terms.
    /// </summary>
    /// <remarks>
    /// The two are one limit and two enforcement points: a declared length is
    /// rejected before a byte of the body is read, while a chunked one is
    /// counted as it arrives and refused when the running total passes the
    /// ceiling. A test over the first alone would leave the streaming case —
    /// the one an attacker chooses, because it costs the sender nothing to
    /// omit a header — resting on an assumption about which of the two Kestrel
    /// implements.
    /// </remarks>
    [Fact]
    public async Task A_chunked_body_past_the_ceiling_is_refused_too()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using GatewayOnKestrel gateway = new(stub.Address);

        using HttpRequestMessage request = Authenticated(HttpMethod.Post, Route);
        request.Content = new StreamContent(new UnknownLengthStream(GatewayLimits.MaxRequestBodyBytes + 1));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TransferEncodingChunked = true;

        // A MemoryStream is seekable, so StreamContent reads its length and
        // sends Content-Length anyway — which made the first version of this
        // test a second copy of the one above, passing for the wrong reason.
        // The assertion is here because it is the only thing that told the
        // difference.
        request.Content.Headers.ContentLength.ShouldBeNull("otherwise this is the previous test again");

        HttpResponseMessage response = await gateway.Client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    private static async Task<HttpResponseMessage> Post(long bytes, string destination, CancellationToken ct)
    {
        using GatewayOnKestrel gateway = new(destination);

        using HttpRequestMessage request = Authenticated(HttpMethod.Post, Route);
        request.Content = new ByteArrayContent(new byte[bytes]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        return await gateway.Client.SendAsync(request, ct);
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string route)
    {
        HttpRequestMessage request = new(method, route);
        request.Headers.Add(TestAuthHandler.UserHeader, "subject-a");

        return request;
    }

    /// <summary>
    /// A fixed number of bytes that nothing can measure in advance, which is
    /// what makes the request chunked.
    /// </summary>
    /// <remarks>
    /// <c>StreamContent</c> asks a seekable stream for its length and sends a
    /// <c>Content-Length</c> header from it, so the streaming case can only be
    /// reached with a stream that refuses the question — the shape a client
    /// producing a body as it goes actually has.
    /// </remarks>
    private sealed class UnknownLengthStream(long length) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int served = (int)Math.Min(count, _remaining);
            Array.Clear(buffer, offset, served);
            _remaining -= served;

            return served;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// The stubbed gateway served by a real Kestrel on an ephemeral port,
    /// started eagerly so the client's base address is the listening one.
    /// </summary>
    /// <remarks>
    /// A type rather than three lines repeated in each test, because the order
    /// is load-bearing and easy to get wrong silently:
    /// <c>WebApplicationFactory.UseKestrel</c> throws once the host has been
    /// initialised, and initialisation is what <c>CreateClient</c> does — so a
    /// factory whose client is taken first is a <c>TestServer</c> again, with
    /// no limit and no failure to say so.
    /// </remarks>
    private sealed class GatewayOnKestrel : IDisposable
    {
        private readonly StubbedGatewayFactory _factory;

        public GatewayOnKestrel(string destination)
        {
            _factory = new StubbedGatewayFactory(destination);
            _factory.UseKestrel(0);
            _factory.StartServer();

            Client = _factory.CreateClient();
        }

        public HttpClient Client { get; }

        public void Dispose()
        {
            Client.Dispose();
            _factory.Dispose();
        }
    }
}
