using Common.Domain;
using Ordering.Domain.Common;

namespace Ordering.Domain.Orders;

/// <summary>
/// A line on an order. An entity rather than a value object — it has identity
/// that survives a quantity change — but not an aggregate root: it is reached
/// only through <see cref="Order"/>, which owns every invariant about it.
/// </summary>
/// <remarks>
/// Its own key type rather than <see cref="OrderId"/>, and §5.5's
/// <c>Entity&lt;TId&gt;</c> comment is why: equality there compares the type
/// as well as the identifier precisely because a shared key type would
/// otherwise make a line equal to the order it belongs to, and nothing about
/// that reads as wrong at the call site. A distinct type removes the question.
/// </remarks>
public sealed class OrderLine : Entity<OrderLineId>
{
    public ProductId ProductId { get; private set; }
    public int Quantity { get; private set; }
    public Money UnitPrice { get; private set; }

    public Money LineTotal => UnitPrice * Quantity;

    // EF Core materialisation only (§5.4).
    private OrderLine() { }

    private OrderLine(OrderLineId id, ProductId productId, int quantity, Money unitPrice)
    {
        Id = id;
        ProductId = productId;
        Quantity = quantity;
        UnitPrice = unitPrice;
    }

    /// <summary>
    /// <c>internal</c>, not public: a line is created by <see cref="Order"/>
    /// and by nothing else. The quantity guard lives on
    /// <c>Order.AddLine</c> because that is where the merge decision is made,
    /// and duplicating it here would be a second place to change it.
    /// </summary>
    internal static OrderLine For(ProductId productId, int quantity, Money unitPrice) =>
        new(OrderLineId.New(), productId, quantity, unitPrice);

    internal void IncreaseQuantity(int by)
    {
        if (by <= 0)
            throw new DomainException("Quantity must be positive.");

        Quantity += by;
    }
}
