using System.Net;
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
    /// The browser flow that actually breaks: an <c>OPTIONS</c> preflight to a
    /// proxied route, asking to send an <c>Authorization</c> header.
    /// </summary>
    /// <remarks>
    /// A simple GET to a health probe — which is all the test above does —
    /// exercises none of what makes CORS fragile here. The preflight carries
    /// **no token by construction**, and the route it names carries §10.2's
    /// <c>authenticated</c> policy, so anything that let authorization see this
    /// request would answer 401 and the browser would report a CORS failure for
    /// a request the server never intended to refuse.
    /// Raised by Copilot as a gap, and it is: the flag tests prove the policy
    /// is configured, not that a browser can use it.
    ///
    /// <para>
    /// This route carries **no** method constraint — a claim that it did stood
    /// here for one round and was wrong, since <c>catalog-public</c> is the
    /// only route in §10.2 that limits methods. That case is the one below,
    /// because a preflight crossing a <c>Match:Methods</c> asks a different
    /// question from one crossing an authorization policy.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Cors_answers_a_preflight_for_a_proxied_route_that_requires_a_token()
    {
        using CorsFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage preflight = new(HttpMethod.Options, "/api/v1/orders/018f4c2e");
        preflight.Headers.Add("Origin", CorsFactory.Origin);
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization");

        HttpResponseMessage response = await client.SendAsync(preflight, TestContext.Current.CancellationToken);

        // The status, not "not 401". A browser rejects a preflight on any
        // non-success status whatever headers came with it, so 403, 429 and
        // 500 all leave the flow just as unusable as the 401 this test was
        // written for — and the weaker assertion passed for every one of them.
        // The CORS middleware short-circuits a valid preflight with 204.
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        response.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(CorsFactory.Origin);
        response.Headers.GetValues("Access-Control-Allow-Methods").ShouldContain(
            m => m.Contains("GET", StringComparison.Ordinal));
        response.Headers.GetValues("Access-Control-Allow-Headers").ShouldContain(
            h => h.Contains("authorization", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The same preflight against the one route that limits methods, which is
    /// the shipped platform's only public path.
    /// </summary>
    /// <remarks>
    /// A preflight is an <c>OPTIONS</c>, and <c>catalog-public</c> matches
    /// <c>GET</c> alone (§10.2) — so this is the case where the CORS
    /// middleware must answer before YARP's method constraint refuses, and the
    /// browser flow that a real SPA actually makes. Nothing covered it until
    /// Copilot pointed out that the route named in the case above carries no
    /// <c>Match:Methods</c> at all.
    /// </remarks>
    [Fact]
    public async Task Cors_answers_a_preflight_for_the_method_limited_public_route()
    {
        using CorsFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage preflight = new(HttpMethod.Options, "/api/v1/catalog/products");
        preflight.Headers.Add("Origin", CorsFactory.Origin);
        preflight.Headers.Add("Access-Control-Request-Method", "GET");

        HttpResponseMessage response = await client.SendAsync(preflight, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
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

    /// <summary>
    /// A section that exists and holds nothing usable, which is the shape
    /// <c>GetRequiredSection</c> cannot see.
    /// </summary>
    /// <remarks>
    /// <c>Cors__Origins__0=</c> is the commonest way a deployment gets this
    /// wrong, and it binds to an array holding one empty string:
    /// <c>WithOrigins</c> accepts it, the host starts, and every browser
    /// request is rejected by a policy matching no origin at all. The same
    /// finding <c>AddJwtAuthentication</c> already carried for
    /// <c>Identity:Authority</c> (§11.3) — blank counts as missing — learned
    /// once there and not applied here until Copilot said so.
    /// </remarks>
    [Fact]
    public void Cors_enabled_with_a_blank_origin_refuses_to_start()
    {
        using CorsWithBlankOriginFactory factory = new();

        InvalidOperationException thrown =
            Should.Throw<InvalidOperationException>(() => _ = factory.Services);

        thrown.Message.ShouldContain("Cors:Origins");
    }

    /// <summary>
    /// A value that is present, non-blank, and still not an origin.
    /// </summary>
    /// <remarks>
    /// <c>https//spa.example</c> — one missing colon — is what a typo produces,
    /// and <c>WithOrigins</c> takes it as a literal to compare against rather
    /// than rejecting it. The host starts healthy and matches no browser ever,
    /// which is the blank entry's outcome reached by another route. The guard
    /// grew in three rounds — empty, then <c>*</c>, then this — and each was a
    /// value the one before it admitted.
    /// </remarks>
    [Fact]
    public void Cors_enabled_with_a_malformed_origin_refuses_to_start()
    {
        using CorsWithMalformedOriginFactory factory = new();

        InvalidOperationException thrown =
            Should.Throw<InvalidOperationException>(() => _ = factory.Services);

        thrown.Message.ShouldContain("https//spa.example");
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

    private sealed class CorsWithBlankOriginFactory : GatewayFactory
    {
        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
        [
            new("Cors:Enabled", "true"),
            new("Cors:Origins:0", string.Empty)
        ];
    }

    private sealed class CorsWithMalformedOriginFactory : GatewayFactory
    {
        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
        [
            new("Cors:Enabled", "true"),
            new("Cors:Origins:0", "https//spa.example")
        ];
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
