using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>
/// A real HTTP server on an ephemeral loopback port, standing in for whichever
/// service a route points at. It records the path it was given, and answers
/// 204 unless the caller asks through the query string for a body —
/// optionally under a <c>Content-Encoding</c> the stub declares for itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>A listener rather than an address that refuses.</b> The first version
/// pointed the clusters at <c>127.0.0.1:1</c> on the reasoning that a refused
/// connection costs nothing — measured, it cost about two seconds a request,
/// so the hundred requests §10.3's window admits took three and a half minutes
/// and the window replenished before the test could exhaust it. The limiter
/// was working and the test could not see it.
/// </para>
/// <para>
/// What the listener buys beyond speed is the assertion §10.2 says nothing
/// else in the solution can make: the recorded path is the path a service
/// receives, so the prefix strip is checked against the wire rather than
/// against the configuration that describes it.
/// </para>
/// </remarks>
public sealed class StubDestination : IAsyncLifetime
{
    /// <summary>
    /// Ask for a body of this many bytes instead of the default 204. Named
    /// here and read here, so a caller cannot spell it differently and get a
    /// 204 that looks like a compression failure.
    /// </summary>
    public const string BodySizeQuery = "body";

    /// <summary>
    /// Ask for a declared <c>Content-Encoding</c> on that body: <c>gzip</c>
    /// gzips it as a destination that compressed for itself would, and
    /// <c>identity</c> declares it unencoded and sends it plain.
    /// </summary>
    /// <remarks>
    /// One switch for two cases because the middleware treats them the same
    /// way — it declines any response that already carries the header — and
    /// keeping them apart in the stub would hide that the two tests are
    /// exercising one rule (ADR-020).
    /// </remarks>
    public const string ContentEncodingQuery = "encoding";

    private readonly ConcurrentQueue<string> _paths = new();
    private WebApplication? _app;

    /// <summary>The base address to point a YARP cluster at.</summary>
    public string Address { get; private set; } = string.Empty;

    /// <summary>Every path this server has been asked for, in arrival order.</summary>
    public IReadOnlyCollection<string> ReceivedPaths => _paths;

    public async ValueTask InitializeAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        // Port 0: the operating system picks a free one, so parallel test
        // classes each get their own server and nothing collides on a rerun.
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        WebApplication app = builder.Build();

        app.Use(async (context, next) =>
        {
            _paths.Enqueue(context.Request.Path.Value ?? string.Empty);

            await next();
        });

        // 204 unless the caller asks for a body, which is what keeps every test
        // written before this one unchanged: a query string is the one way to
        // vary the response that YARP forwards untouched, so the tests that
        // need bytes back ask for them per request rather than mutating a
        // fixture the class beside them is also holding.
        app.MapFallback((HttpContext context) =>
        {
            if (!int.TryParse(context.Request.Query[BodySizeQuery], out int size))
                return Results.NoContent();

            // A destination that has spoken for its own encoding — either
            // because it compressed the body itself, or because it is refusing
            // compression by declaring the body unencoded. The gateway must
            // leave both alone, and ADR-020 rests on the second.
            string? declared = context.Request.Query[ContentEncodingQuery];

            if (!string.IsNullOrEmpty(declared))
            {
                context.Response.Headers.ContentEncoding = declared;

                return declared == "gzip"
                    ? Results.Bytes(GzipOf(new string('a', size)), "application/json")
                    : Results.Text(new string('a', size), "application/json");
            }

            // One repeated character, so the body is at the compressible end of
            // what a real JSON response looks like. A compression assertion
            // wants the encoded form to be unmistakably smaller than the plain
            // one, and incompressible bytes would leave it measuring noise.
            return Results.Text(new string('a', size), "application/json");
        });

        await app.StartAsync();

        Address = app.Urls.First();
        _app = app;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }

    private static byte[] GzipOf(string body)
    {
        using MemoryStream buffer = new();

        using (GZipStream compressor = new(buffer, CompressionLevel.Fastest, leaveOpen: true))
            compressor.Write(Encoding.UTF8.GetBytes(body));

        // Not `[.. buffer]`: CLAUDE.md's spread rule governs materialising a
        // SEQUENCE, and a MemoryStream is not one — the spread fails to
        // compile on it (CS9212). Noted here because a review has already
        // read the rule the other way.
        return buffer.ToArray();
    }
}
