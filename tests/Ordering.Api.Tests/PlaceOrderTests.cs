using System.Net;
using System.Net.Http.Json;
using Ordering.Application.Orders;
using Ordering.Application.Orders.PlaceOrder;
using Ordering.TestSupport;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>
/// §6.4's slice end to end against a real database — dispatcher, pipeline,
/// transaction behaviour, repository, EF — so what is proved is the slice, not
/// a re-wiring of it.
/// </summary>
/// <remarks>
/// <b>Here rather than in <c>Ordering.Application.Tests</c>, where §12.1 homes
/// handler tests, and the reason is <see cref="Common.Application.ICurrentUser"/>.</b>
/// Its implementation is <c>HttpContextCurrentUser</c>, so a handler resolved
/// in a bare service scope has no principal and <c>Id</c> throws before any
/// assertion is reached. Catalog's handler tests live at the application level
/// because <c>PublishProductHandler</c> takes no principal; the first handler
/// that binds a subject has to be driven by something that can supply one.
/// Faking an <c>HttpContext</c> in the other project was the alternative, and
/// it needs the framework reference §4.1 keeps out of a plain test project.
/// <para>
/// Prices are seeded straight into <c>ordering.ProductPrices</c>, which is the
/// only way this slice can be exercised today: the projection that fills that
/// table from Catalog's events is PR-20's, and PR-20 depends on this PR. A raw
/// INSERT is allowed here where §12.4 asks for seeding through the aggregate —
/// the table is a read model with no aggregate behind it, so there is no
/// domain type whose shape it could drift from.
/// </para>
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class PlaceOrderTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly Guid Caller = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task An_order_with_no_priced_products_is_refused_as_a_rule_not_a_bad_request()
    {
        // The state every environment is in until PR-20 lands the projection:
        // the table exists and is empty. 422 rather than 400 is the point —
        // the request was well-formed and the validator passed it, and the
        // products being unpriceable is a fact about this service's state.
        HttpResponseMessage response = await PlaceAsync(Guid.CreateVersion7());

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_priced_product_commits_an_order_at_the_projected_price()
    {
        // The price comes off the projection and never off the request, which
        // is what stops a caller naming their own price.
        Guid product = Guid.CreateVersion7();
        await SeedPriceAsync(product, 19.99m, "EUR");

        HttpResponseMessage response = await PlaceAsync(product, quantity: 2);

        // 200 rather than 201, and it is the platform's answer rather than
        // this endpoint's: ToHttpResult maps a successful Result<T> to
        // Results.Ok (§10.5), which is what Catalog's POST returns too.
        // Changing it is a Common.Web decision affecting every service and
        // §10.5's table, not one a service PR takes on its own.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Guid id = await IdOfAsync(response);

        // The handler never called SaveChanges — a committed row is the
        // transaction behaviour doing its half (§6.3).
        (await fixture.ScalarAsync<decimal>(
            "SELECT Value = UnitPriceAmount FROM ordering.OrderLines WHERE OrderId = {0}", id))
            .ShouldBe(19.99m);

        (await fixture.ScalarAsync<int>(
            "SELECT Value = Quantity FROM ordering.OrderLines WHERE OrderId = {0}", id))
            .ShouldBe(2);

        (await fixture.ScalarAsync<string>(
            "SELECT Value = Status FROM ordering.Orders WHERE Id = {0}", id))
            .ShouldBe("AwaitingStock", "stored by name, never by number (§7.2)");
    }

    [Fact]
    public async Task The_order_is_attributed_to_the_caller_and_not_to_anything_in_the_request()
    {
        // §11.4's subject rule, asserted where it is enforced. There is no
        // CustomerId on the command to override — the absence is the mechanism
        // — so what this checks is that the stored owner is the principal the
        // request arrived with.
        Guid product = Guid.CreateVersion7();
        await SeedPriceAsync(product, 5m, "EUR");

        Guid id = await IdOfAsync(await PlaceAsync(product));

        (await fixture.ScalarAsync<Guid>(
            "SELECT Value = CustomerId FROM ordering.Orders WHERE Id = {0}", id))
            .ShouldBe(Caller);
    }

    [Fact]
    public async Task A_price_in_another_currency_does_not_satisfy_the_order()
    {
        // The projection is keyed by (ProductId, Currency), so a product
        // priced only in USD is unpriceable in EUR — and must be refused
        // rather than matched on the id alone.
        Guid product = Guid.CreateVersion7();
        await SeedPriceAsync(product, 19.99m, "USD");

        (await PlaceAsync(product)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task An_unavailable_product_is_not_orderable_though_its_price_is_known()
    {
        // IsAvailable is the reader's filter rather than a deletion, so the
        // history of what a product cost survives it being unpublished.
        Guid product = Guid.CreateVersion7();
        await SeedPriceAsync(product, 19.99m, "EUR", available: false);

        (await PlaceAsync(product)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_malformed_request_is_a_400_before_the_domain_sees_it()
    {
        // ValidationBehavior's half, translated by §10.5's handler. The domain
        // would also refuse an empty item list, and the difference in status
        // is §5.7's division: a bad request is the caller's phrasing, a rule
        // is the model's answer to a well-formed one.
        HttpClient client = Authenticated();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/orders",
            new PlaceOrderCommand(
                [],
                new AddressDto("1 Test Street", null, "Almaty", "050000", "KZ"),
                "EURO"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private HttpClient Authenticated()
    {
        HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Caller.ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsHeader, OrderingPermissions.Write);

        return client;
    }

    private Task<HttpResponseMessage> PlaceAsync(Guid product, int quantity = 1) =>
        Authenticated().PostAsJsonAsync(
            "/v1/orders",
            new PlaceOrderCommand(
                [new PlaceOrderItem(product, quantity)],
                new AddressDto("1 Test Street", null, "Almaty", "050000", "KZ"),
                "EUR"),
            TestContext.Current.CancellationToken);

    private static async Task<Guid> IdOfAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);

    private Task SeedPriceAsync(Guid product, decimal amount, string currency, bool available = true) =>
        fixture.ExecuteAsync(
            """
            INSERT INTO ordering.ProductPrices (ProductId, Currency, Amount, IsAvailable, UpdatedAt)
            VALUES ({0}, {1}, {2}, {3}, SYSDATETIMEOFFSET());
            """,
            product,
            currency,
            amount,
            available);
}
