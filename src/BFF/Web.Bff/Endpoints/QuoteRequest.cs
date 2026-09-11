using Common.Contracts.Ordering.V1;
using FluentValidation;

namespace Web.Bff.Endpoints;

/// <summary>
/// A basket, as the screen holds it: lines of a product and how many of it the
/// customer wants. A product may be named by more than one line, and the
/// quantities are merged — see <see cref="Lines"/>.
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
/// At most <see cref="OrderLimits.MaxLines"/> of them. A product may be named
/// more than once and the quantities are merged, because that is what placing
/// the order does with them.
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
            // Before anything reads a member off an element, because a JSON
            // "lines": [null] binds as a list holding a null: the element type
            // being non-nullable is a compiler constraint and not a
            // deserialisation one, and System.Text.Json enforces neither. The
            // duplicate check below projects ProductId off every element, so
            // without this a malformed request arrived as a 500 rather than
            // the 400 this validator exists to produce.
            //
            // This is the null-LIST guard's sibling rather than a second copy
            // of it. Cascade(Stop) above catches "lines": null and nothing in
            // that chain looks inside the list, so a guard on one does nothing
            // for the other — which is why each has its own test. Found by
            // Copilot.
            .Must(lines => lines.All(line => line is not null))
            .WithMessage("A quote line cannot be null.")
            // Over the MERGED quantity, because a repeated product is
            // legitimate and the endpoint merges it before pricing — exactly
            // as Order.AddLine does in the domain. Checked per line alone this
            // would not be a bound on the basket: two lines of MaxQuantity
            // each would quote a basket the order refuses, which is the whole
            // failure this endpoint's shared bounds exist to prevent.
            //
            // This refused a repeated product outright until Copilot pointed
            // out that PlaceOrder accepts one and merges it, deliberately and
            // with a domain test pinning it. Shipping a second duplicate
            // policy in the host that quotes FOR that order is the same defect
            // as the one ADR-045 set out to fix, pointing the other way.
            // Summed as long, because Enumerable.Sum over int is CHECKED and
            // this predicate runs before the per-line rules: two lines at
            // int.MaxValue threw OverflowException and answered 500 where the
            // whole point of this validator is a 400. Widening makes the rule
            // total over every int the wire can bind, and a hundred lines of
            // int.MaxValue is nowhere near long's range.
            .Must(lines => lines
                .GroupBy(line => line.ProductId)
                .All(product => product.Sum(line => (long)line.Quantity) <= OrderLimits.MaxQuantity))
            .WithMessage($"A quote cannot contain more than {OrderLimits.MaxQuantity} of one product.");

        // Guarded, because RuleForEach is a separate rule from the chain above
        // and the class-level cascade runs every rule: a null element rejected
        // there would still arrive here. The predicate repeats no bound — it
        // asks only whether the elements are safe to look inside.
        RuleForEach(x => x.Lines)
            .Where(line => line is not null)
            .ChildRules(line =>
        {
            line.RuleFor(l => l.ProductId).NotEmpty();
            line.RuleFor(l => l.Quantity)
                .GreaterThanOrEqualTo(OrderLimits.MinQuantity)
                .LessThanOrEqualTo(OrderLimits.MaxQuantity);
        });
    }
}
