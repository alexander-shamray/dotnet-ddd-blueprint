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
