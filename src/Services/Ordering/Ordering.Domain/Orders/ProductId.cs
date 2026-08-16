namespace Ordering.Domain.Orders;

/// <summary>
/// A product, as Ordering knows it. Catalog owns the aggregate; this context
/// holds nothing but the identifier and the price it captured at the time,
/// which is what keeps the two services independently deployable (ADR-002).
/// </summary>
/// <remarks>
/// A distinct type from <c>Catalog.Domain.Products.ProductId</c> even though
/// both wrap a <see cref="Guid"/>. Sharing one would be a domain assembly
/// crossing a service boundary, which §4.3 permits exactly one of and this is
/// not it — the value crosses as a primitive inside a contract (§9.1) and is
/// re-typed on arrival.
/// </remarks>
public readonly record struct ProductId(Guid Value)
{
    public static ProductId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
