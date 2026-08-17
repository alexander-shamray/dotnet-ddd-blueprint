using System.Globalization;
using Catalog.Pricing.V1;

namespace Web.Bff.Endpoints;

/// <summary>
/// The BFF's one screen, and the only thing in this host that spends §9.7's
/// hop budget.
/// </summary>
/// <remarks>
/// <c>/v1/checkout</c> rather than a resource path, because the gateway routes
/// <c>/bff</c> and <c>/api</c> as two namespaces and a client picks one or the
/// other (§10.2): aggregated responses shaped for a screen, or the service APIs
/// shaped for a resource. A caller reaches this at
/// <c>/bff/v1/checkout/quote</c>; the gateway strips <c>/bff</c> and this host
/// sees the version, exactly as a service sees one under <c>/api</c>.
/// </remarks>
public static class CheckoutEndpoints
{
    public static void MapCheckoutEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app
            .MapGroup("/v1/checkout")
            .WithTags("Checkout")
            // Fail closed at the group, the shape §11.4 uses everywhere. The
            // gateway's web-bff route already carries the "authenticated"
            // policy, and this is not redundant with it: §11.2 requires every
            // host to validate its own tokens, because anything reaching this
            // one by another path — a port-forward, a network policy somebody
            // widened — arrives with no edge in front of it.
            .RequireAuthorization();

        group
            .MapGet(
                "/quote",
                async (
                    Guid[] productId,
                    string currency,
                    Pricing.PricingClient pricing,
                    CancellationToken ct) =>
                {
                    if (productId.Length == 0)
                    {
                        return Results.Problem(
                            title: "No products to price",
                            detail: "A quote needs at least one productId.",
                            statusCode: StatusCodes.Status400BadRequest);
                    }

                    // The caller's set, deduplicated and order-preserving. The
                    // dedup matters twice over: it is what makes the reply's
                    // one-price-per-product shape line up with the request, and
                    // it is what keeps a caller from spending the ceiling on
                    // the same id a hundred times.
                    Guid[] requested = [.. productId.Distinct()];

                    GetPricesRequest request = new() { Currency = currency };
                    request.ProductId.AddRange(requested.Select(id => id.ToString()));

                    // No ceiling checked here, deliberately. Catalog's
                    // GetPricesValidator owns that number and answers
                    // InvalidArgument past it, which UpstreamExceptionHandler
                    // turns into the 400 the caller deserves. A second copy of
                    // the limit in this host would be a number that drifts from
                    // the one actually enforced — and the drift would show as
                    // requests refused here that Catalog would have served.
                    GetPricesReply reply = await pricing.GetPricesAsync(request, cancellationToken: ct);

                    List<QuoteLine> lines = new(reply.Price.Count);

                    foreach (ProductPrice price in reply.Price)
                    {
                        // InvariantCulture on both sides of the wire, matching
                        // what PricingService formats with. Left to the ambient
                        // culture, a host running under a comma-decimal locale
                        // parses "12.50" as 1250 — a hundredfold error that
                        // appears in production and never on the machine that
                        // wrote the code.
                        //
                        // And NOT NumberStyles.Number, which is the obvious
                        // choice and was the first one written here. It
                        // includes AllowThousands, so "12,50" parses under the
                        // INVARIANT culture as twelve hundred and fifty — the
                        // exact hundredfold error the invariant culture was
                        // supposed to rule out, arriving through the styles
                        // argument instead. A wire format has no group
                        // separators, so neither does this parse. Caught by a
                        // test that expected a 500 and got a 200.
                        if (!decimal.TryParse(
                                price.Amount,
                                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                                CultureInfo.InvariantCulture,
                                out decimal amount))
                        {
                            // A contract violation between two services, not a
                            // caller's mistake, so it stays a 500. Naming the
                            // product is what makes it findable; the value is
                            // Catalog's own price and not a secret.
                            throw new InvalidOperationException(
                                $"Catalog returned '{price.Amount}' as the price of product " +
                                $"{price.ProductId}, which is not a decimal in the invariant form " +
                                "pricing.proto specifies.");
                        }

                        lines.Add(new QuoteLine(Guid.Parse(price.ProductId), price.Name, amount));
                    }

                    // Set-based, so the answer does not depend on the reply's
                    // order and a product Catalog echoed twice cannot make a
                    // requested one look priced.
                    HashSet<Guid> priced = [.. lines.Select(line => line.ProductId)];

                    return Results.Ok(new QuoteResponse(
                        currency,
                        lines,
                        lines.Sum(line => line.Amount),
                        [.. requested.Where(id => !priced.Contains(id))]));
                })
            .WithName("GetQuote");
    }
}
