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
    public void AddCatalogApplication_registers_the_two_behaviours_in_pipeline_order()
    {
        ServiceCollection services = new();

        services.AddCatalogApplication();

        IEnumerable<Type?> behaviours = services
            .Where(d => d.ServiceType == typeof(IPipelineBehavior<,>))
            .Select(d => d.ImplementationType);

        behaviours.ShouldBe(
            [
                typeof(LoggingBehavior<,>),
                typeof(ValidationBehavior<,>)
            ],
            "two of four — IdempotencyBehavior and TransactionBehavior join with their PRs (§6.3)");
    }
}
