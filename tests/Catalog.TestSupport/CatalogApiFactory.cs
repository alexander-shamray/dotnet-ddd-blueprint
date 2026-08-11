using Catalog.TestSupport.Outbox;
using Common.Application;
using Common.Infrastructure.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Catalog.TestSupport;

/// <summary>
/// The real Catalog host over caller-supplied dependencies (§12.4). One type
/// for both suites here — the host smoke points it at names that cannot
/// resolve, the container suite at running containers — so what differs
/// between them is the infrastructure and not the wiring.
/// </summary>
public class CatalogApiFactory(string connectionString, string rabbitConnectionString)
    : WebApplicationFactory<Program>
{
    /// <summary>
    /// The RUNTIME connection of §7.1, and only that one. The host has no
    /// business reading <c>CatalogMigrator</c>, and a fixture that supplied
    /// both would hide it if it started. The bus key is required because
    /// <c>AddMassTransitMessaging</c> throws without it — every host over
    /// this Program needs one, reachable or not.
    /// </summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder
            .UseSetting("ConnectionStrings:Catalog", connectionString)
            .UseSetting("ConnectionStrings:RabbitMq", rabbitConnectionString)
            .ConfigureServices(services =>
            {
                // Remove ONLY the outbox dispatcher, not every hosted service:
                // MassTransit registers its bus as one, so a
                // RemoveAll<IHostedService>() would stop the broker from
                // starting and silently disable every consumption test.
                //
                // The dispatcher polls every 500 ms; left running it drains
                // outbox rows underneath assertions about them. Tests that
                // want it call fixture.ProcessOutboxBatchAsync() explicitly.
                //
                // This match is why AddCatalogInfrastructure uses
                // AddHostedService<T> rather than a factory overload — a
                // factory registration leaves ImplementationType null and
                // this line would quietly match nothing.
                ServiceDescriptor hosted = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(OutboxDispatcher));
                services.Remove(hosted);

                // Still resolvable directly, so tests can drive one pass.
                services.AddSingleton<OutboxDispatcher>();

                // §9.4. Adding, not replacing: the production assemblies stay,
                // so a test cannot stage a type the real host would refuse.
                // Without this, NameOf throws on the first builder call and
                // every outbox test fails before its assertion.
                //
                // Mutating the registered instance rather than re-registering
                // one, because MessageTypeSource is deliberately mutable for
                // exactly this and the map is built from it at first resolve.
                services
                    .Single(d => d.ServiceType == typeof(MessageTypeSource))
                    .ImplementationInstance
                    .ShouldBeSource()
                    .Add(typeof(AlwaysThrows).Assembly);

                // The projection handlers for two of those three events. Each
                // layer scans itself (§6.2), and this assembly is a layer the
                // production registration has no reason to know about.
                services.AddPluggableFrom(typeof(AlwaysThrows).Assembly);
            });
}

file static class ServiceDescriptorExtensions
{
    /// <summary>
    /// Reads the registered instance back as itself, with a message that says
    /// what changed if it ever stops being registered that way — a cast
    /// failing here would otherwise read as a null reference from a line that
    /// mentions no null.
    /// </summary>
    public static MessageTypeSource ShouldBeSource(this object? instance) =>
        instance as MessageTypeSource ??
            throw new InvalidOperationException(
                "MessageTypeSource is no longer registered as a singleton instance, so the test " +
                "assembly's events cannot be added to it before the map is built (§9.4).");
}
