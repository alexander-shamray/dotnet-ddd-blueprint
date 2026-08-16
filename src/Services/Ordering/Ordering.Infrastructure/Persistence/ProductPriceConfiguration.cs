using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ordering.Infrastructure.Persistence;

/// <summary>
/// §6.4's local price projection — Catalog's prices, as Ordering last saw
/// them. Not an aggregate and not part of the domain model: a read model,
/// mapped here only so that <c>migrations add</c> emits its table.
/// </summary>
/// <remarks>
/// <b>Mapped by EF, read by Dapper, and written by neither yet.</b>
/// <see cref="ProjectedPriceReader"/> queries it with Dapper (§6.5), and the
/// projection handler that maintains it is PR-20's. Configuring it through EF
/// is what makes the schema the migration's business rather than a hand-written
/// <c>CREATE TABLE</c> that would drift from the columns the reader selects.
/// <para>
/// There is no <c>DbSet</c> for it on the context, deliberately: nothing in
/// this service loads a price through EF, and a set would invite a write path
/// that bypasses the projection's idempotent <c>MERGE</c> (§6.6).
/// <c>ApplyConfigurationsFromAssembly</c> finds this class regardless.
/// </para>
/// <para>
/// <c>LastSeenAt</c> is the out-of-order guard PR-20 needs — a projection
/// applying an older event after a newer one must be able to tell, and the
/// column has to exist before the handler that reads it. <c>IsAvailable</c> is
/// the reader's filter: a product Catalog unpublishes stops being orderable
/// without its price row being deleted, so the history of what it cost
/// survives.
/// </para>
/// </remarks>
internal sealed class ProductPriceConfiguration : IEntityTypeConfiguration<ProductPrice>
{
    public void Configure(EntityTypeBuilder<ProductPrice> builder)
    {
        builder.ToTable("ProductPrices", "ordering");

        // Composite: one row per product per currency, which is exactly what
        // the reader's WHERE clause seeks on.
        builder.HasKey(p => new { p.ProductId, p.Currency });

        builder.Property(p => p.Currency).HasMaxLength(3);
        builder.Property(p => p.Amount).HasPrecision(19, 4);
    }
}

/// <summary>
/// A row of <see cref="ProductPriceConfiguration"/>'s table. Deliberately not
/// a domain type — it lives in Infrastructure because it is a read model, and
/// putting it in <c>Ordering.Domain</c> would make Catalog's pricing part of
/// this service's model rather than a cache of another service's.
/// </summary>
internal sealed class ProductPrice
{
    public Guid ProductId { get; set; }
    public string Currency { get; set; } = null!;
    public decimal Amount { get; set; }
    public bool IsAvailable { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
