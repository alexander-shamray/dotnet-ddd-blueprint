using System.Data;
using Common.Application;
using Dapper;
using Ordering.Application.Orders;
using Ordering.Domain.Common;
using Ordering.Domain.Orders;

namespace Ordering.Infrastructure.Persistence;

/// <summary>
/// §6.4's price reader, over a local projection in Ordering's own database.
/// No network call inside the write transaction, so Catalog being down does
/// not stop an order being placed (ADR-002).
/// </summary>
/// <remarks>
/// <b>The table is empty until PR-20.</b> This PR builds the consumer and the
/// schema; the projection that fills <c>ordering.ProductPrices</c> from
/// Catalog's <c>ProductPublished</c> and <c>PriceChanged</c> events is PR-20's
/// deliverable, and PR-20 depends on this one. Until it lands, every
/// <c>PlaceOrder</c> against a real database returns
/// <c>order.products_unavailable</c> — which is the correct answer to "price
/// these products" from a service that has no prices, not a defect. §12.4's
/// handler tests seed the table directly, which is what lets the slice be
/// proven before its producer exists.
/// <para>
/// Dapper rather than EF (§6.5): this is a read, and it does not load an
/// aggregate. Prices can be stale by the projection's lag — typically
/// milliseconds — and where that is unacceptable the order captures the price
/// it used and payment reconciles against it. That is a business rule, not a
/// reason to make the write path depend on another service being up.
/// </para>
/// </remarks>
internal sealed class ProjectedPriceReader(IDbConnectionFactory connections) : IProductPriceReader
{
    private const string Sql =
        """
        SELECT ProductId, Amount, Currency
        FROM ordering.ProductPrices
        WHERE ProductId IN @ProductIds
            AND Currency = @Currency
            AND IsAvailable = 1;
        """;

    public async Task<IReadOnlyDictionary<ProductId, Money>> GetAsync(
        IReadOnlyCollection<ProductId> productIds,
        string currency,
        CancellationToken ct)
    {
        // An empty IN list is not a query Dapper can expand, and asking for no
        // prices is a legal thing for a caller to do — the validator refuses
        // an empty Items, but this port is not only that caller's.
        if (productIds.Count == 0)
            return new Dictionary<ProductId, Money>();

        using IDbConnection connection = connections.Create();

        // Upper-cased, because Money.Of normalises on the way in and this
        // column is written through it (PR-20). Comparing the caller's string
        // as it arrived makes a valid [A-Za-z]{3} request depend on the
        // server's collation: under a case-sensitive one "gbp" finds nothing
        // and PlaceOrder answers order.products_unavailable — which is what it
        // says about a product that does not exist. Catalog's GetPricesHandler
        // carries the same line for the same reason.
        IEnumerable<PriceRow> rows = await connection.QueryAsync<PriceRow>(
            new CommandDefinition(
                Sql,
                new
                {
                    ProductIds = productIds.Select(p => p.Value),
                    Currency = currency.ToUpperInvariant()
                },
                cancellationToken: ct));

        return rows.ToDictionary(r => new ProductId(r.ProductId), r => Money.Of(r.Amount, r.Currency));
    }

    /// <summary>
    /// Dapper's materialisation target. A private shape rather than the domain
    /// type, because the row is three columns and <see cref="Money"/> is
    /// always-valid — <c>Money.Of</c> above is where the row becomes a value
    /// object, and a projection row that fails that guard should throw here
    /// rather than arrive half-built.
    /// </summary>
    private sealed record PriceRow(Guid ProductId, decimal Amount, string Currency);
}
