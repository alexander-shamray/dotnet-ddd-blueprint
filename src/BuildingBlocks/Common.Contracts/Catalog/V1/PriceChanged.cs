namespace Common.Contracts.Catalog.V1;

/// <summary>
/// A product's price moved (§3.2). Ordering projects it into
/// <c>ordering.ProductPrices</c> and reads it on the <em>write</em> path
/// (§6.4), which is why the amount travels rather than an instruction to go
/// and fetch one.
/// </summary>
/// <remarks>
/// <c>Amount</c> and <c>Currency</c> rather than a <c>Money</c>: a contract
/// carries primitives (§9.1), and the currency travels beside the amount for
/// the reason §9.6's <c>AuthorisePayment</c> gives — a bare decimal is a charge
/// waiting to be made in the wrong denomination.
/// </remarks>
public sealed record PriceChanged : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid ProductId { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }
}
