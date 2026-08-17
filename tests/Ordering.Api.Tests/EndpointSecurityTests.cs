using System.Net;
using System.Net.Http.Json;
using Ordering.Api;
using Ordering.TestSupport;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>
/// What only a host running the PRODUCTION authentication scheme can say.
/// Every other suite here swaps in <see cref="TestAuthHandler"/>, which is what
/// lets them authenticate at all and exactly why none of them can answer this.
/// </summary>
/// <remarks>
/// <para>
/// Catalog's counterpart is the model, and this file is the second half of the
/// promise <c>UnreachableInfrastructureFactory</c> already carried: its
/// <c>ConfigureAuthentication</c> override is deliberately empty, and its
/// comment said the forged-header suite "arrives with the first endpoint to
/// forge against". Ordering has two now.
/// </para>
/// <para>
/// Separate from <c>HostSmokeTests</c>, which owns the factory these share, and
/// separate for the scaffold's sake: this file names <c>/v1/orders</c>, so it
/// belongs to the slice and must not survive into a service with no endpoints.
/// <c>HostSmokeTests</c> is copied to every new service and this file is not.
/// </para>
/// </remarks>
public class EndpointSecurityTests(HostSmokeTests.UnreachableInfrastructureFactory factory)
    : IClassFixture<HostSmokeTests.UnreachableInfrastructureFactory>
{
    [Fact]
    public async Task Forged_identity_headers_do_not_authenticate_on_the_write_path()
    {
        // The headers are TestAuthHandler's own, which is what makes this
        // worth running: this host registers only the production JWT scheme,
        // so the headers every other suite in this assembly authenticates
        // with are just bytes here.
        //
        // The failure it catches is a test convenience reaching production
        // wiring — a scheme registered in Common.Web "for the fixtures", or
        // this factory's empty ConfigureAuthentication override deleted as
        // dead code. Every authorization test in the repository would still
        // pass, and any caller could name any subject and any permission.
        //
        // No Authorization header at all, so nothing is fetched from the
        // authority — .invalid never resolves, and a challenge needs no
        // signing keys.
        using HttpClient client = factory.CreateClient();

        HttpRequestMessage request = new(HttpMethod.Post, "/v1/orders")
        {
            Content = JsonContent.Create(
                new
                {
                    Items = new[] { new { ProductId = Guid.CreateVersion7(), Quantity = 1 } },
                    ShippingAddress = new
                    {
                        Line1 = "1 Test Street",
                        Line2 = (string?)null,
                        City = "Almaty",
                        PostalCode = "050000",
                        Country = "KZ"
                    },
                    Currency = "EUR"
                })
        };
        request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        request.Headers.Add(TestAuthHandler.PermissionsHeader, OrderingPermissions.Write);

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Forged_identity_headers_do_not_authenticate_on_the_cancel_path()
    {
        // Both routes, because they carry different policies — Write and
        // Cancel — and a 401 is decided before either is consulted. One
        // endpoint passing says nothing about a second that was added later
        // and reached for RequireAuthorization with the wrong argument.
        using HttpClient client = factory.CreateClient();

        HttpRequestMessage request = new(HttpMethod.Post, $"/v1/orders/{Guid.CreateVersion7()}/cancel")
        {
            Content = JsonContent.Create(new { Reason = "CustomerRequest" })
        };
        request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        request.Headers.Add(TestAuthHandler.PermissionsHeader, OrderingPermissions.Cancel);

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_liveness_probe_still_answers_without_a_token()
    {
        // What keeps the two above from passing for the wrong reason: if this
        // host were simply refusing everything, they would be 401 whether or
        // not authentication worked. Catalog makes this point with its public
        // product listing; Ordering has no anonymous endpoint — an order
        // belongs to somebody — so the liveness probe is the one unauthenticated
        // path there is, and §13.5 requires it to answer for the orchestrator
        // rather than for a caller.
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
