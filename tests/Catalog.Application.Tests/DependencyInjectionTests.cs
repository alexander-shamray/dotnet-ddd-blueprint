using Catalog.Application.Products.GetProducts;
using Catalog.Application.Products.PublishProduct;
using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Application.Tests;

/// <summary>
/// The registration surface of <c>AddCatalogApplication</c>, asserted on the
/// collection rather than a built provider: registration order is pipeline
/// order (§6.3), and only the descriptor list still shows it.
/// </summary>
public class DependencyInjectionTests
{
    [Fact]
    public void AddCatalogApplication_registers_the_dispatcher_scoped()
    {
        ServiceCollection services = new();

        services.AddCatalogApplication();

        ServiceDescriptor dispatcher = services
            .Where(d => d.ServiceType == typeof(IDispatcher))
            .ShouldHaveSingleItem();
        dispatcher.Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddCatalogApplication_registers_the_system_clock()
    {
        // LoggingBehavior injects TimeProvider, and neither ValidateOnBuild
        // nor the host smoke can see the hole: an open generic is not
        // constructed until a closed IPipelineBehavior<,> resolves, which
        // nothing does before the first dispatched request (§4.2, §5.4).
        ServiceCollection services = new();

        services.AddCatalogApplication();

        ServiceDescriptor clock = services
            .Where(d => d.ServiceType == typeof(TimeProvider))
            .ShouldHaveSingleItem();
        clock.Lifetime.ShouldBe(ServiceLifetime.Singleton);
        clock.ImplementationInstance.ShouldBeSameAs(TimeProvider.System);
    }

    [Fact]
    public void AddCatalogApplication_registers_the_request_metrics_singleton()
    {
        // The clock test's twin, for the same reason: LoggingBehavior injects
        // RequestMetrics, and neither ValidateOnBuild nor the host smoke can
        // see the omission before the first dispatched request.
        ServiceCollection services = new();

        services.AddCatalogApplication();

        ServiceDescriptor metrics = services
            .Where(d => d.ServiceType == typeof(RequestMetrics))
            .ShouldHaveSingleItem();
        metrics.Lifetime.ShouldBe(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddCatalogApplication_registers_the_real_domain_event_dispatcher_scoped()
    {
        // §4.2 registers IDomainEventDispatcher in Application, beside
        // AddDispatcher. Without it the first resolved TransactionBehavior
        // throws, and nothing resolves one before the first dispatched
        // command.
        ServiceCollection services = new();

        services.AddCatalogApplication();

        ServiceDescriptor dispatcher = services
            .Where(d => d.ServiceType == typeof(IDomainEventDispatcher))
            .ShouldHaveSingleItem();
        dispatcher.Lifetime.ShouldBe(ServiceLifetime.Scoped);

        // Named, not merely counted. The NullDomainEventDispatcher this
        // replaced satisfied every other assertion in this test while
        // dropping every event Product.Publish raised, which is exactly the
        // failure a shape-only check cannot see.
        dispatcher.ImplementationType!.Name.ShouldBe("DomainEventDispatcher");
    }

    [Fact]
    public void AddCatalogApplication_registers_the_projection_registry_scoped()
    {
        // Scoped, not singleton: the registry resolves scoped handlers, and
        // GetServices for a scoped service from the root provider throws
        // (§7.5). Its memo is the singleton beside it, keyed to the container.
        ServiceCollection services = new();

        services.AddCatalogApplication();

        services
            .Where(d => d.ServiceType == typeof(IProjectionRegistry))
            .ShouldHaveSingleItem()
            .Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddCatalogApplication_registers_the_allow_list_mapper()
    {
        // The one registration that decides what Catalog publishes (§9.3).
        // Explicit rather than scanned, so "Catalog publishes these facts" is
        // not a property of which types happen to be in the assembly.
        ServiceCollection services = new();

        services.AddCatalogApplication();

        services
            .Where(d => d.ServiceType == typeof(IIntegrationEventMapper))
            .ShouldHaveSingleItem()
            .ImplementationType!.Name.ShouldBe("CatalogIntegrationEventMapper");
    }

    [Fact]
    public void AddCatalogApplication_registers_the_three_behaviours_in_pipeline_order()
    {
        ServiceCollection services = new();

        services.AddCatalogApplication();

        IEnumerable<Type?> behaviours = services
            .Where(d => d.ServiceType == typeof(IPipelineBehavior<,>))
            .Select(d => d.ImplementationType);

        behaviours.ShouldBe(
            [
                typeof(LoggingBehavior<,>),
                typeof(ValidationBehavior<,>),
                typeof(TransactionBehavior<,>)
            ],
            "three of four — IdempotencyBehavior joins with its PR, between Validation and Transaction (§6.3)");
    }

    [Fact]
    public void AddCatalogApplication_registers_the_command_validator()
    {
        // ValidationBehavior takes IEnumerable<IValidator<T>>, so a missing
        // scan is not a failure — it is a pipeline that validates nothing and
        // says so to nobody. The registration is the only place to catch it.
        ServiceCollection services = new();

        services.AddCatalogApplication();

        services.ShouldContain(
            d => d.ServiceType == typeof(FluentValidation.IValidator<PublishProductCommand>),
            "AddValidatorsFromAssemblyContaining is §4.2's line, and losing it fails silently");
    }

    [Fact]
    public void AddCatalogApplication_registers_the_slice_handlers()
    {
        // The §6.2 scan found nothing until this PR; these two are the first
        // real registrations it produces, so the scan itself is now testable.
        ServiceCollection services = new();

        services.AddCatalogApplication();

        services.ShouldContain(d =>
            d.ServiceType == typeof(ICommandHandler<PublishProductCommand, Result<Guid>>));
        services.ShouldContain(d =>
            d.ServiceType == typeof(IQueryHandler<GetProductsQuery, CursorPage<ProductSummaryDto>>));
    }
}
