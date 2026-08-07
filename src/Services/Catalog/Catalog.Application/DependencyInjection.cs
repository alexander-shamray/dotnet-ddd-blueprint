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

        // LoggingBehavior injects it, so a host that omits this line fails
        // ValidateOnBuild rather than the first dispatched request (§13.3).
        services.AddSingleton<RequestMetrics>();

        // Ordered, explicit, not scanned — registration order is pipeline
        // order (§6.3). Two of four: IdempotencyBehavior and
        // TransactionBehavior join with the PRs that build them.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        return services;
    }
}
