namespace Catalog.Application.Products.GetProducts;

/// <summary>
/// Exactly the shape this listing needs — no generic DTO reused across
/// endpoints (§6.5). The price rides as its two column values: a query
/// bypasses the domain model, and rehydrating <c>Money</c> to serialise it
/// back out would be the loop §6.5 exists to remove.
/// </summary>
public sealed record ProductSummaryDto(
    Guid ProductId,
    string Name,
    string? ThumbnailUrl,
    decimal Amount,
    string Currency,
    DateTimeOffset PublishedAt);
