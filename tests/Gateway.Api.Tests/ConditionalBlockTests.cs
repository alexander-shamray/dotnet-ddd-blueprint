using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>
/// §4.2's two conditional blocks — forwarded headers and CORS — and the rule
/// that governs both: each is optional, and REQUIRED once switched on. "Off"
/// and "on but unconfigured" are different states, the first a valid topology
/// and the second a silent defect.
/// </summary>
/// <remarks>
/// Both configuration reads are hoisted out of their callbacks in
/// <c>Program.cs</c>, and these tests are why. An options callback runs when
/// the options are first resolved — for CORS, on the first request that needs
/// a policy — so the same <c>GetRequiredSection</c> read from inside one
/// throws at a request rather than at a deployment, which is the deferral the
/// pair of flags exists to avoid.
/// </remarks>
public sealed class ConditionalBlockTests
{
    [Fact]
    public void Cors_enabled_with_no_origins_refuses_to_start()
    {
        using CorsWithoutOriginsFactory factory = new();

        InvalidOperationException thrown =
            Should.Throw<InvalidOperationException>(() => _ = factory.Services);

        // Enabled but unset would otherwise yield WithOrigins([]), which
        // rejects every browser request while starting cleanly — surfacing as
        // a CORS error in a console rather than as the missing setting it is.
        thrown.Message.ShouldContain("Cors:Origins");
    }

    [Fact]
    public async Task Cors_enabled_with_origins_answers_a_browser_from_one_of_them()
    {
        using CorsFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/health/live");
        request.Headers.Add("Origin", CorsFactory.Origin);

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(CorsFactory.Origin);
    }

    /// <summary>
    /// The other half of the flag, without which the test above proves only
    /// that CORS headers exist rather than that anything turns them on.
    /// </summary>
    [Fact]
    public async Task Cors_left_off_answers_the_same_browser_with_no_header()
    {
        using GatewayFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/health/live");
        request.Headers.Add("Origin", CorsFactory.Origin);

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    [Fact]
    public void Ingress_enabled_with_no_trusted_networks_refuses_to_start()
    {
        using IngressWithoutNetworksFactory factory = new();

        InvalidOperationException thrown =
            Should.Throw<InvalidOperationException>(() => _ = factory.Services);

        // Left empty, ASP.NET Core trusts nothing beyond loopback and silently
        // keeps the proxy's address, so the rate limiter partitions all traffic
        // into one bucket and its per-client limit becomes a global cap.
        thrown.Message.ShouldContain("Ingress:TrustedNetworks");
    }

    /// <summary>
    /// A network that will not parse is the same class of defect as a section
    /// that is absent, and it must fail in the same place. This is what pins
    /// the parse to startup: it runs inside an options callback, which would
    /// otherwise be entered when the middleware is first constructed.
    /// </summary>
    [Fact]
    public void Ingress_enabled_with_an_unparseable_network_refuses_to_start() =>
        Should.Throw<FormatException>(() =>
        {
            using IngressWithBadNetworkFactory factory = new();

            _ = factory.Services;
        });

    private sealed class CorsFactory : GatewayFactory
    {
        /// <summary>The SPA's dev origin (§14.1) — 5173 is Vite's default.</summary>
        public const string Origin = "http://localhost:5173";

        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
        [
            new("Cors:Enabled", "true"),
            new("Cors:Origins:0", Origin)
        ];
    }

    private sealed class CorsWithoutOriginsFactory : GatewayFactory
    {
        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
            [new("Cors:Enabled", "true")];
    }

    private sealed class IngressWithoutNetworksFactory : GatewayFactory
    {
        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
            [new("Ingress:Enabled", "true")];
    }

    private sealed class IngressWithBadNetworkFactory : GatewayFactory
    {
        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
        [
            new("Ingress:Enabled", "true"),
            new("Ingress:TrustedNetworks:0", "10.0.0.0/8"),
            new("Ingress:TrustedNetworks:1", "not-a-network")
        ];
    }
}
