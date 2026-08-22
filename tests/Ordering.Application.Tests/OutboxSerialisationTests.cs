using System.Text.Json;
using Common.Infrastructure.Outbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Domain.Common;
using Ordering.Domain.Orders;
using Ordering.Domain.Orders.Events;
using Ordering.Infrastructure;
using Shouldly;
using Xunit;

namespace Ordering.Application.Tests;

/// <summary>
/// The <c>Local</c> lane's payload contract (§9.4). No containers, and it
/// lives here rather than in §12.6's contract suite because the set it
/// iterates comes from the <see cref="MessageTypeMap"/> and that suite selects
/// on the contracts namespace, which no domain event is in.
/// </summary>
/// <remarks>
/// Both halves come out of the real <c>AddOrderingInfrastructure</c>, and that
/// is the whole design of this test. A hand-built <see cref="OutboxJson"/>
/// listing the four converters would assert that they work — which nobody
/// doubts — and stay green if a registration were deleted, while the running
/// host wrote a null currency into every row. Registration is the thing that
/// can silently go missing, so registration is what this resolves.
/// </remarks>
public class OutboxSerialisationTests
{
    private static readonly DateTimeOffset Raised = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Every_stageable_domain_event_round_trips_through_the_outbox_options()
    {
        // Not "every IDomainEvent": the map is the set the outbox can actually
        // carry, and a type it does not know cannot reach a payload column.
        //
        // Four value objects ride on these five events and every one has a
        // private constructor. Three are structs, so System.Text.Json builds
        // the default, finds nothing to set and hands back a zero amount or a
        // null reference without throwing — the failure this loop exists to
        // make loud.
        using ServiceProvider provider = Registered();
        JsonSerializerOptions options = provider.GetRequiredService<OutboxJson>().Options;

        foreach (Type type in provider.GetRequiredService<MessageTypeMap>().StageableDomainEvents)
        {
            object sample = DomainEventSamples.Create(type);
            string json = JsonSerializer.Serialize(sample, type, options);
            object? read = JsonSerializer.Deserialize(json, type, options);

            // Compared through the payload rather than with ShouldBe(sample),
            // and this is not a weakened assertion — it is the only one that
            // can be made here. Three of these events carry an
            // IReadOnlyList<OrderLineSnapshot>, and a record's generated
            // equality compares that member with
            // EqualityComparer<IReadOnlyList<T>>.Default, which is reference
            // equality: a perfectly round-tripped list is a different instance
            // and never equal. Catalog's one domain event carries no
            // collection, which is why the test this was copied from asserts
            // on the object and has never met the case.
            //
            // Re-serialising catches everything the object comparison would
            // have: a Money that came back as default writes {"Amount":0,
            // "Currency":null} and no longer matches. The gap it cannot see is
            // a member dropped on write *and* on read together — which the
            // hand-written converters make unreachable, because each validates
            // on read that the members it writes were present, and a deleted
            // Write line therefore throws rather than round-tripping quietly.
            JsonSerializer.Serialize(read, type, options)
                .ShouldBe(json, $"{type.Name} cannot survive the Local lane");
        }
    }

    [Fact]
    public void All_five_domain_events_are_stageable()
    {
        // The loop above is vacuous if the map is empty, and it would be
        // vacuous quietly — a registration that stopped naming Ordering.Domain
        // would turn the assertion into a no-op and nothing else would say so.
        // Naming all five rather than one also makes an added event a decision:
        // it fails here until it has a sample.
        using ServiceProvider provider = Registered();

        provider.GetRequiredService<MessageTypeMap>().StageableDomainEvents.ShouldBe(
            [
                typeof(OrderPlacedDomainEvent),
                typeof(OrderStockConfirmedDomainEvent),
                typeof(OrderConfirmedDomainEvent),
                typeof(OrderShippedDomainEvent),
                typeof(OrderCancelledDomainEvent)
            ],
            ignoreOrder: true);
    }

