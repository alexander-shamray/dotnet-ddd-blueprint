using Ordering.Domain.Common;
using Ordering.Domain.Orders;

namespace Ordering.Application.Orders;

/// <summary>
/// The prices <c>PlaceOrderHandler</c> builds its lines from (§6.4).
/// </summary>
/// <remarks>
/// <b>The obvious implementation is wrong, which is why this is a port.</b>
/// Prices are owned by Catalog, and calling Catalog over gRPC here would run a
/// network call to another service inside the write transaction (§6.3),
/// holding a database transaction open across it. The implementation reads a
/// <em>local</em> projection in Ordering's own database instead, so Catalog
/// can be down and orders still get placed — availability stops multiplying,
/// which is §2.3's fourth principle and ADR-002.
/// <para>
/// §9.7's gRPC pricing client is a different caller: the BFF, reading prices
/// to render the order form before anything is submitted. A display read may
/// be synchronous and may fail with a spinner. The write path may not.
/// </para>
/// <para>
/// The table behind it is empty until PR-20 lands the projection that fills
/// it, and that is the honest state rather than a gap: this PR is the consumer
/// and PR-20 is the producer, which is the order the delivery plan sequences
/// them in — PR-20 depends on this one.
/// </para>
/// </remarks>
public interface IProductPriceReader
{
    Task<IReadOnlyDictionary<ProductId, Money>> GetAsync(
        IReadOnlyCollection<ProductId> productIds,
        string currency,
        CancellationToken ct);
}
