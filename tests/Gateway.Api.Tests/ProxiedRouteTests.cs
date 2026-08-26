using System.Net;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>
/// The routes driven all the way through the forwarder, against a stub
/// standing in for the service.
/// </summary>
/// <remarks>
/// This is the only place the prefix strip of §10.2 is observed rather than
/// described. The in-process API tests of §12.4 call a service on the path it
/// maps, so they exercise everything after the strip and nothing before it,
/// and <see cref="RouteConfigurationTests"/> reads the configuration that
/// asks for the strip rather than the request that came out of it.
/// </remarks>
public sealed class ProxiedRouteTests(StubDestination stub) : IClassFixture<StubDestination>
{
    /// <summary>
    /// §10.2's table, end to end: the client calls <c>/api/v1/catalog/…</c>,
    /// the gateway strips <c>/api</c>, and the service receives
    /// <c>/v1/catalog/…</c> — which is the group <c>ProductEndpoints</c> maps.
    /// </summary>
    [Fact]
    public async Task The_service_receives_the_path_with_the_namespace_prefix_removed()
    {
        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/api/v1/catalog/products", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The LAST path, not any path. The stub is a class fixture and
        // The_public_route_forwards_a_caller_carrying_no_token sends this exact
        // path too, so ShouldContain could be satisfied by that test's entry
        // while this request was forwarded wrongly — an assertion passing on
        // somebody else's evidence. The same hazard the POST case avoids by
        // counting before and after.
        stub.ReceivedPaths.Last().ShouldBe("/v1/catalog/products");
    }

    /// <summary>
    /// The second namespace strips its own prefix and only its own — one strip
    /// per namespace, and <c>/bff</c> has exactly one.
    /// </summary>
    [Fact]
    public async Task The_bff_namespace_strips_bff_rather_than_api()
    {
        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/bff/dashboard");
        request.Headers.Add(TestAuthHandler.UserHeader, "018f4c2e");

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        stub.ReceivedPaths.Last().ShouldBe("/dashboard");
    }

    /// <summary>
    /// The public route names YARP's reserved <c>anonymous</c> policy, so a
    /// caller with no token reaches the service. A 401 here would mean §10.2's
    /// one public path had stopped being public — which is the failure the
    /// fallback policy of §11.4 makes reachable by an edit to the route file
    /// alone.
    /// </summary>
    [Fact]
    public async Task The_public_route_forwards_a_caller_carrying_no_token()
    {
        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/api/v1/catalog/products", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// The positive half of the permission check, for the same reason §11.4
    /// wants both directions: a policy that refuses everybody passes every
    /// negative test there is.
    /// </summary>
    [Fact]
    public async Task The_admin_route_admits_a_caller_holding_the_permission()
    {
        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/inventory/stock");
        request.Headers.Add(TestAuthHandler.UserHeader, "018f4c2e");
        request.Headers.Add(TestAuthHandler.PermissionsHeader, GatewayPermissions.InventoryAdmin);

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// The public route matches GET alone (§10.2), so the POST Catalog exposes
    /// behind <c>catalog:write</c> is not reachable through the gateway.
    /// </summary>
    /// <remarks>
    /// <b>405, and this comment claimed 404 until the assertion was tightened
    /// enough to check.</b> The reasoning behind 404 was that a method-limited
    /// route means no route matched — which is not how ASP.NET Core routing
    /// works: the path pattern matches, the method constraint rejects, and the
    /// result is <c>MethodNotAllowed</c>. Nothing in the blueprint claimed
    /// otherwise, so the defect was one comment and the weak assertion that
    /// let it stand — <c>ShouldNotBe(NoContent)</c>, which passes for 401,
    /// 403, 429 and 500 alike.
    /// <para>
    /// <b>Who is asking now decides which of the two it is, and that is
    /// §11.4's fallback policy reaching a route nobody wrote.</b> The 405 is
    /// produced by routing's own short-circuit endpoint, which carries no
    /// authorization metadata — so <c>AddCommonWebDefaults</c>'
    /// <c>SetFallbackPolicy</c> applies to it and an anonymous caller is
    /// challenged before the method is ever considered. An authenticated one
    /// still gets 405. Both halves are asserted below, because the anonymous
    /// half alone would go on passing if the route stopped existing.
    /// </para>
    /// <para>
    /// This is a deliberate consequence rather than a side effect: 405 tells an
    /// unauthenticated caller which methods a path accepts, and the platform's
    /// posture (§11.2) is that such a caller learns nothing. The public GET is
    /// unaffected — it names <c>anonymous</c> in the route file, so it carries
    /// metadata and the fallback never reaches it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_public_route_matches_no_method_but_get()
    {
        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        // Counted before, not asserted against the path afterwards: the stub is
        // a class fixture and the tests above it have already sent that exact
        // path, so a ShouldNotContain here would pass or fail on test order.
        int before = stub.ReceivedPaths.Count;

        using HttpRequestMessage request =
            new(HttpMethod.Post, "/api/v1/catalog/products");
        request.Headers.Add(TestAuthHandler.UserHeader, "018f4c2e");

        HttpResponseMessage response =
            await client.SendAsync(request, TestContext.Current.CancellationToken);

        // The status, not merely "not the one the stub answers": the weaker
        // form went on passing whatever the reason, which is how it kept a
        // wrong comment above it alive.
        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);

        // And nothing reached a destination, which is the half a status code
        // cannot show — a 405 minted by the service would read identically
        // from here.
        stub.ReceivedPaths.Count.ShouldBe(before);
    }

    /// <summary>
    /// The same request without a caller, which the fallback policy answers
    /// first (§11.4).
    /// </summary>
    /// <remarks>
    /// Paired with the test above rather than replacing it. On its own a 401
    /// here is satisfied by a gateway with no routes at all — every path would
    /// answer 401 — so it says nothing about <c>catalog-public</c>; the 405
    /// beside it is what establishes the route exists and is GET-only.
    /// </remarks>
    [Fact]
    public async Task A_wrong_method_is_challenged_before_it_is_refused()
    {
        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        int before = stub.ReceivedPaths.Count;

        HttpResponseMessage response = await client.PostAsync(
            "/api/v1/catalog/products",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        stub.ReceivedPaths.Count.ShouldBe(before);
    }
}
