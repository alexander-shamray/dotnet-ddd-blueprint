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
/// service a route points at. It answers 204 to everything and records the
/// path it was given.
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
    /// Ask for that body gzip-encoded, with the <c>Content-Encoding</c> a
    /// destination that compressed for itself would send.
    /// </summary>
    public const string PreEncodedQuery = "encoded";

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

            // A destination that has compressed for itself. The gateway must
            // pass this through rather than encode it a second time, which is
            // also the only way a response that must not be compressed can say
            // so (ADR-020).
            if (context.Request.Query.ContainsKey(PreEncodedQuery))
            {
                context.Response.Headers.ContentEncoding = "gzip";

                return Results.Bytes(GzipOf(new string('a', size)), "application/json");
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

        return buffer.ToArray();
    }
}
