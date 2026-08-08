using Common.Application;
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
        return services;
    }
}
