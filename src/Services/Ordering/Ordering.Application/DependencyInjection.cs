using Ordering.Application.Integration;
using Common.Application;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Ordering.Application;

/// <summary>
/// The one registration method this layer exposes (§4.2). Also the assembly's
/// <c>typeof</c> anchor for the architecture gates, per §4.1's shape — the
/// layer that has a <c>DependencyInjection.cs</c> needs no separate marker.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddOrderingApplication(this IServiceCollection services)
    {
        services.AddPluggableFrom(typeof(DependencyInjection).Assembly);   // §6.2
        services.AddDispatcher();

        // Explicit rather than scanned, beside the dispatcher it serves —
        // §4.2's registration sample is the shape. It stages nothing until
        // this service has an aggregate raising domain events, and needs no
        // null object to say so: a collector over an empty change tracker
        // returns nothing and the dispatcher exits early (§7.5).
        services.AddDomainEventDispatcher();

        // The allow-list of §9.3, and the one registration that decides what
        // this service publishes. Explicit rather than scanned: a mapper
        // discovered by convention would make "Ordering publishes these three
        // facts" a property of which types happen to be in the assembly.
        services.AddScoped<IIntegrationEventMapper, OrderingIntegrationEventMapper>();

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
        // §4.2's line spelt over the assembly rather than over a type in
        // it, because there is no validator yet to name — and this class,
        // the obvious anchor, is static and cannot be a type argument.
        // Move to AddValidatorsFromAssemblyContaining<TFirstValidator>()
        // with the first one, and add the registration test that guards
        // it: ValidationBehavior takes IEnumerable<IValidator<T>>, so a
        // lost scan is a pipeline that validates nothing and says so to
        // nobody.
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);
        return services;
    }
}
