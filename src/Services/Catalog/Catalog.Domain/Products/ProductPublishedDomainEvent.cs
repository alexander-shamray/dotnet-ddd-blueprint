using Catalog.Domain.Common;
using Common.Domain;

namespace Catalog.Domain.Products;

/// <summary>
/// Raised by <see cref="Product.Publish"/>. Carries what the
/// <c>ProductPublished</c> contract in <c>Common.Contracts.Catalog.V1</c>
/// takes from the domain — <c>ProductId</c>, <c>Name</c>,
/// <c>ThumbnailUrl</c>, the price it splits into two halves, and
/// <c>OccurredAt</c>, the one envelope member the domain supplies; the
/// mapper mints <c>MessageId</c> and sets <c>CorrelationId</c> from the
/// aggregate (§9.3). An event missing a field its contract needs is a
/// mapper that cannot be written (§5.5). Rich domain types are allowed here
/// and flattened to primitives only at the contract boundary; the
/// <c>*DomainEvent</c> suffix is what keeps the two from ever sharing a type
/// name.
/// </summary>
public sealed record ProductPublishedDomainEvent(
    ProductId ProductId,
    string Name,
    string? ThumbnailUrl,
    Money Price,
    DateTimeOffset OccurredAt) : IDomainEvent;
