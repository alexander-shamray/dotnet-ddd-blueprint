using System.Net;
using System.Net.Http.Json;
using Common.Contracts.Ordering.V1;
using Common.Infrastructure.Outbox;
using Microsoft.AspNetCore.Mvc;
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

    [Fact]
    public async Task A_cancellation_through_this_endpoint_publishes_the_user_origin()
    {
        // **The other half of #123's translation, and it can only be proved
        // here.** A User-origin command dispatched in a bare scope has no
        // principal, so §11.4's ownership guard fails closed and returns
        // NotFound before an origin is ever written — which is the guard
        // working, and why the sibling assertion in SagaCommandHandlerTests
        // covers the System case alone. A real request is what supplies the
        // caller this path needs.
        //
        // Inverting the handler's switch would tag this cancellation as the
        // workflow's own echo, and §9.6 would then discard it on a missing
        // instance instead of faulting — the silent loss #123 exists to close.
        OrderId order = await SeedOrderAsync(Bob);

        HttpResponseMessage response = await CancelAsync(order, asUser: Bob);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        OutboxMessage row = (await fixture.OutboxAsync()).ShouldHaveSingleItem();

        row.Payload.ShouldContain(
            $"\"Origin\":\"{CancelOrigins.User}\"",
            Case.Sensitive,
            "a cancellation with a principal behind it is not this workflow's echo");
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

    [Theory]
    [InlineData(nameof(OrderStatus.Shipped))]
    [InlineData(nameof(OrderStatus.Delivered))]
    public async Task An_order_past_despatch_is_refused_with_422_and_the_shipped_code(string status)
    {
        // **#109's second half: the producer had no test at all.** Every
        // occurrence of AlreadyShipped under tests/ was a sample string —
        // §10.5's 422 was unproven and §9.8's dashboard series was built on a
        // code nothing had ever been shown to emit. OrderTests covers the
        // domain THROW, which is the half that already worked; nothing covered
        // the catch, the mapping or the status code.
        //
        // **Delivered is arranged directly because nothing reaches it.**
        // OrderStatus declares it and no transition sets it — §9.6 has no
        // ShipmentDelivered — so the guard in Order.Cancel is written for a
        // status the aggregate cannot get to on its own. Driving the row there
        // is what makes the guard testable rather than decorative, and it is
        // the case that would have caught the message defect: the old
        // description said "A shipped order cannot be cancelled" and this
        // customer's order was delivered.
        OrderId order = await SeedOrderAsync(Bob);
        await fixture.ExecuteAsync(
            "UPDATE ordering.Orders SET Status = {0} WHERE Id = {1}",
            status,
            order.Value);

        HttpResponseMessage response = await CancelAsync(order, Bob);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        ProblemDetails? problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(
            TestContext.Current.CancellationToken);

        problem.ShouldNotBeNull();
        problem.Extensions["code"]?.ToString().ShouldBe(
            "order.already_shipped",
            "the code is a §9.8 dimension value and splitting it would halve the series");
        // **The exact string, not the absence of the old one.** Asserting only
        // that the detail no longer contains "A shipped order" rejects one
        // obsolete substring and passes for a blank detail, a truncated one,
        // or any other wrong message — which leaves #109's actual subject, the
        // customer-visible wording, unpinned by the test written to pin it.
        //
        // Pinning the prose makes it a thing a later edit has to come here and
        // change, and that is the cost being accepted rather than an oversight:
        // this sentence is served to a customer, and #109 was filed because it
        // said something untrue to half of them.
        problem.Detail.ShouldBe(
            "An order that has already shipped cannot be cancelled; raise a return instead.",
            $"a {status} order's customer reads this, and naming one of the two " +
                "statuses is what #109 was filed for");

        (await StatusOfAsync(order)).ShouldBe(status, "a refusal must not have cancelled anything");
    }

    private Task<string> StatusOfAsync(OrderId order) =>
        fixture.ScalarAsync<string>(
            "SELECT Value = Status FROM ordering.Orders WHERE Id = {0}",
            order.Value);
}
