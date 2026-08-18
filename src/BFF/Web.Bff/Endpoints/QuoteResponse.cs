namespace Web.Bff.Endpoints;

/// <summary>
/// What the order form needs to render, in one response — which is the whole
/// job description of a BFF (§10.1). Shaped for a screen rather than for a
/// resource: the total is computed here because every client would otherwise
/// compute it, and two clients computing a total is two places to get rounding
/// wrong.
/// </summary>
/// <param name="Currency">Echoed from the request, so the response stands alone.</param>
/// <param name="Lines">One per product that has a price in this currency.</param>
/// <param name="Total">The sum of <paramref name="Lines"/>.</param>
/// <param name="Unpriced">
/// The products asked about that Catalog returned no price for — unknown,
/// unpublished, or priced in another currency. Named rather than omitted: a
/// form that silently drops a line the customer chose is worse than one that
/// says it cannot price it.
/// </param>
public sealed record QuoteResponse(
    string Currency,
    IReadOnlyList<QuoteLine> Lines,
    decimal Total,
    IReadOnlyList<Guid> Unpriced);

/// <summary>One priced product on the order form.</summary>
public sealed record QuoteLine(Guid ProductId, string Name, decimal Amount);
