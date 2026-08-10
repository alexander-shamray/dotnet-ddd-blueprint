using Common.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Infrastructure.Persistence;

/// <summary>
/// Catalog's write-side context (§7.2). Sealed, and an implementation detail of
/// this assembly — §6.3 rejects an <c>IApplicationDbContext</c> exposing
/// <c>DbSet&lt;T&gt;</c>, because that puts EF Core types in an Application
/// signature while appearing to respect the boundary.
/// </summary>
/// <remarks>
/// Public rather than internal, and the distinction is worth stating: §6.3's
/// rule is that the context never <em>leaves</em> Infrastructure, which is a
/// rule about references and is enforced by the architecture gates, not by the
/// access modifier. Three callers construct or resolve it by name — the
/// <c>dotnet ef</c> tooling, the migrator host (§7.4) and the Testcontainers
/// fixture (§12.4) — and none of them is Application, which could not name it
/// anyway without the EF Core dependency its gate forbids.
/// </remarks>
public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    /// <summary>
    /// §9.4's outbox. The one <c>DbSet</c> here that is not an aggregate root,
    /// and deliberately so: the row has to be written by the same context as
    /// the aggregate to enlist in the same transaction, which is the entire
    /// mechanism. §12.4's tests read it through this property.
    /// </summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("catalog");

        // Landed before its first find (PR-08) so that PR-10 added an
        // IEntityTypeConfiguration<T> and not also the line that discovers
        // it; ProductConfiguration is what it finds today. §7.2 puts mapping
        // in these classes and never in attributes on domain types, which
        // would put EF Core in Catalog.Domain.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CatalogDbContext).Assembly);
    }

    /// <summary>
    /// §7.2's global conventions. They cover a model with no properties today,
    /// which is the argument for landing them now: an unbounded
    /// <c>NVARCHAR(MAX)</c> is cheap to prevent and expensive to migrate, and a
    /// convention introduced after the first entity silently changes a column
    /// that already exists.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(19, 4);
        configurationBuilder.Properties<string>().HaveMaxLength(400);
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("datetimeoffset(7)");
    }
}
