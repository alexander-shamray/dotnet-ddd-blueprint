using System.Net;
using System.Net.Http.Json;
using Common.Contracts.Catalog.V1;
using Common.Infrastructure.Inbox;
using Ordering.Application.Orders.PlaceOrder;
using Ordering.Infrastructure.Messaging;
using Ordering.TestSupport;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>
/// The full async path of Appendix C's PR-20 row: a Catalog event published on
/// a real broker, consumed by the real receive endpoint, applied by §6.6's
/// projection, and read back by the write path that depends on it.
/// </summary>
/// <remarks>
/// <b>The real transport rather than the harness, because the harness removes
/// the thing under test.</b> <c>AddMassTransitTestHarness</c> replaces the
/// <c>UsingRabbitMq</c> configuration wholesale — and the receive endpoint,
/// its retry policy and its inbox filter all live inside that callback, so a
/// harness-backed version of this suite would assert a topology nothing had
/// declared. <see cref="MessagingRegistrationTests"/> covers what survives the
/// swap; this covers what does not.
/// <para>
/// <b>Ordering publishes Catalog's event to itself, and the topology is the
/// same either way.</b> MassTransit routes on the message type, so the
/// exchange this reaches is the one Catalog would publish to — what is being
/// asserted is Ordering's binding to it, and a second host would add a
/// container and prove nothing further.
/// </para>
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class CatalogEventEndpointTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly Guid Caller = Guid.Parse("44444444-4444-4444-4444-444444444444");

    /// <summary>
    /// How long a published message is given to reach the table. Generous
    /// because it covers a broker round trip on a runner that is also holding
    /// two other container sets, and bounded because the failure this suite
    /// exists to catch — an endpoint that binds nothing — never arrives late,
    /// it never arrives.
    /// </summary>
    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Every message id this test published, so <see cref="DisposeAsync"/> can
    /// wait for each delivery to finish before the next test truncates.
    /// </summary>
    private readonly List<Guid> _published = [];

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    /// <summary>
    /// Drains the deliveries this test started. Without it a test can pass and
    /// return while its own message is still being consumed.
    /// </summary>
    /// <remarks>
    /// <b>The assertions here finish one write too early, and the inbox row is
    /// the one they miss.</b> The projection writes through Dapper on its own
    /// connection, inside the consumer; §9.5's filter commits the inbox row
    /// <em>after</em> the consumer returns. So a test polling
    /// <c>ordering.ProductPrices</c> sees what it came for while the delivery
    /// is still in flight, returns, and the next test's <c>ResetAsync</c>
    /// truncates the <c>ordering</c> schema underneath that pending
    /// <c>SaveChangesAsync</c> — an intermittent failure or a row leaking into
    /// a test that did not publish it, arriving as a flake rather than as a
    /// defect. Copilot found it; the collection running serially is what makes
    /// the race reachable rather than what prevents it.
    /// <para>
    /// The inbox row is the right thing to wait on because it is the
    /// <b>last</b> write of the delivery, which is precisely the property
    /// §9.5's ordering gives it — waiting on the projection's own table would
    /// reproduce the bug.
    /// </para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        foreach (Guid messageId in _published)
        {
            await Eventually(
                async () => (await InboxRowsAsync(messageId)).Count,
                expected: 1,
                because: "a delivery still running when the next test resets the schema is a flake in " +
                    "that test rather than a failure in this one");
        }
    }

    [Fact]
    public async Task A_published_product_reaches_the_projection_over_the_broker()
    {
        Guid product = Guid.CreateVersion7();
        var messageId = Guid.CreateVersion7();

        await PublishAsync(product, 19.99m, "EUR", messageId);

        await Eventually(
            () => fixture.ScalarAsync<int>(
                "SELECT Value = COUNT(*) FROM ordering.ProductPrices WHERE ProductId = {0}",
                product),
            expected: 1,
            because: "the endpoint declared in AddMassTransitMessaging is what binds ProductPublished to " +
                "the projection — a consumer registered and never bound looks exactly like this until the " +
                "budget runs out");

        (await fixture.ScalarAsync<decimal>(
            "SELECT Value = Amount FROM ordering.ProductPrices WHERE ProductId = {0}",
            product))
            .ShouldBe(19.99m);
    }

    [Fact]
    public async Task One_delivery_is_recorded_once_on_the_named_queue()
    {
        // Two claims in one, and the endpoint name is the sharper of them.
        //
        // §9.8 requires every receive endpoint to apply InboxFilter<>, so a
        // row here is what says the message arrived on the endpoint that has
        // one. Both halves were measured while writing this: with
        // AddMassTransitMessaging's ConfigureEndpoints call still in place and
        // the ProductPublished binding deleted, MassTransit manufactured a
        // queue named after the consumer type, the projection ran, and NO
        // inbox row was written — a policied endpoint on paper and an
        // policy-free one in the broker. That call is gone for that reason, and
        // this row is the standing check that it does not come back.
        //
        // The count is the second claim: one publish, one queue, one row. Two
        // would mean the same event is bound twice, which an idempotent MERGE
        // survives and the projections after it would not.
        //
        // Filtered by message id rather than counted whole: the collection is
        // serial, but a message still in flight from the previous test would
        // otherwise decide this assertion.
        Guid product = Guid.CreateVersion7();
        var messageId = Guid.CreateVersion7();

        await PublishAsync(product, 19.99m, "EUR", messageId);

        await Eventually(
            async () => (await InboxRowsAsync(messageId)).Count,
            expected: 1,
            because: "a delivery that leaves no inbox row reached an endpoint with no filter on it, which " +
                "§9.8 permits only for the saga and only in writing");

        // Held for a moment past the first sighting, because the count claim
        // is about a SECOND row: an assertion that stops at the first would
        // pass whether or not another was on its way.
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        IReadOnlyList<InboxMessage> rows = await InboxRowsAsync(messageId);

        rows.Count.ShouldBe(1);
        rows[0].Endpoint.ShouldBe(
            DependencyInjection.CatalogEventsQueue,
            "§9.4 and §9.8 both print this queue name, and the saga (§9.6) will address the other two by " +
            "name — a renamed queue is a subscription that silently stops arriving");
    }

    [Fact]
    public async Task A_product_becomes_orderable_once_its_price_has_been_projected()
    {
        // Appendix C's "full async path" in one test: Catalog's fact leaves a
        // broker, Ordering's read model absorbs it, and §6.4's write path
        // prices an order from it without a network call in the transaction.
        // Before this PR the same request answered 422 — correctly, from a
        // service with no prices.
        Guid product = Guid.CreateVersion7();

        (await PlaceAsync(product)).StatusCode.ShouldBe(
            HttpStatusCode.UnprocessableEntity,
            "the arrange half is a claim too: without it a green assertion below could be a price left " +
            "over from another test rather than the one this published");

        await PublishAsync(product, 19.99m, "EUR", Guid.CreateVersion7());

        await Eventually(
            () => fixture.ScalarAsync<int>(
                "SELECT Value = COUNT(*) FROM ordering.ProductPrices WHERE ProductId = {0}",
                product),
            expected: 1,
            because: "nothing downstream can be asserted until the projection has run");

        HttpResponseMessage response = await PlaceAsync(product);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        Guid id = await response.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);

        (await fixture.ScalarAsync<decimal>(
            "SELECT Value = UnitPriceAmount FROM ordering.OrderLines WHERE OrderId = {0}",
            id))
            .ShouldBe(19.99m, "the order is priced from the projection and never from the request");
    }

    /// <summary>
    /// Each of the other two bindings, driven over the same queue — because
    /// each is a line in <c>AddMassTransitMessaging</c> that nothing else
    /// watches.
    /// </summary>
    /// <remarks>
    /// <b>Registration and binding are two statements, and only one of them
    /// had a guard.</b> <c>MessagingRegistrationTests</c> asserts the three
    /// <c>AddConsumer</c> calls and says out loud that it cannot see a receive
    /// endpoint — the harness replaces the callback the endpoint lives in — and
    /// <c>ProductPriceProjectionTests</c> invokes the handlers directly. So
    /// deleting <c>ConfigureConsumer&lt;IntegrationEventConsumer&lt;PriceChanged&gt;&gt;</c>
    /// left every suite green while that subscription silently stopped
    /// arriving. Copilot found it, and it is this repository's most repeated
    /// shape: a line kept for a reason, watched by nothing.
    /// <para>
    /// A price rather than a publish, because <c>PriceChanged</c> reaching the
    /// table is the whole claim; the projection's own arithmetic is
    /// <c>ProductPriceProjectionTests</c>' subject and is not re-proved here.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_price_change_reaches_the_projection_over_the_broker()
    {
        Guid product = Guid.CreateVersion7();

        await PublishAsync(product, 19.99m, "EUR", Guid.CreateVersion7());
        await Eventually(
            () => AmountPenceAsync(product),
            expected: 1999,
            because: "the arrange half has to land before the assert half can mean anything");

        await PublishChangeAsync(product, 24.99m, "EUR");

        await Eventually(
            () => AmountPenceAsync(product),
            expected: 2499,
            because: "PriceChanged has its own ConfigureConsumer line, and nothing but this drives it " +
                "over the queue that line names");
    }

    [Fact]
    public async Task A_discontinuation_reaches_the_projection_over_the_broker()
    {
        Guid product = Guid.CreateVersion7();

        await PublishAsync(product, 19.99m, "EUR", Guid.CreateVersion7());
        await Eventually(
            () => AmountPenceAsync(product),
            expected: 1999,
            because: "the arrange half has to land before the assert half can mean anything");

        await PublishDiscontinuedAsync(product);

        await Eventually(
            () => fixture.ScalarAsync<int>(
                """
                SELECT Value = COUNT(*)
                FROM ordering.ProductPrices
                WHERE ProductId = {0}
                    AND IsAvailable = 0
                """,
                product),
            expected: 1,
            because: "ProductDiscontinued has its own ConfigureConsumer line too, and it is the third " +
                "binding no other test reaches");
    }

    /// <summary>
    /// Publishes with the transport id the envelope carries, which §9.1 says
    /// is one GUID rather than two — the inbox keys on the transport's, and a
    /// body claiming a different one would make the row unfindable from the
    /// message that produced it.
    /// </summary>
    private async Task PublishAsync(Guid product, decimal amount, string currency, Guid messageId)
    {
        ProductPublished published = new()
        {
            MessageId = messageId,
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = DateTimeOffset.UtcNow,
            ProductId = product,
            Name = "A product",
            ThumbnailUrl = null,
            Amount = amount,
            Currency = currency
        };

        _published.Add(messageId);

        await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .Publish(published, c => c.MessageId = messageId, TestContext.Current.CancellationToken);
    }

    private async Task PublishChangeAsync(Guid product, decimal amount, string currency)
    {
        var messageId = Guid.CreateVersion7();

        PriceChanged changed = new()
        {
            MessageId = messageId,
            CorrelationId = Guid.CreateVersion7(),
            // Strictly later than the publish above, which the projection's
            // guard requires and a same-tick clock read would not guarantee.
            OccurredAt = DateTimeOffset.UtcNow.AddMinutes(1),
            ProductId = product,
            Amount = amount,
            Currency = currency
        };

        _published.Add(messageId);

        await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .Publish(changed, c => c.MessageId = messageId, TestContext.Current.CancellationToken);
    }

    private async Task PublishDiscontinuedAsync(Guid product)
    {
        var messageId = Guid.CreateVersion7();

        ProductDiscontinued discontinued = new()
        {
            MessageId = messageId,
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = DateTimeOffset.UtcNow.AddMinutes(1),
            ProductId = product
        };

        _published.Add(messageId);

        await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .Publish(discontinued, c => c.MessageId = messageId, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The amount in minor units, because <see cref="Eventually"/> polls an
    /// <c>int</c> — and a poll on a decimal would need an equality this table
    /// does not owe.
    /// </summary>
    private Task<int> AmountPenceAsync(Guid product) =>
        fixture.ScalarAsync<int>(
            """
            SELECT Value = COALESCE(CAST(MAX(Amount) * 100 AS int), 0)
            FROM ordering.ProductPrices
            WHERE ProductId = {0}
            """,
            product);

    private async Task<IReadOnlyList<InboxMessage>> InboxRowsAsync(Guid messageId) =>
        [.. (await fixture.InboxAsync()).Where(m => m.MessageId == messageId)];

    private Task<HttpResponseMessage> PlaceAsync(Guid product)
    {
        HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Caller.ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsHeader, OrderingPermissions.Write);

        return client.PostAsJsonAsync(
            "/v1/orders",
            new PlaceOrderCommand(
                Guid.CreateVersion7(),
                [new PlaceOrderItem(product, 1)],
                new AddressDto("1 Test Street", null, "Almaty", "050000", "KZ"),
                "EUR"),
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Polls rather than sleeps, and fails with the last value it saw — a
    /// timeout that says only "expected 1" is indistinguishable from a broker
    /// that never started.
    /// </summary>
    private static async Task Eventually(Func<Task<int>> read, int expected, string because)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + DeliveryBudget;
        int actual = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = await read();

            if (actual == expected)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        actual.ShouldBe(expected, because);
    }
}
