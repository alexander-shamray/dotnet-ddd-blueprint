using Common.Application;
using Common.Contracts;
using Common.Contracts.Ordering.V1;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Application;
using Ordering.Domain.Common;
using Ordering.Domain.Orders;
using Ordering.Domain.Orders.Events;
using Shouldly;
using Xunit;

namespace Ordering.Application.Tests;

/// <summary>
/// §9.3's allow-list for Ordering, which PR-21 filled. Resolved through
/// <c>AddOrderingApplication</c> rather than constructed, on
/// <c>CatalogIntegrationEventMapperTests</c>' terms: the mapper is internal,
/// and an allow-list nobody resolves publishes nothing while every assertion
/// about it passes.
/// </summary>
/// <remarks>
/// <b>Nothing else asserts a single field of these three payloads.</b> The
/// real-broker tests construct <c>V1.OrderPlaced</c> themselves rather than
/// going through the mapper, and the outbox tests assert that a row completes
/// and carries the right ids — so dropping <c>Lines</c>, or
/// <c>ShippingAddress.Line2</c>, or mapping a cancellation reason to the wrong
/// code, would have left the suite green. Copilot found the gap.
/// </remarks>
public class OrderingIntegrationEventMapperTests
{
    private static readonly DateTimeOffset Raised = new(2026, 8, 19, 9, 0, 0, TimeSpan.Zero);

    private static readonly OrderId Order = OrderId.New();

    private static readonly CustomerId Customer = new(Guid.CreateVersion7());

    private static readonly ProductId Product = ProductId.New();

    private static IIntegrationEventMapper Mapper()
    {
        ServiceCollection services = new();
        services.AddOrderingApplication();

        return services
            .BuildServiceProvider()
            .CreateScope()
            .ServiceProvider
            .GetRequiredService<IIntegrationEventMapper>();
    }

    private static IReadOnlyList<OrderLineSnapshot> Lines() =>
        [new OrderLineSnapshot(Product, 2, Money.Of(64.99m, "EUR"))];

    [Fact]
    public void A_placed_order_becomes_its_contract()
    {
        IReadOnlyList<object> mapped = Mapper().Map(
            [new OrderPlacedDomainEvent(Order, Customer, Money.Of(129.98m, "EUR"), Lines(), Raised)]);

        OrderPlaced placed = mapped.ShouldHaveSingleItem().ShouldBeOfType<OrderPlaced>();

        placed.OrderId.ShouldBe(Order.Value);
        placed.CustomerId.ShouldBe(Customer.Value);
        placed.TotalAmount.ShouldBe(129.98m);
        placed.Currency.ShouldBe("EUR");
        placed.OccurredAt.ShouldBe(Raised);

        // The correlation is the ORDER rather than an ambient request id, which
        // is what lets one id follow the workflow across every service — §9.6's
        // saga correlates its instance on the same value.
        placed.CorrelationId.ShouldBe(Order.Value);
        placed.MessageId.ShouldNotBe(Guid.Empty);

        // The lines are what ReserveStock is drawn from (§9.6). An empty list
        // here is a saga that reserves nothing and an order nobody can fill.
        PlacedLine line = placed.Lines.ShouldHaveSingleItem();
        line.ProductId.ShouldBe(Product.Value);
        line.Quantity.ShouldBe(2);
        line.UnitPrice.ShouldBe(64.99m);
    }

    [Fact]
    public void A_confirmed_order_carries_the_whole_address()
    {
        // Line2 is the field PR-21 added to the contract, and this is the only
        // assertion that it survives the mapping — the gap that made adding it
        // worth doing was that nothing populated the type at all.
        Address address = Address.Of("12 Rue de la Paix", "Appartement 4", "Paris", "75002", "FR");

        IReadOnlyList<object> mapped = Mapper().Map(
            [
                new OrderConfirmedDomainEvent(
                    Order,
                    Customer,
                    PaymentReference.Of("psp-ref-1"),
                    address,
                    Money.Of(129.98m, "EUR"),
                    Lines(),
                    Raised)
            ]);

        OrderConfirmed confirmed = mapped.ShouldHaveSingleItem().ShouldBeOfType<OrderConfirmed>();

        confirmed.ShippingAddress.Line1.ShouldBe("12 Rue de la Paix");
        confirmed.ShippingAddress.Line2.ShouldBe("Appartement 4");
        confirmed.ShippingAddress.City.ShouldBe("Paris");
        confirmed.ShippingAddress.PostCode.ShouldBe("75002");
        confirmed.ShippingAddress.Country.ShouldBe("FR");

        ConfirmedLine line = confirmed.Lines.ShouldHaveSingleItem();
        line.ProductId.ShouldBe(Product.Value);
        line.UnitPrice.ShouldBe(64.99m);

        // The payment reference is NOT on this contract, and its absence is the
        // residual PR-21 named rather than a field this test forgot: Shipping
        // needs the address, and nobody has asked for the reference.
        confirmed.CustomerId.ShouldBe(Customer.Value);
    }

