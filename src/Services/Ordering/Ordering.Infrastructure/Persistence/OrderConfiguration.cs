using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ordering.Domain.Orders;

namespace Ordering.Infrastructure.Persistence;

/// <summary>
/// §7.2's pattern: configuration in a class, never in attributes on the domain
/// type — which would put EF Core in <c>Ordering.Domain</c>, past the gate.
/// Found by <c>ApplyConfigurationsFromAssembly</c>.
/// </summary>
internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders", "ordering");
        builder.HasKey(o => o.Id);

        builder
            .Property(o => o.Id)
            .HasConversion(id => id.Value, value => new OrderId(value))
            .ValueGeneratedNever();

        builder
            .Property(o => o.CustomerId)
            .HasConversion(id => id.Value, value => new CustomerId(value));

        // The column §11.4's ownership check reads on every cancellation, and
        // the one §6.5's history query filters by. Both are equality on a
        // single customer, so a plain index over it is the whole requirement.
        builder.HasIndex(o => o.CustomerId);

        // By name, never by number (§7.2). An enum stored as an int makes the
        // member order a storage contract: inserting a status in the middle
        // silently reinterprets every existing row.
        builder
            .Property(o => o.Status)
            .HasConversion<string>()
            .HasMaxLength(20);

        // The order's currency is a private field, not a property — EF needs
        // telling it exists at all. It is what every line is validated
        // against, so an order without it cannot compute its own total.
        builder
            .Property<string>("_currency")
            .HasColumnName("Currency")
            .HasMaxLength(3);

        builder.ComplexProperty(
            o => o.ShippingAddress,
            address =>
            {
                address.Property(a => a.Line1).HasColumnName("ShipToLine1").HasMaxLength(200);
                address.Property(a => a.Line2).HasColumnName("ShipToLine2").HasMaxLength(200);
                address.Property(a => a.City).HasColumnName("ShipToCity").HasMaxLength(100);
                address.Property(a => a.PostalCode).HasColumnName("ShipToPostalCode").HasMaxLength(20);
                address.Property(a => a.Country).HasColumnName("ShipToCountry").HasMaxLength(2);
            });

        // Optimistic concurrency — SQL Server maintains this automatically.
        builder.Property(o => o.Version).IsRowVersion();

        // Total is computed from the lines and is not stored: a persisted copy
        // is a second source of truth that a line change can leave behind.
        builder.Ignore(o => o.Total);
        builder.Ignore(o => o.DomainEvents);

        // A related entity rather than an owned collection, and the reason is
        // ComplexProperty: an owned-collection builder does not offer it, so
        // Money on a line would have to be mapped a second way — two spellings
        // of one value object in one file, which is the drift §7.2's
        // convention block exists to prevent. The aggregate boundary is kept
        // by what is absent instead: no DbSet<OrderLine> on the context, and
        // OrderLine.For is internal, so a line cannot be reached or made
        // except through Order. OrderLineConfiguration maps the rest.
        //
        // The backing field, not the read-only view. Writing through `Lines`
        // would have EF assign a property with no setter; `_lines` is what
        // AddLine actually mutates.
        // IsRequired is not decoration: without it EF infers an optional
        // relationship and emits a nullable OrderId, so the database would
        // accept a line belonging to no order. That is the aggregate boundary
        // failing in the one place the domain cannot defend it — OrderLine.For
        // is internal and Lines is read-only, so the only route to an orphan is
        // the schema permitting one.
        builder
            .HasMany(o => o.Lines)
            .WithOne()
            .HasForeignKey("OrderId")
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .Navigation(o => o.Lines)
            .HasField("_lines")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
