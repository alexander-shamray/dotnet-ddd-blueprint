namespace Common.Contracts.Catalog.V1;

/// <summary>
/// Catalog's first public fact (§3.2). The namespace carries the version, not
/// the type name (§9.2), so <c>V2.ProductPublished</c> is a new namespace and
/// not a suffix.
/// </summary>
/// <remarks>
/// The envelope's three members are written out here rather than inherited
/// from a base record: a shared base is a shared versioning fate (§9.2), and
/// three properties is a cheaper price than that.
/// <para>
/// <c>Amount</c> and <c>Currency</c> rather than the domain's <c>Money</c> —
/// a contract carries primitives, and the decomposition happens in the mapper
/// (§9.3) because that is the boundary. The <c>*DomainEvent</c> suffix on the
/// type this is mapped from is what keeps the two from ever sharing a name,
/// which is what makes "the domain type never reaches the broker" an
/// assertion a test can make (§12.4).
/// </para>
/// </remarks>
public sealed record ProductPublished : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid ProductId { get; init; }

    public required string Name { get; init; }

    public required string? ThumbnailUrl { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }
}
