namespace Catalog.Domain.Products;

/// <summary>
/// §5.2's strongly typed identifier. A primitive <c>Guid</c> lets
/// <c>GetProduct(categoryId)</c> compile; this makes it a compile error, at
/// essentially zero cost, for the lifetime of the system.
/// </summary>
/// <remarks>
/// Version 7 rather than 4 for the creation time readable inside every
/// identifier — not for insert locality, which §5.2's trap explains SQL
/// Server's <c>uniqueidentifier</c> ordering denies it either way.
/// </remarks>
public readonly record struct ProductId(Guid Value)
{
    public static ProductId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
