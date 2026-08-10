using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

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
            .UseSetting("ConnectionStrings:RabbitMq", rabbitConnectionString);
}
