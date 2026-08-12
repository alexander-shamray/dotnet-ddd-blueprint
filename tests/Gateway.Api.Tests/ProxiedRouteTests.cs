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
        stub.ReceivedPaths.ShouldContain("/v1/catalog/products");
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
        stub.ReceivedPaths.ShouldContain("/dashboard");
    }

    /// <summary>
    /// The public route carries no authorization policy, so a caller with no
    /// token reaches the service. A 401 here would mean §10.2's one public
    /// path had stopped being public.
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
    /// 405 leaks nothing here: the path is public by design, so confirming it
    /// exists tells a caller only what §10.2 already publishes. The write path
    /// stays unreachable at the edge either way.
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

        HttpResponseMessage response = await client.PostAsync(
            "/api/v1/catalog/products",
            content: null,
            TestContext.Current.CancellationToken);

        // The status, not merely "not the one the stub answers": the weaker
        // form went on passing whatever the reason, which is how it kept a
        // wrong comment above it alive.
        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);

        // And nothing reached a destination, which is the half a status code
        // cannot show — a 405 minted by the service would read identically
        // from here.
        stub.ReceivedPaths.Count.ShouldBe(before);
    }
}
