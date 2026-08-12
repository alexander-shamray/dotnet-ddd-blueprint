using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>
/// §4.2's forwarded-headers block, driven rather than configured: a trusted
/// <c>X-Forwarded-For</c> must reach the rate limiter, because the limiter's
/// partition key is the client address.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only the refusals were tested until Copilot said so.</b>
/// <c>ConditionalBlockTests</c> proves the host refuses to start when
/// <c>Ingress:Enabled</c> is on and unconfigured, and that a bad CIDR fails at
/// startup — both about configuration that is absent or malformed. Nothing
/// proved the positive path: that a header from a trusted peer is applied, and
/// applied *before* the limiter partitions on the address. §10.1 opens the
/// chapter with what that costs — "a gateway that assumes it is the edge
/// rate-limits the whole world as one client" — and §4.2 puts a whole row on
/// it, and neither had a test.
/// </para>
/// <para>
/// <b>The discriminator is a second forwarded address, not a status.</b> One
/// client spends the anonymous window and is refused, which proves the limiter
/// is live and keyed on something; a different forwarded address then gets its
/// own budget. With the header ignored — the middleware moved below the
/// limiter, or a trusted network that never matches the peer — both addresses
/// collapse onto the connection's own, and the second request is the
/// hundred-and-second rather than the first.
/// </para>
/// </remarks>
public sealed class ForwardedHeadersTests(StubDestination stub) : IClassFixture<StubDestination>
{
    /// <summary>§10.3's anonymous fixed window.</summary>
    private const int PermitLimit = 100;

    private const string PublicRoute = "/api/v1/catalog/products";

    /// <summary>Two addresses from the documentation range (RFC 5737).</summary>
    private const string FirstClient = "203.0.113.7";
    private const string SecondClient = "203.0.113.8";

    [Fact]
    public async Task A_trusted_forwarded_address_is_what_the_limiter_partitions_on()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using BehindIngressFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        for (int i = 0; i < PermitLimit; i++)
        {
            HttpResponseMessage permitted = await Get(client, FirstClient, ct);

            permitted.StatusCode.ShouldBe(
                HttpStatusCode.NoContent,
                $"request {i + 1} of {PermitLimit} is inside {FirstClient}'s window");
        }

        HttpResponseMessage exhausted = await Get(client, FirstClient, ct);

        exhausted.StatusCode.ShouldBe(
            HttpStatusCode.TooManyRequests,
            "the window is spent for this address, which is what makes the next assertion mean anything");

        HttpResponseMessage other = await Get(client, SecondClient, ct);

        other.StatusCode.ShouldBe(
            HttpStatusCode.NoContent,
            $"{SecondClient} holds its own window — a gateway ignoring the forwarded header would meter " +
            "both addresses as the one connection it can see (§10.1)");
    }

    private static async Task<HttpResponseMessage> Get(HttpClient client, string forwardedFor, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, PublicRoute);
        request.Headers.Add("X-Forwarded-For", forwardedFor);

        return await client.SendAsync(request, ct);
    }

    /// <summary>
    /// The gateway as it runs in Kubernetes (§15.3): a proxy in front, and
    /// only that proxy trusted.
    /// </summary>
    private sealed class BehindIngressFactory(string destination) : StubbedGatewayFactory(destination)
    {
        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
        [
            .. base.AdditionalSettings,
            new("Ingress:Enabled", "true"),

            // Loopback, because that is what the peer is made to be below.
            // Trusting a network that never matches the peer is one of the two
            // ways this silently reverts to metering everyone together, and it
            // is the way a real deployment gets it wrong.
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
    /// leaves null.
    /// </summary>
    /// <remarks>
    /// An <see cref="IStartupFilter"/> rather than middleware added in the
    /// host, because this has to run <b>before</b>
    /// <c>app.UseForwardedHeaders()</c> — the middleware only honours a
    /// forwarded header from a peer it can see and match against
    /// <c>KnownIPNetworks</c>, and a null peer matches nothing. Startup filters
    /// wrap the application's own pipeline, which is the one seam that gets in
    /// front of a <c>Program.cs</c> the test may not edit.
    /// </remarks>
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
