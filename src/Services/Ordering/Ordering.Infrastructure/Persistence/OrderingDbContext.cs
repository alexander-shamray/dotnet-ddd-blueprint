using Common.Infrastructure.Inbox;
using Common.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Ordering.Domain.Orders;

namespace Ordering.Infrastructure.Persistence;

/// <summary>
/// Ordering's write-side context (§7.2). Sealed, and an implementation detail of
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
public sealed class OrderingDbContext(DbContextOptions<OrderingDbContext> options) : DbContext(options)
{
    /// <summary>
    /// The aggregate root (§5.4). One <c>DbSet</c> per root and no set for
    /// <c>OrderLine</c>, which is owned — reaching a line without its order is
    /// exactly the aggregate-boundary breach the model exists to prevent.
    /// </summary>
    public DbSet<Order> Orders => Set<Order>();

    /// <summary>
    /// §9.4's outbox. The one <c>DbSet</c> here that is not an aggregate root,
    /// and deliberately so: the row has to be written by the same context as
    /// the aggregate to enlist in the same transaction, which is the entire
    /// mechanism. §12.4's tests read it through this property.
    /// </summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>
    /// §9.5's inbox. Declared for the same reason as the outbox above and read
    /// by nothing in production: <c>InboxFilter&lt;T&gt;</c> is common code and
    /// reaches the entity through <c>Set&lt;InboxMessage&gt;()</c>, which is
    /// what lets one filter serve every service. The property is here so this
    /// context states its whole model, and so §12.4's tests can read the table
    /// the way they read the other one.
    /// </summary>
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("ordering");

        // Here before its first find, so that the first entity adds an
        // IEntityTypeConfiguration<T> and not also the line that discovers
        // it. §7.2 puts mapping in these classes and never in attributes on
        // domain types, which would put EF Core in Ordering.Domain.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrderingDbContext).Assembly);
    }

    /// <summary>
    /// §7.2's global conventions. They landed with the scaffold, over a model
    /// that then had no properties at all, and that was the argument for
    /// landing them early: an unbounded <c>NVARCHAR(MAX)</c> is cheap to
    /// prevent and expensive to migrate, and a convention introduced after the
    /// first entity silently changes a column that already exists. They now
    /// govern the orders, lines, product prices, inbox and outbox rows this
    /// context maps — which is the outcome the timing bought, not a change of
    /// purpose.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(19, 4);
        configurationBuilder.Properties<string>().HaveMaxLength(400);
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("datetimeoffset(7)");
    }
}