    [Fact]
    public void A_cancellation_carries_the_wire_code_and_not_the_enum_name()
    {
        // The one mapping with a translation in it. CancellationReason's member
        // names are not the contract, and must never become it — a consumer
        // branching on "OutOfStock" would break the day the enum is renamed.
        IReadOnlyList<object> mapped = Mapper().Map(
            [new OrderCancelledDomainEvent(
                Order,
                Customer,
                CancellationReason.PaymentTimeout,
                CancellationOrigin.Workflow,
                Raised)]);

        OrderCancelled cancelled = mapped.ShouldHaveSingleItem().ShouldBeOfType<OrderCancelled>();

        cancelled.Reason.ShouldBe(
            CancelReasons.PaymentTimeout,
            "the saga sends payment_timeout and the published fact has to say the same thing — §13.3 " +
            "tags orders.cancelled with it, and a decline and a silent PSP are a different incident");
        cancelled.Reason.ShouldNotBe(nameof(CancellationReason.PaymentTimeout));
    }

    [Theory]
    [InlineData(CancellationOrigin.User, CancelOrigins.User)]
    [InlineData(CancellationOrigin.Workflow, CancelOrigins.Workflow)]
    public void A_cancellation_carries_its_origin_onto_the_wire(
        CancellationOrigin origin,
        string expected)
    {
        // #123's discriminator. The saga reads this field and nothing else to
        // decide whether a cancellation with no instance is its own echo, so a
        // mapper that dropped it would reinstate the silent discard with every
        // other test still green — the field is optional on the contract, so
        // absent is a legal shape rather than a deserialisation failure.
        IReadOnlyList<object> mapped = Mapper().Map(
            [
                new OrderCancelledDomainEvent(
                    Order,
                    Customer,
                    CancellationReason.CustomerRequest,
                    origin,
                    Raised)
            ]);

        OrderCancelled cancelled = mapped.ShouldHaveSingleItem().ShouldBeOfType<OrderCancelled>();

        cancelled.Origin.ShouldBe(expected);
        cancelled.Origin.ShouldNotBe(nameof(CancellationOrigin.Workflow));
    }

    [Fact]
    public void The_two_local_events_reach_no_contract()
    {
        // §3.2's Publishes column is three facts, and the allow-list is what
        // makes that structural. OrderStockConfirmed is internal bookkeeping;
        // OrderShipped is *Shipping's* fact, and republishing it under
        // Ordering's name would put one event on the bus with two owners.
        IReadOnlyList<object> mapped = Mapper().Map(
            [
                new OrderStockConfirmedDomainEvent(Order, Money.Of(129.98m, "EUR"), Raised),
                new OrderShippedDomainEvent(Order, Customer, TrackingNumber.Of("TRACK-1"), Raised)
            ]);

        mapped.ShouldBeEmpty("an unregistered domain event is local-only, and that is not an error");
    }

    [Fact]
    public void Every_mapped_event_mints_its_own_message_id()
    {
        // Two events in one commit must not share an id: it is the outbox row's
        // key, the broker header and the inbox's dedupe key all at once (§9.1),
        // so a shared value would make the second look like a duplicate of the
        // first and be dropped.
        IReadOnlyList<object> mapped = Mapper().Map(
            [
                new OrderPlacedDomainEvent(Order, Customer, Money.Of(129.98m, "EUR"), Lines(), Raised),
                new OrderCancelledDomainEvent(
                    Order,
                    Customer,
                    CancellationReason.OutOfStock,
                    CancellationOrigin.Workflow,
                    Raised)
            ]);

        mapped.Count.ShouldBe(2);

        Guid[] ids = [.. mapped.Cast<IIntegrationEvent>().Select(e => e.MessageId)];

        ids.ShouldBeUnique();
        ids.ShouldAllBe(id => id != Guid.Empty);
    }
}
