using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Api.Endpoints;
using Ordering.Application.Orders;
using Ordering.Domain.Orders;
using Ordering.Infrastructure.Persistence;
using Ordering.TestSupport;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>
/// PR-16's deferred security test, carried here because it needs the first
/// resource in the platform that has an owner. §11.4's ownership check, over
/// HTTP against a real database.
/// </summary>
/// <remarks>
/// Over the wire rather than against the handler, and that is the point.
/// <c>ICurrentUser</c> is <c>HttpContextCurrentUser</c> in a running host, so
/// only a real request exercises the thing that actually answers "who is the
/// caller" — the claims projection, the authentication scheme and the
/// authorization policies included. A handler test with a substituted
/// <c>ICurrentUser</c> proves the <c>if</c>, not the mechanism it depends on.
/// <para>
/// Catalog could not host this test: every product is public to every caller
/// by design, so there was no resource whose owner could differ from the
/// caller. That is why PR-16 deferred it rather than skipping it.
/// </para>
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class OrderOwnershipTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task User_A_cancelling_user_B_s_order_gets_404_and_not_403()
    {
        // The assertion PR-16's row names. 404 rather than 403 is the whole
        // point: a 403 confirms the order exists, which hands an attacker an
        // oracle over every id they can guess. The order must still be there
        // afterwards, because a status code that lies about the outcome is
        // only half the defect — the other half is the cancellation happening
        // anyway.
        OrderId order = await SeedOrderAsync(Bob);

        HttpResponseMessage response = await CancelAsync(order, asUser: Alice);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await StatusOfAsync(order)).ShouldBe(
            nameof(OrderStatus.AwaitingStock),
            "the 404 must be a refusal, not a cancellation reported as a miss");
    }

    [Fact]
    public async Task The_owner_can_cancel_their_own_order()
    {
        // The control. Without it the test above passes on a handler that
        // returns 404 to everybody, which is a working access-control check
        // and a broken feature.
        OrderId order = await SeedOrderAsync(Bob);

        HttpResponseMessage response = await CancelAsync(order, asUser: Bob);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await StatusOfAsync(order)).ShouldBe(nameof(OrderStatus.Cancelled));
    }

    [Fact]
    public async Task An_order_that_does_not_exist_is_the_same_404()
    {
        // The two refusals must be indistinguishable from outside. If the
        // not-found path and the not-yours path ever diverge — a different
        // code, a different body, a measurably different latency — the pair
        // becomes the oracle the 404 was chosen to avoid.
        HttpResponseMessage response = await CancelAsync(new OrderId(Guid.CreateVersion7()), asUser: Alice);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_admin_claim_reaches_an_order_it_does_not_own()
    {
        // §11.4's one sanctioned override, and it is a claim rather than a
        // policy: the endpoint cannot decide this, because the order is not
        // loaded when the policy runs. Note what is still true here — nothing
        // in the request says whose order it is, so this overrides ownership
        // without breaching the subject rule.
        OrderId order = await SeedOrderAsync(Bob);

        HttpResponseMessage response = await CancelAsync(
            order,
            asUser: Alice,
            permissions: $"{OrderingPermissions.Cancel} orders:admin");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await StatusOfAsync(order)).ShouldBe(nameof(OrderStatus.Cancelled));
    }

    [Fact]
    public async Task An_unauthenticated_caller_gets_401_and_never_reaches_the_handler()
    {
        // The group's RequireAuthorization, doing its half. This matters
        // beside the ownership check rather than instead of it: the handler's
        // guard fails closed on a missing principal too, and two independent
        // refusals is the design — one of them removed should still leave the
        // order alone.
        OrderId order = await SeedOrderAsync(Bob);

        HttpResponseMessage response = await CancelAsync(order, asUser: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await StatusOfAsync(order)).ShouldBe(nameof(OrderStatus.AwaitingStock));
    }

    [Fact]
    public async Task A_caller_without_the_cancel_permission_gets_403()
    {
        // 403 rather than 404 here, and the difference from the ownership case
        // is deliberate: this caller is refused by the endpoint policy before
        // any order is loaded, so nothing has been revealed about whether the
        // id exists — the response is the same for every id, which is what
        // makes it safe to be honest about the missing permission.
        OrderId order = await SeedOrderAsync(Bob);

        HttpResponseMessage response = await CancelAsync(order, asUser: Bob, permissions: "orders:write");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await StatusOfAsync(order)).ShouldBe(nameof(OrderStatus.AwaitingStock));
    }

    /// <summary>
    /// §12.4's shared seeding helper, on the fixture rather than here so both
    /// suites reach one implementation of "an order that exists".
    /// </summary>
    private async Task<OrderId> SeedOrderAsync(Guid customer) =>
        new(await fixture.SeedOrderAsync(customer));

    private Task<HttpResponseMessage> CancelAsync(
        OrderId order,
        Guid? asUser,
        string? permissions = null)
    {
        HttpClient client = fixture.Factory.CreateClient();
        if (asUser is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, asUser.ToString());
            client.DefaultRequestHeaders.Add(
                TestAuthHandler.PermissionsHeader,
                permissions ?? OrderingPermissions.Cancel);
        }

        return client.PostAsJsonAsync(
            $"/v1/orders/{order.Value}/cancel",
            new CancelOrderRequest(CancelReasons.CustomerRequest),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_reason_outside_the_wire_vocabulary_is_rejected()
    {
        // §12.4's fourth security test. The enum's member name is the
        // interesting input: Enum.TryParse would accept "CustomerRequest",
        // and CancellationReasons deliberately does not — it maps the wire
        // vocabulary and refuses anything else rather than defaulting, so a
        // sibling service sending an unknown code is a loud deployment
        // problem instead of an order cancelled for the wrong recorded
        // reason.
        OrderId order = await SeedOrderAsync(Bob);
        HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Bob.ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsHeader, OrderingPermissions.Cancel);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/orders/{order.Value}/cancel",
            new CancelOrderRequest("CustomerRequest"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await StatusOfAsync(order)).ShouldBe(nameof(OrderStatus.AwaitingStock));
    }

    private Task<string> StatusOfAsync(OrderId order) =>
        fixture.ScalarAsync<string>(
            "SELECT Value = Status FROM ordering.Orders WHERE Id = {0}",
            order.Value);
}
