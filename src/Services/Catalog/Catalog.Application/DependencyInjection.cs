using Catalog.Application.Integration;
using Catalog.Application.Products.PublishProduct;
using Common.Application;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Catalog.Application;

/// <summary>
/// The one registration method this layer exposes (§4.2). Also the assembly's
/// <c>typeof</c> anchor for the architecture gates, per §4.1's shape — the
/// layer that has a <c>DependencyInjection.cs</c> needs no separate marker.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddCatalogApplication(this IServiceCollection services)
    {
        services.AddPluggableFrom(typeof(DependencyInjection).Assembly);   // §6.2
        services.AddDispatcher();

        // Explicit rather than scanned, beside the dispatcher it serves —
        // §4.2's registration sample is the shape. §7.5's real dispatcher
        // since PR-14; the NullDomainEventDispatcher that dropped every
        // ProductPublishedDomainEvent between PR-10 and here is deleted, not
        // disabled, so nothing can register it back by accident.
        services.AddDomainEventDispatcher();

        // The allow-list of §9.3, and the one registration that decides what
        // this service publishes. Explicit rather than scanned: a mapper
        // discovered by convention would make "Catalog publishes these three
        // facts" a property of which types happen to be in the assembly.
        services.AddScoped<IIntegrationEventMapper, CatalogIntegrationEventMapper>();

        // The clock (§5.4) and the request histogram (§13.3): LoggingBehavior
        // injects both, and nothing catches an omission before the first
        // dispatched request — not ValidateOnBuild, which never constructs an
        // open generic, and not the host smoke, which never enters the
        // dispatcher. The registration test is what guards these two lines.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<RequestMetrics>();

        // Ordered, explicit, not scanned — registration order is pipeline
        // order (§6.3). Three of four: IdempotencyBehavior joins with the PR
        // that builds it, and slots in between Validation and Transaction.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));

        // §4.2's sample line. IValidator<T> is not in PluggableInterfaces.All
        // because it is FluentValidation's contract, not one of ours — its own
        // scanner knows its own conventions (Include* filters, internal
        // validators) and a second scan would drift from it.
        services.AddValidatorsFromAssemblyContaining<PublishProductValidator>();
        return services;
    }
}
