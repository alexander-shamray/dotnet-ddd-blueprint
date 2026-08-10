using Catalog.Domain.Common;
using Catalog.Domain.Products;
using Common.Application;
using Common.Contracts.Catalog.V1;
using Common.Domain;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Application.Tests;

/// <summary>
/// §9.3's allow-list. Resolved through <c>AddCatalogApplication</c> rather
/// than constructed, because the mapper is internal and because the thing
/// worth proving is the registered one — an allow-list nobody resolves
/// publishes nothing while every assertion about it passes.
/// </summary>
public class CatalogIntegrationEventMapperTests
{
    private static readonly DateTimeOffset Raised = new(2026, 8, 11, 2, 26, 0, TimeSpan.Zero);

    private static IIntegrationEventMapper Mapper()
    {
        ServiceCollection services = new();
        services.AddCatalogApplication();

        // Scoped, so it comes out of a scope. The provider outlives this call
        // by design — the mapper holds no state and no test disposes it.
        return services
            .BuildServiceProvider()
            .CreateScope()
            .ServiceProvider
            .GetRequiredService<IIntegrationEventMapper>();
    }

    [Fact]
    public void The_registered_domain_event_becomes_its_contract()
    {
        var productId = ProductId.New();

        IReadOnlyList<object> mapped = Mapper().Map(
            [new ProductPublishedDomainEvent(productId, "Walnut desk", "https://cdn.example/d.jpg",
                Money.Of(19.99m, "eur"), Raised)]);

        ProductPublished contract = mapped.ShouldHaveSingleItem().ShouldBeOfType<ProductPublished>();

        contract.ProductId.ShouldBe(productId.Value);
        contract.Name.ShouldBe("Walnut desk");
        contract.ThumbnailUrl.ShouldBe("https://cdn.example/d.jpg");
        contract.OccurredAt.ShouldBe(Raised);

        // Money decomposed into its two halves, normalised by the domain on
        // the way in. A contract may not carry a domain type (§9.1), and this
        // is where that decomposition is allowed to happen.
        contract.Amount.ShouldBe(19.99m);
        contract.Currency.ShouldBe("EUR");
    }

    [Fact]
    public void The_correlation_is_the_product_and_the_message_id_is_minted()
    {
        var productId = ProductId.New();

        ProductPublished contract = Mapper()
            .Map([new ProductPublishedDomainEvent(productId, "Desk", null, Money.Of(1m, "EUR"), Raised)])
            .ShouldHaveSingleItem()
            .ShouldBeOfType<ProductPublished>();

        // A business correlation, not an ambient request id — §9.3 sets it
        // from the aggregate because that is what a support tool follows
        // across services.
        contract.CorrelationId.ShouldBe(productId.Value);
        contract.MessageId.ShouldNotBe(Guid.Empty);
        contract.MessageId.ShouldNotBe(contract.CorrelationId);
    }

    [Fact]
    public void An_unregistered_domain_event_is_skipped_and_is_not_an_error()
    {
        // §9.3's first row. Most domain events are internal, and failing on
        // them would force every new event to be published or explicitly
        // suppressed — which is the pressure that gets an internal fact
        // published by accident.
        Mapper().Map([new NotPublished(Raised)]).ShouldBeEmpty();
    }

    private sealed record NotPublished(DateTimeOffset OccurredAt) : IDomainEvent;
}
