using Common.Application;

namespace Catalog.Application.Products.GetPrices;

/// <summary>
/// The prices of a known set of products, in one currency — §9.7's synchronous
/// hop, read by the BFF to render an order form.
/// </summary>
/// <remarks>
/// Deliberately <b>not</b> paginated, where <c>GetProductsQuery</c> beside it
/// is. §6.5 requires a cursor on any collection endpoint <i>whose size the
/// caller does not bound</i>, and names this query as the one instance of the
/// exception: the caller enumerates the ids, so a cursor would paginate a set
/// it already holds. What the chapter asks for instead is a ceiling on the
/// list, and <see cref="GetPricesValidator"/> is where it lives — an unbounded
/// <c>IN</c> list is the same unbounded read wearing a different hat, and a
/// malformed request is the validator's to refuse (§6.3).
/// </remarks>
public sealed record GetPricesQuery(IReadOnlyCollection<Guid> ProductIds, string Currency)
    : IQuery<IReadOnlyList<ProductPriceDto>>;
