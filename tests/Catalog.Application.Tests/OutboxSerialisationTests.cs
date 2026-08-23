using System.Text.Json;
using Catalog.Domain.Common;
using Catalog.Domain.Products;
using Catalog.Infrastructure;
using Common.Infrastructure.Outbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Application.Tests;

/// <summary>
/// The <c>Local</c> lane's payload contract (§9.4). No containers, and it
/// lives here rather than in §12.6's contract suite because the set it
/// iterates comes from the <see cref="MessageTypeMap"/> and that suite selects
/// on the contracts namespace, which no domain event is in.
/// </summary>
/// <remarks>
/// Both halves come out of the real <c>AddCatalogInfrastructure</c>, and that
/// is the whole design of this test. A hand-built <see cref="OutboxJson"/>
/// listing <c>MoneyJsonConverter</c> would assert that the converter works —
/// which nobody doubts — and stay green if the registration were deleted,
/// while the running host wrote a price of zero into every row. Registration
/// is the thing that can silently go missing, so registration is what this
/// resolves.
/// <para>
/// It needs no database for that: <c>AddCatalogInfrastructure</c> only
/// registers, so the connection strings below are shapes rather than
/// addresses, and nothing here opens one.
/// </para>
/// </remarks>
public class OutboxSerialisationTests
{
    private static readonly DateTimeOffset Raised = new(2026, 8, 11, 2, 26, 0, TimeSpan.Zero);

    [Fact]
    public void Every_stageable_domain_event_round_trips_through_the_outbox_options()
    {
        // Not "every IDomainEvent": the map is the set the outbox can actually
        // carry, and a type it does not know cannot reach a payload column.
        //
        // This is the assertion that caught Money. A readonly record struct
        // with a private constructor and two get-only properties does not
        // throw here — System.Text.Json builds default(Money), finds nothing
        // to set, and returns Amount 0 with a null Currency. The failure mode
        // is a projection running on a price of zero, on the day a deploy
        // lands mid-batch, with nothing in any log to say so.
        using ServiceProvider provider = Registered();
        JsonSerializerOptions options = provider.GetRequiredService<OutboxJson>().Options;

        foreach (Type type in provider.GetRequiredService<MessageTypeMap>().StageableDomainEvents)
        {
            object sample = DomainEventSamples.Create(type);
            string json = JsonSerializer.Serialize(sample, type, options);

            JsonSerializer
                .Deserialize(json, type, options)
                .ShouldBe(sample, $"{type.Name} cannot survive the Local lane");
        }
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
    public void There_is_a_stageable_domain_event_to_round_trip()
    {
        // The loop above is vacuous if the map is empty, and it would be
        // vacuous quietly — a registration that stopped naming Catalog.Domain
        // would turn the assertion into a no-op and nothing else would say so.
        using ServiceProvider provider = Registered();

        provider
            .GetRequiredService<MessageTypeMap>()
            .StageableDomainEvents
            .ShouldContain(typeof(ProductPublishedDomainEvent));
    }

    private static ServiceProvider Registered()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Catalog"] = "Server=none;Database=Catalog;",
                ["ConnectionStrings:RabbitMq"] = "amqp://none",
                // AddRedisConnections reads both eagerly and throws naming the
                // missing one, so the two lines below are what let
                // AddCatalogInfrastructure run at all — the same reason the
                // bus key above is here. Nothing resolves a multiplexer in
                // this suite: the keyed registrations are factories, and no
                // test asks for one.
                ["ConnectionStrings:RedisCache"] = "redis.invalid:6379",
                ["ConnectionStrings:RedisCoordination"] = "redis.invalid:6380"
            })
            .Build();

        ServiceCollection services = new();
        services.AddCatalogInfrastructure(configuration);
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
        private static readonly Dictionary<Type, object> Samples = new()
        {
            [typeof(ProductPublishedDomainEvent)] = new ProductPublishedDomainEvent(
                ProductId.New(),
                "Walnut desk",
                "https://cdn.example/desk.jpg",
                Money.Of(19.99m, "EUR"),
                Raised)
        };

        public static object Create(Type type) =>
            Samples.TryGetValue(type, out object? sample) ? sample
                : throw new InvalidOperationException(
                    $"{type.Name} is stageable on the Local lane but has no sample here. Add one — " +
                    "the round-trip assertion is what stops a member rename from deserialising to " +
                    "its default in production (§9.4).");
    }
}
