using Common.Application;

namespace Catalog.Application.Products.GetProducts;

/// <summary>
/// The catalogue listing, newest first. Cursor-paginated because pagination is
/// mandatory on any collection endpoint and cursor-based by default
/// (§6.5, ADR-016) — there is no such thing as a small table in production.
/// </summary>
public sealed record GetProductsQuery(string? Cursor, int Limit)
    : IQuery<CursorPage<ProductSummaryDto>>;
