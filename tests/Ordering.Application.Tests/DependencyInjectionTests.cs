using Common.Application;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Application.Orders.CancelOrder;
using Ordering.Application.Orders.PlaceOrder;
using Shouldly;
using Xunit;

namespace Ordering.Application.Tests;

/// <summary>
/// The registration surface of <c>AddOrderingApplication</c>, asserted on the
/// collection rather than a built provider: registration order is pipeline
/// order (§6.3), and only the descriptor list still shows it.
/// </summary>
public class DependencyInjectionTests
{
    [Fact]
    public void AddOrderingApplication_registers_the_dispatcher_scoped()
    {
        ServiceCollection services = new();

        services.AddOrderingApplication();

        ServiceDescriptor dispatcher = services
            .Where(d => d.ServiceType == typeof(IDispatcher))
            .ShouldHaveSingleItem();
        dispatcher.Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddOrderingApplication_registers_the_system_clock()
    {
        // LoggingBehavior injects TimeProvider, and neither ValidateOnBuild
        // nor the host smoke can see the hole: an open generic is not
        // constructed until a closed IPipelineBehavior<,> resolves, which
        // nothing does before the first dispatched request (§4.2, §5.4).
        ServiceCollection services = new();

        services.AddOrderingApplication();

        ServiceDescriptor clock = services
            .Where(d => d.ServiceType == typeof(TimeProvider))
            .ShouldHaveSingleItem();
        clock.Lifetime.ShouldBe(ServiceLifetime.Singleton);
        clock.ImplementationInstance.ShouldBeSameAs(TimeProvider.System);
    }

    [Fact]
    public void AddOrderingApplication_registers_the_request_metrics_singleton()
    {
        // The clock test's twin, for the same reason: LoggingBehavior injects
        // RequestMetrics, and neither ValidateOnBuild nor the host smoke can
        // see the omission before the first dispatched request.
        ServiceCollection services = new();

        services.AddOrderingApplication();

        ServiceDescriptor metrics = services
            .Where(d => d.ServiceType == typeof(RequestMetrics))
            .ShouldHaveSingleItem();
        metrics.Lifetime.ShouldBe(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddOrderingApplication_registers_the_real_domain_event_dispatcher_scoped()
    {
        // §4.2 registers IDomainEventDispatcher in Application, beside
        // AddDispatcher. Without it the first resolved TransactionBehavior
        // throws, and nothing resolves one before the first dispatched
        // command.
        ServiceCollection services = new();

        services.AddOrderingApplication();

        ServiceDescriptor dispatcher = services
            .Where(d => d.ServiceType == typeof(IDomainEventDispatcher))
            .ShouldHaveSingleItem();
        dispatcher.Lifetime.ShouldBe(ServiceLifetime.Scoped);

        // Named, not merely counted. The null object this replaced satisfied
        // every other assertion in this test while dropping every domain event
        // the aggregate raised, which is exactly the failure a shape-only
        // check cannot see.
        dispatcher.ImplementationType!.Name.ShouldBe("DomainEventDispatcher");
    }

    [Fact]
    public void AddOrderingApplication_registers_the_projection_registry_scoped()
    {
        // Scoped, not singleton: the registry resolves scoped handlers, and
        // GetServices for a scoped service from the root provider throws
        // (§7.5). Its memo is the singleton beside it, keyed to the container.
        ServiceCollection services = new();

        services.AddOrderingApplication();

        services
            .Where(d => d.ServiceType == typeof(IProjectionRegistry))
            .ShouldHaveSingleItem()
            .Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddOrderingApplication_registers_the_allow_list_mapper()
    {
        // The one registration that decides what Ordering publishes (§9.3).
        // Explicit rather than scanned, so "Ordering publishes these facts" is
        // not a property of which types happen to be in the assembly.
        ServiceCollection services = new();

        services.AddOrderingApplication();

        services
            .Where(d => d.ServiceType == typeof(IIntegrationEventMapper))
            .ShouldHaveSingleItem()
            .ImplementationType!.Name.ShouldBe("OrderingIntegrationEventMapper");
    }

    [Fact]
    public void AddOrderingApplication_registers_the_four_behaviours_in_pipeline_order()
    {
        ServiceCollection services = new();

        services.AddOrderingApplication();

        IEnumerable<Type?> behaviours = services
            .Where(d => d.ServiceType == typeof(IPipelineBehavior<,>))
            .Select(d => d.ImplementationType);

        behaviours.ShouldBe(
            [
                typeof(LoggingBehavior<,>),
                typeof(ValidationBehavior<,>),
                typeof(IdempotencyBehavior<,>),
                typeof(TransactionBehavior<,>)
            ],
            "all four, in pipeline order (§6.3) — idempotency claims its key inside validation, " +
            "so a malformed command is refused without burning one, and outside the transaction, " +
            "so the claim is held before any work starts");
    }

    [Fact]
    public void The_handler_scan_registered_both_command_handlers()
    {
        // §6.2's scan fails silently when it stops finding things: nothing
        // resolves an open generic at build time, so ValidateOnBuild says
        // nothing and the dispatcher throws on the first request that needs
        // the handler — in production, on the path that matters.
        //
        // This is not hypothetical here. Both handlers were written
        // `internal sealed`, copying §11.4's printed sample, and Scrutor's
        // AddClasses registers public classes only — so both were skipped in
        // silence and every cancellation answered 500. This test is what makes
        // that a build failure next time.
        ServiceCollection services = new();
        services.AddOrderingApplication();

        Type[] handlers =
        [
            .. services
                .Where(d => d.ServiceType.IsGenericType &&
                    d.ServiceType.GetGenericTypeDefinition() == typeof(ICommandHandler<,>))
                .Select(d => d.ImplementationType!)
        ];

        handlers.ShouldContain(typeof(PlaceOrderHandler));
        handlers.ShouldContain(typeof(CancelOrderHandler));
    }

    [Fact]
    public void The_validator_scan_found_the_place_order_validator()
    {
        // The other silent scan. ValidationBehavior takes
        // IEnumerable<IValidator<T>>, so a lost scan is a pipeline that
        // validates nothing and reports success — an empty enumerable is a
        // valid answer to "which validators apply", and it is the same answer
        // a correctly configured query gives.
        ServiceCollection services = new();
        services.AddOrderingApplication();

        services
            .Where(d => d.ServiceType == typeof(IValidator<PlaceOrderCommand>))
            .Select(d => d.ImplementationType)
            .ShouldContain(typeof(PlaceOrderValidator));
    }
}
