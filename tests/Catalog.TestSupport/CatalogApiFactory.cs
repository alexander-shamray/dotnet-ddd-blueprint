using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Catalog.TestSupport;

/// <summary>
/// The real Catalog host over a caller-supplied database (§12.4). One type for
/// both suites here — the host smoke points it at a name that cannot resolve,
/// the container suite at a running SQL Server — so what differs between them
/// is the database and not the wiring.
/// </summary>
public class CatalogApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    /// <summary>
    /// The RUNTIME connection of §7.1, and only that one. The host has no
    /// business reading <c>CatalogMigrator</c>, and a fixture that supplied
    /// both would hide it if it started.
    /// </summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("ConnectionStrings:Catalog", connectionString);
}
