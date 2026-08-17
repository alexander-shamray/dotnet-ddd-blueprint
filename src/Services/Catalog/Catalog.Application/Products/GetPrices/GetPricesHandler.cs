using System.Data;
using Common.Application;
using Dapper;

namespace Catalog.Application.Products.GetPrices;

/// <summary>
/// §6.5's read side again — Dapper over the write tables, Catalog being level 1
/// — answering §9.7's one synchronous hop.
/// </summary>
public sealed class GetPricesHandler(IDbConnectionFactory connections)
    : IQueryHandler<GetPricesQuery, IReadOnlyList<ProductPriceDto>>
{
    private const string Sql =
        """
        SELECT
            ProductId = p.Id,
            Name      = p.Name,
            Amount    = p.PriceAmount,
            Currency  = p.PriceCurrency
        FROM catalog.Products p
        WHERE p.Id IN @ProductIds
            AND p.PriceCurrency = @Currency;
        """;

    public async Task<IReadOnlyList<ProductPriceDto>> HandleAsync(GetPricesQuery query, CancellationToken ct)
    {
        // Asking for no prices is a legal thing for a caller to do, and an
        // empty IN list is not a query Dapper can expand — the same guard, for
        // the same reason, as Ordering's ProjectedPriceReader. The ceiling at
        // the other end is GetPricesValidator's, because it is a malformed
        // request rather than an empty answer.
        if (query.ProductIds.Count == 0)
            return [];

        using IDbConnection connection = connections.Create();

        // Distinct, because the id list is the caller's and a repeated id would
        // otherwise cost a duplicate row the BFF would have to fold. The set is
        // the semantics the reply already has — one price per product.
        IEnumerable<ProductPriceDto> rows = await connection.QueryAsync<ProductPriceDto>(
            new CommandDefinition(
                Sql,
                new { ProductIds = query.ProductIds.Distinct(), Currency = query.Currency },
                cancellationToken: ct));

        return [.. rows];
    }
}
