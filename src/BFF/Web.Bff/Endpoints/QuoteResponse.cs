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
/// <param name="Total">
/// The sum of every line's <see cref="QuoteLine.LineTotal"/> — the basket,
/// quantities included.
/// <para>
/// It used to be the sum of <see cref="QuoteLine.Amount"/>, which was the sum
/// of one unit of each distinct product, because the request carried no
/// quantities to multiply by. That number was correct against its own contract
/// and useless to the screen this response exists to serve: the reference
/// client relabelled it "quoted unit prices" rather than show it as a total.
/// A rule the implementation could not satisfy is a rule that was going to be
/// broken by the next client instead (ADR-045).
/// </para>
/// </param>
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
/// <param name="ProductId">The product this line prices.</param>
/// <param name="Name">Catalog's name for it.</param>
/// <param name="Amount">
/// The price of <b>one</b> of it. Still here, and still called this: a cart
/// renders "£12.50 each" beside the line total, so the unit price is a thing
/// the screen needs rather than an intermediate value.
/// </param>
/// <param name="Quantity">
/// Echoed from the request, for the reason <see cref="QuoteResponse.Currency"/>
/// is — so the response stands alone. A client that has to hold its request
/// beside the reply to render the reply has been handed half an answer.
/// </param>
/// <param name="LineTotal">
/// <paramref name="Amount"/> times <paramref name="Quantity"/>, computed here.
/// Without it the basket total would be right and every <i>line</i> total
/// would be computed in the client, which is the same defect one level down: a
/// cart shows "2 × £12.50 = £25.00", and supplying the first two figures but
/// not the third leaves the client computing money after all.
/// </param>
public sealed record QuoteLine(
    Guid ProductId,
    string Name,
    decimal Amount,
    int Quantity,
    decimal LineTotal);
