namespace Ordering.Domain.Orders;

/// <summary>
/// A line's own identity, distinct from <see cref="OrderId"/> so that
/// <c>Entity&lt;TId&gt;</c>'s equality cannot be asked to compare a line with
/// the order that holds it.
/// </summary>
public readonly record struct OrderLineId(Guid Value)
{
    public static OrderLineId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
