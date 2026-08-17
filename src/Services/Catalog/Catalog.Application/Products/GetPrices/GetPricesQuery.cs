using Common.Application;

namespace Catalog.Application.Products.GetPrices;

/// <summary>
/// The prices of a known set of products, in one currency — §9.7's synchronous
/// hop, read by the BFF to render an order form.
/// </summary>
/// <remarks>
/// Deliberately <b>not</b> paginated, where <c>GetProductsQuery</c> beside it
/// is. §6.5 requires a cursor on any collection endpoint because the caller
/// does not bound the result; here the caller supplies the ids, so the bound is
/// the request itself. What that needs instead is a ceiling on how many ids one
/// request may carry, which the handler applies — an unbounded <c>IN</c> list
/// is the same unbounded read wearing a different hat.
/// </remarks>
public sealed record GetPricesQuery(IReadOnlyCollection<Guid> ProductIds, string Currency)
    : IQuery<IReadOnlyList<ProductPriceDto>>;
