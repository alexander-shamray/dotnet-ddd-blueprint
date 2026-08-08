using Catalog.Infrastructure.Persistence;
using Common.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Catalog.Infrastructure;

/// <summary>
/// The one registration method this layer exposes (§4.2), and the assembly's
/// <c>typeof</c> anchor. The <c>IConfiguration</c> parameter arrives with PR-08
/// because PR-08 is the first thing that reads one — an unused parameter is the
/// same untruth as an unused <c>using</c>.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddCatalogInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // The RUNTIME identity of §7.1 — data plane only, no DDL. The migrator
        // key is deliberately unreadable from here: two connection strings that
        // any host may read are a naming convention, not a boundary.
        //
        // EnableRetryOnFailure is not decoration. §6.3 requires
        // CreateExecutionStrategy around every user-initiated transaction, and
        // with no retry configured the strategy is a no-op that quietly stops
        // proving anything.
        services.AddDbContext<CatalogDbContext>(o =>
            o.UseSqlServer(
                configuration.GetConnectionString("Catalog"),
                sql => sql.EnableRetryOnFailure()));

        // Each layer scans itself (§6.2): projections, cache invalidators and
        // command mappers will live here, and scanning only Application would
        // skip them all. Finding nothing yet is the truthful state.
        services.AddPluggableFrom(typeof(DependencyInjection).Assembly);

        services.AddScoped<IUnitOfWork, EfUnitOfWork>();                     // §6.3

        // No IDbConnectionFactory: §4.2's sample registers one and §6.5 says
        // what it is for — Dapper against read models — and Catalog has no
        // query until PR-10. A registration nothing injects is an unused
        // project reference in container form.

        // Readiness lives here, not in Common.Web, because it needs the
        // connection string the shared host package does not have (§13.5).
        // Until this line Catalog reported ready immediately, which §13.5 says
        // is indistinguishable from readiness never having been wired up — true
        // then, because there was no connection string, and false from here.
        services
            .AddHealthChecks()
            .AddSqlServer(configuration.GetConnectionString("Catalog")!, name: "sql", tags: ["ready"]);

        return services;
    }
}