    [Theory]
    [InlineData("""{"Amount":19.99,"Currency":"EUR","Note":{"Amount":1,"Currency":"USD"}}""")]
    [InlineData("""{"Note":{"Amount":1,"Currency":"USD"},"Amount":19.99,"Currency":"EUR"}""")]
    [InlineData("""{"Amount":19.99,"Note":[{"Amount":1}],"Currency":"EUR"}""")]
    public void A_money_payload_ignores_members_a_later_version_added(string json)
    {
        // §9.2 makes an added member an ordinary, backward-compatible change,
        // so a payload written by a later deployment is a thing this converter
        // will meet — a row staged before a rollback is the cheapest way to
        // get one.
        //
        // Reading it must skip the whole unknown value. Reading only its first
        // token leaves the reader inside the nested object: its Amount is
        // taken for this one and its EndObject ends the loop, so the money
        // deserialises to the wrong number without anything throwing. All
        // three orderings, because the defect only bites when the unknown
        // member sits before the one it shadows.
        using ServiceProvider provider = Registered();

        JsonSerializer
            .Deserialize<Money>(json, provider.GetRequiredService<OutboxJson>().Options)
            .ShouldBe(Money.Of(19.99m, "EUR"));
    }

    [Fact]
    public void An_address_payload_keeps_an_absent_second_line_absent()
    {
        // The optional member is the one a converter gets wrong in the quiet
        // direction: reading a missing Line2 as a failure would reject every
        // single-line address, and writing it as absent rather than null makes
        // "no second line" and "member added later" the same payload.
        using ServiceProvider provider = Registered();
        JsonSerializerOptions options = provider.GetRequiredService<OutboxJson>().Options;

        Address address = Address.Of("1 Test Street", null, "Almaty", "050000", "KZ");

        JsonSerializer
            .Deserialize<Address>(JsonSerializer.Serialize(address, options), options)
            .ShouldBe(address);
    }

    private static ServiceProvider Registered()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Ordering"] = "Server=none;Database=Ordering;",
                ["ConnectionStrings:RabbitMq"] = "amqp://none",
                // AddRedisConnections reads both eagerly and throws naming the
                // missing one, so the two lines below are what let
                // AddOrderingInfrastructure run at all — the same reason the
                // bus key above is here. Nothing resolves a multiplexer in
                // this suite: the keyed registrations are factories, and no
                // test asks for one.
                ["ConnectionStrings:RedisCache"] = "redis.invalid:6379",
                ["ConnectionStrings:RedisCoordination"] = "redis.invalid:6380"
            })
            .Build();

        ServiceCollection services = new();
        services.AddOrderingInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// A deliberate obstacle, the same one <c>ContractSamples</c> is in §12.6:
    /// a new domain event with no sample fails here instead of being skipped,
    /// which is the failure mode of every loop over types that falls back to
    /// <c>Activator.CreateInstance</c> — a parameterless record would produce
    /// an all-default instance that round-trips perfectly and proves nothing.
    /// </summary>
    private static class DomainEventSamples
    {
        private static readonly OrderId Order = OrderId.New();
        private static readonly CustomerId Customer = new(Guid.CreateVersion7());
        private static readonly Money Total = Money.Of(19.99m, "EUR");

        private static readonly IReadOnlyList<OrderLineSnapshot> Lines =
            [new OrderLineSnapshot(ProductId.New(), 2, Money.Of(9.995m, "EUR"))];

        private static readonly Dictionary<Type, object> Samples = new()
        {
            [typeof(OrderPlacedDomainEvent)] =
                new OrderPlacedDomainEvent(Order, Customer, Total, Lines, Raised),
            [typeof(OrderStockConfirmedDomainEvent)] =
                new OrderStockConfirmedDomainEvent(Order, Total, Raised),
            [typeof(OrderConfirmedDomainEvent)] = new OrderConfirmedDomainEvent(
                Order,
                Customer,
                PaymentReference.Of("pay_123"),
                Address.Of("1 Test Street", "Flat 4", "Almaty", "050000", "KZ"),
                Total,
                Lines,
                Raised),
            [typeof(OrderShippedDomainEvent)] =
                new OrderShippedDomainEvent(Order, Customer, TrackingNumber.Of("TRK-1"), Raised),
            [typeof(OrderCancelledDomainEvent)] =
                new OrderCancelledDomainEvent(Order, Customer, CancellationReason.CustomerRequest, Raised)
        };

        public static object Create(Type type) =>
            Samples.TryGetValue(type, out object? sample) ? sample
                : throw new InvalidOperationException(
                    $"{type.Name} is stageable on the Local lane but has no sample here. Add one — " +
                    "the round-trip assertion is what stops a member rename from deserialising to " +
                    "its default in production (§9.4).");
    }
}
