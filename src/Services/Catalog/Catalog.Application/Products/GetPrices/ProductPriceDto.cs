namespace Catalog.Application.Products.GetPrices;

/// <summary>
/// Exactly the shape the pricing hop needs, and no more (§6.5). The price
/// rides as its two column values for the reason <c>ProductSummaryDto</c>
/// gives: a query bypasses the domain model, and rehydrating <c>Money</c> only
/// to take it apart again for the wire is the loop §6.5 exists to remove.
/// </summary>
public sealed record ProductPriceDto(Guid ProductId, string Name, decimal Amount, string Currency);
