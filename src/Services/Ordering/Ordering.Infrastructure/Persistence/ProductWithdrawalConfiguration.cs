using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ordering.Infrastructure.Persistence;

/// <summary>
/// When Catalog last withdrew a product, at product level rather than per
/// currency. Read by §6.6's price projection so that a withdrawal survives
/// having no <c>ordering.ProductPrices</c> row to write to.
/// </summary>
/// <remarks>
/// <b>This table exists because a guard needs somewhere to live when the row
/// it would guard does not exist.</b> <c>ProductPrices</c> is keyed
/// <c>(ProductId, Currency)</c> and <c>ProductDiscontinued</c> carries no
/// currency (§9.1), so the discontinue statement can only reach rows that are
/// already there. §9.4 guarantees no ordering, so the two cases it cannot
/// reach are ordinary rather than exotic: a withdrawal claimed ahead of the
/// publish still retrying behind it, and a stale price for a currency nobody
/// had projected when the withdrawal ran. Both end with the price table's
/// <c>MERGE</c> taking its <c>NOT MATCHED</c> branch — the one branch no
/// <c>UpdatedAt</c> comparison can cover, because there is no target row to
/// compare against — and inserting an orderable row for a product Catalog has
/// withdrawn.
/// <para>
/// <b>§6.6 makes the same argument one projection over and reaches the same
/// answer.</b> Its <c>OrderSummaries</c> status handler is a <c>MERGE</c>
/// rather than an <c>UPDATE</c> precisely so a <c>Cancelled</c> claimed before
/// its <c>OrderPlaced</c> does not "match no row, change nothing, and be marked
/// processed". A status event can carry its own row into existence because it
/// knows the key; a withdrawal cannot, because the key includes a currency it
/// does not have. So it carries a watermark instead, and the price statement
/// consults it on the branch that has nothing else to consult.
/// </para>
/// <para>
/// <b>A watermark rather than a flag</b>, for the reason the price table's
/// guard is a comparison: a withdrawal must not make a product permanently
/// unorderable. Catalog republishing at a later <c>OccurredAt</c> re-lists it,
/// in currencies that have rows and in currencies that do not.
/// </para>
/// <para>
/// No <c>DbSet</c>, like <see cref="ProductPriceConfiguration"/> beside it:
/// Dapper writes this table and Dapper reads it, and a set would invite a path
/// that bypasses the idempotent <c>MERGE</c> keeping the watermark monotonic.
/// </para>
/// </remarks>
internal sealed class ProductWithdrawalConfiguration : IEntityTypeConfiguration<ProductWithdrawal>
{
    public void Configure(EntityTypeBuilder<ProductWithdrawal> builder)
    {
        builder.ToTable("ProductWithdrawals", "ordering");

        // The product alone. That is the whole point of the table — the price
        // table is already keyed per currency and this is what covers the
        // currencies it has never seen.
        builder.HasKey(w => w.ProductId);
    }
}

/// <summary>
/// A row of <see cref="ProductWithdrawalConfiguration"/>'s table: the newest
/// <c>OccurredAt</c> of any <c>ProductDiscontinued</c> seen for the product.
/// </summary>
internal sealed class ProductWithdrawal
{
    public Guid ProductId { get; set; }
    public DateTimeOffset WithdrawnAt { get; set; }
}
