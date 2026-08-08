using Common.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Catalog.Infrastructure;

/// <summary>
/// The one registration method this layer exposes (§4.2), and the assembly's
/// <c>typeof</c> anchor. No <c>IConfiguration</c> parameter yet — §4.2's
/// worked example takes one to read connection strings, and this PR has none
/// to read. PR-08 adds the parameter alongside the first line that uses it;
/// an unused parameter is the same untruth as an unused <c>using</c>.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddCatalogInfrastructure(this IServiceCollection services)
    {
        // Each layer scans itself (§6.2): projections, cache invalidators and
        // command mappers will live here, and scanning only Application would
        // skip them all. Finding nothing yet is the truthful state.
        services.AddPluggableFrom(typeof(DependencyInjection).Assembly);
        return services;
    }
}
