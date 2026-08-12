using System.Collections.Concurrent;
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

        app.MapFallback(() => Results.NoContent());

        await app.StartAsync();

        Address = app.Urls.First();
        _app = app;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }
}
