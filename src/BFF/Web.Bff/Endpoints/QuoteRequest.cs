using Common.Contracts.Ordering.V1;
using FluentValidation;

namespace Web.Bff.Endpoints;

/// <summary>
/// A basket, as the screen holds it. One line per product, each carrying how
/// many of it the customer wants.
/// </summary>
/// <remarks>
/// <para>
/// <b>A body rather than a query string, and a POST rather than a GET.</b> A
/// quote is a read, so POST makes it look like a write; two things make that
/// trade acceptable here. Nothing caches this — the group carries
/// <c>RequireAuthorization()</c>, emits no cache headers, and a quote is keyed
/// to one customer's basket, so a shared cache's hit rate on it is
/// approximately zero. And the request is a list of records, which a body is
/// the honest place for: as repeated query parameters a hundred ids run to
/// roughly four and a half kilobytes of URL, inside every common limit but not
/// far inside, and it is a limit nobody chose.
/// </para>
/// <para>
/// <b>Not two parallel arrays.</b> The cheap alternative was to keep the GET
/// and add <c>int[] quantity</c> beside <c>Guid[] productId</c>. ASP.NET binds
/// them independently: nothing enforces equal length, and nothing enforces
/// that the <i>n</i>th quantity belongs to the <i>n</i>th id. A proxy that
/// drops or reorders one repeated parameter silently shifts every quantity
/// onto the wrong product, and the only symptom is a wrong total — the exact
/// failure class <c>CheckoutEndpoints</c> already grew an <c>outstanding</c>
/// set to catch on the reply side. Positional pairing across two independently
/// bound collections cannot be validated after the fact: at equal length there
/// is no way to tell a correct pairing from a shifted one.
/// </para>
/// </remarks>
/// <param name="Currency">The currency the basket is to be priced in.</param>
/// <param name="Lines">
/// One per product, at most <see cref="OrderLimits.MaxLines"/> of them, each
/// naming a product at most once.
/// </param>
public sealed record QuoteRequest(string Currency, IReadOnlyList<QuoteRequestLine> Lines);

/// <summary>One product in the basket, and how many of it.</summary>
public sealed record QuoteRequestLine(Guid ProductId, int Quantity);

/// <summary>
/// §6.4's shape of validator, in the one host that has no handler pipeline to
/// run one for it — so <c>CheckoutEndpoints</c> invokes it directly and lets
/// the <c>ValidationException</c> reach <c>Common.Web</c>'s handler, which is
/// what turns it into §10.5's field-keyed 400.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bounds are Ordering's, read rather than repeated.</b>
/// <see cref="OrderLimits"/> owns them and argues why; what matters here is
/// that this endpoint quotes for an order <c>PlaceOrderValidator</c> will
/// later have to accept, so the two must agree exactly.
/// </para>
/// <para>
/// <b>Quantity is the BFF's to bound, where the product count is not.</b> The
/// endpoint declines to check Catalog's id ceiling, deliberately and
/// correctly: Catalog's <c>GetPricesValidator</c> owns that number, and a
/// second copy here would drift from the one actually enforced. That reasoning
/// does not transfer. Catalog prices a <i>product</i>; it has no opinion about
/// how many of one a basket holds and no validator that could own the bound.
/// Quantity is a basket concept, this is the only host that sees a basket, and
/// Ordering is the only other holder of the same rule — so the constant is
/// shared rather than copied, and the ceiling on <see cref="QuoteRequest.Lines"/>
/// is a bound on this request's own size, checked before the hop, rather than
/// a second copy of Catalog's.
/// </para>
/// </remarks>
internal sealed class QuoteRequestValidator : AbstractValidator<QuoteRequest>
{
    public QuoteRequestValidator()
    {
        // NotEmpty before Matches, and \z rather than $, for the two reasons
        // PlaceOrderValidator states: Matches alone skips null, and .NET's $
        // matches before a trailing newline, so "EUR\n" has to fail here.
        RuleFor(x => x.Currency).NotEmpty().Matches(@"^[A-Za-z]{3}\z");

        // Cascade(Stop) is load-bearing, exactly as it is in
        // PlaceOrderValidator: an explicit JSON "lines": null would otherwise
        // record NotEmpty's failure and then dereference null in the predicate
        // below, turning a malformed request into a 500.
        RuleFor(x => x.Lines)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(lines => lines.Count <= OrderLimits.MaxLines)
            .WithMessage($"A quote cannot contain more than {OrderLimits.MaxLines} lines.")
            // A repeated product is refused rather than summed, and rather
            // than deduplicated. Summing silently repairs a caller's bug and
            // hides it: a basket has one line per product, and a request with
            // two is a client that has lost track of its own state. Dropping
            // the duplicate is worse still — under the old contract a repeated
            // id carried no information, so Distinct() lost nothing, but a
            // repeated LINE carries a quantity, and dropping it discards part
            // of the customer's basket.
            //
            // The dedup's second purpose survives: it kept a caller from
            // spending Catalog's id ceiling on one product repeated a hundred
            // times, and refusing outright does that more cheaply, because the
            // request never leaves this host.
            .Must(lines => lines.Select(line => line.ProductId).Distinct().Count() == lines.Count)
            .WithMessage("A quote names each product at most once.");

        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ProductId).NotEmpty();
            line.RuleFor(l => l.Quantity)
                .GreaterThanOrEqualTo(OrderLimits.MinQuantity)
                .LessThanOrEqualTo(OrderLimits.MaxQuantity);
        });
    }
}
