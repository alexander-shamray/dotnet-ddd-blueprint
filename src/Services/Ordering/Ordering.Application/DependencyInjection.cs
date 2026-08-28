using Ordering.Application.Integration;
using Ordering.Application.Orders.PlaceOrder;
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
        // §4.2's registration sample is the shape. Order raises five domain
        // events and, since PR-21, the mapper's allow-list carries three of
        // them — so the Broker lane finally stages rows and this service
        // publishes §3.2's whole Publishes column.
        //
        // The Local lane is still empty, and that is now the only half worth
        // keeping straight: no IProjectionHandler is registered (§7.5),
        // because §6.6's OrderSummaries is not built. A decision about what
        // this service projects, not a consequence of having nothing to
        // project — and the two lanes being empty for two different reasons
        // is why the sentence was worth writing before either was filled.
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
        // order (§6.3), and all four seats are filled since the PR that built
        // §8.5's behaviour.
        //
        // Idempotency sits INSIDE validation and OUTSIDE the transaction, and
        // both neighbours are load-bearing. Inside validation, because a
        // malformed command must be refused without claiming a key — a 400
        // that burned the caller's CommandId for 24 hours would make a typo
        // unretryable. Outside the transaction, because the claim has to be
        // held before any work starts, and a claim taken inside the
        // transaction would be released by a rollback it knows nothing about.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(IdempotencyBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));

        // The key one of those two behaviours builds and the other one writes,
        // and it is scoped because a command is. Registered beside them rather
        // than in Common.Application's own AddDispatcher, because these lines
        // are where a reader looks to find out what the pipeline is made of —
        // and because a service that omits it does not fail at startup:
        // ValidateOnBuild never constructs an open generic, so the miss would
        // surface as a TransactionBehavior that cannot be resolved on the first
        // dispatched command. A registration test is what guards this line.
        services.AddScoped<IdempotencyContext>();

        // §4.2's sample line. IValidator<T> is not in PluggableInterfaces.All
        // because it is FluentValidation's contract, not one of ours — its own
        // scanner knows its own conventions (Include* filters, internal
        // validators) and a second scan would drift from it.
        // Anchored on the first validator rather than on the assembly, which
        // is the move the scaffold's comment asked for the moment one
        // existed. The difference is what happens when the last validator
        // leaves: over an assembly the scan keeps succeeding and validates
        // nothing, while over a type the file stops compiling. A lost scan is
        // otherwise silent — ValidationBehavior takes IEnumerable<IValidator<T>>,
        // and an empty enumerable is a valid answer.
        services.AddValidatorsFromAssemblyContaining<PlaceOrderValidator>();
        return services;
    }
}
