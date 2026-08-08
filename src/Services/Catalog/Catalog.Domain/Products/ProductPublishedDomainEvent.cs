using Catalog.Domain.Common;
using Common.Domain;

namespace Catalog.Domain.Products;

/// <summary>
/// Raised by <see cref="Product.Publish"/>. Carries everything the
/// <c>ProductPublished</c> contract declares — <c>ProductId</c>, <c>Name</c>,
/// <c>ThumbnailUrl</c> and the price's two halves (Appendix D.5) — because an
/// event missing a field its contract needs is a mapper that cannot be
/// written (§5.5). Rich domain types are allowed here and flattened to
/// primitives only at the contract boundary; the <c>*DomainEvent</c> suffix
/// is what keeps the two from ever sharing a type name.
/// </summary>
public sealed record ProductPublishedDomainEvent(
    ProductId ProductId,
    string Name,
    string? ThumbnailUrl,
    Money Price,
    DateTimeOffset OccurredAt) : IDomainEvent;
