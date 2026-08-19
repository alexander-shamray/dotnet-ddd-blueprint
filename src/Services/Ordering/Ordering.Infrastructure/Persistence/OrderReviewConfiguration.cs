using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ordering.Infrastructure.Persistence;

/// <summary>
/// §9.6's escalation table — a work queue, not a log. A row means "a human
/// still needs to look at this"; resolving one deletes it.
/// </summary>
/// <remarks>
/// <b>There is no <c>ResolvedAt</c>.</b> A nullable timestamp nothing sets is
/// an alert that fires once and never clears, and "resolved" and "gone" are the
/// same state for a queue. The audit trail of what was escalated and when lives
/// in the event history, so deleting the row loses nothing — keeping a resolved
/// one would mean building the back-office surface to set the flag, which this
/// platform does not have and does not need for the escalation to work.
/// <para>
/// <b>Mapped by EF, written by Dapper</b>, on
/// <see cref="ProductPriceConfiguration"/>'s terms and for the same reason:
/// configuring it through EF is what makes the schema the migration's business
/// rather than a hand-written <c>CREATE TABLE</c> that drifts from the columns
/// the handler names. There is no <c>DbSet</c> — the only writer is
/// <c>FlagOrderForReviewHandler</c>, through <c>IUnitOfWork.ExecuteRawAsync</c>
/// so the row lands in the command's own transaction (§6.3), and a set would
/// invite a second path that bypasses the conditional insert's range lock.
/// </para>
/// </remarks>
internal sealed class OrderReviewConfiguration : IEntityTypeConfiguration<OrderReview>
{
    public void Configure(EntityTypeBuilder<OrderReview> builder)
    {
        builder.ToTable("OrderReviews", "ordering");

        // Composite, and the reason is the handler's: one outstanding review
        // per order per reason, so a redelivered escalation is absorbed by the
        // key rather than counted twice. An order can legitimately carry two
        // rows — the release timeout raises stock_not_released beside whatever
        // cancelled it — which is why the reason is part of the key and not a
        // column beside a unique order id.
        builder.HasKey(r => new { r.OrderId, r.Reason });

        builder
            .Property(r => r.Reason)
            .HasMaxLength(64)
            .IsUnicode(false)
            .IsRequired();

        // §13.6 alerts on how long a review has been outstanding, which is a
        // range scan over this column and nothing else.
        builder
            .HasIndex(r => r.RaisedAt)
            .HasDatabaseName("IX_OrderReviews_RaisedAt");
    }
}

/// <summary>
/// A row of <see cref="OrderReviewConfiguration"/>'s table. Deliberately not a
/// domain type: §9.6 is explicit that an escalation changes nothing about the
/// order, so putting it in <c>Ordering.Domain</c> would make "a human should
/// look at this" part of the model that decides what an order may do.
/// </summary>
internal sealed class OrderReview
{
    public Guid OrderId { get; set; }
    public string Reason { get; set; } = null!;
    public DateTimeOffset RaisedAt { get; set; }
}
