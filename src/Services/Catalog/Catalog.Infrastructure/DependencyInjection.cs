using System.Text.Json.Serialization;
using Catalog.Domain.Products;
using Catalog.Infrastructure.Messaging;
using Catalog.Infrastructure.Persistence;
using Common.Application;
using Common.Contracts.Catalog.V1;
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Outbox;
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
        services.AddScoped<IProductRepository, ProductRepository>();         // §5.6

        // §7.5's two Infrastructure halves: the collector reads EF's change
        // tracker, the publisher writes the row on the same context. Both
        // scoped, because the context is — a singleton either side would
        // stage into a transaction that had already closed.
        services.AddScoped<IDomainEventCollector, EfDomainEventCollector>();
        services.AddScoped<IIntegrationEventPublisher, OutboxPublisher>();

        // The schema the dispatcher's three statements are composed against.
        // A value rather than a literal in Common.Infrastructure, because
        // that assembly is every service's (§9.4).
        services.AddSingleton(new OutboxTable("catalog"));

        // The persisted type names (§9.4). Both singletons, and the source is
        // registered separately so a test host can Add its own assembly
        // without replacing the production pair.
        //
        // The map's factory is lazy, and MessageTypeMapValidator is what makes
        // "a duplicate FullName fails the host rather than the first message"
        // true: nothing else resolves the map until the dispatcher has claimed
        // a row, so without that hosted service the constructor's throw would
        // land on a background thread in a host that had been ready for hours.
        // It is registered first, because hosted services start in order.
        services.AddSingleton(
            new MessageTypeSource(typeof(ProductPublished).Assembly, typeof(Product).Assembly));
        services.AddSingleton(sp =>
        {
            MessageTypeSource source = sp.GetRequiredService<MessageTypeSource>();
            return new MessageTypeMap(source.Assemblies, source.Aliases, source.WrittenNames);
        });
        services.AddHostedService<MessageTypeMapValidator>();

        // The payload format (§9.4), and the converters that make this
        // service's value objects part of it. MoneyJsonConverter is the same
        // decision as ProductConfiguration's ComplexProperty one file over —
        // Money is persisted twice, as two columns and as two JSON members,
        // and knows about neither. Its absence is silent: a Money round-trips
        // to zero and a null currency rather than throwing.
        services.AddSingleton<JsonConverter, MoneyJsonConverter>();
        services.AddSingleton<OutboxJson>();

        // §13.3's projection lag, on the Commerce.Messaging meter
        // AddObservability already collects.
        services.AddSingleton<MessagingMetrics>();

        // The poll loop of §9.4. AddHostedService<T>, not a factory over a
        // registered singleton, and the difference is load-bearing: the
        // generic overload records an ImplementationType, which is what
        // §12.4's fixture matches on to remove *only* this hosted service —
        // MassTransit's bus is one too, and a RemoveAll<IHostedService>()
        // would stop the broker and silently disable every messaging test. A
        // factory registration leaves ImplementationType null and that
        // removal matches nothing, so the dispatcher would keep draining rows
        // underneath the assertions about them.
        //
        // Nothing here resolves OutboxDispatcher by type, so nothing else
        // registers it. The fixture adds that singleton itself, for the one
        // reason it needs one: driving a single pass with no timer to race.
        services.AddHostedService<OutboxDispatcher>();

        // §6.5's read side. Singleton, as §4.2's sample has it: the factory
        // holds a string and constructs per call — the connections it hands
        // out are the caller's to dispose, so there is no scoped state to
        // capture. The RUNTIME key, deliberately: a query on the migrator's
        // identity would be §7.1's boundary failing quietly.
        services.AddSingleton<IDbConnectionFactory>(
            new SqlConnectionFactory(configuration.GetConnectionString("Catalog")!));

        // The bus (§9). Its readiness needs no line below: AddMassTransit
        // registers the bus health check itself — "masstransit-bus", tagged
        // ready — argued at the registration.
        services.AddMassTransitMessaging(configuration);

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
