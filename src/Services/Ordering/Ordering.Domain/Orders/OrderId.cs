namespace Ordering.Domain.Orders;

/// <summary>
/// §5.2's typed identifier. A <c>Guid</c> parameter accepts any other
/// aggregate's key; this one accepts an order's.
/// </summary>
public readonly record struct OrderId(Guid Value)
{
    // Version 7 rather than 4: the leading 48 bits are a millisecond
    // timestamp, so every identifier carries when it was made. §5.2's trap is
    // worth reading before assuming that also makes it a sequential key — it
    // does not, because uniqueidentifier compares the last six bytes first.
    public static OrderId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
