using Common.Contracts.Ordering.V1;
using FluentValidation;

namespace Ordering.Application.Orders.PlaceOrder;

/// <summary>
/// §6.4's validator. Everything here is about the shape of the request; the
/// rules about an order — at least one line, one currency across them, a
/// positive quantity — belong to <c>Order</c> and are asserted there (§5.7's
/// division).
/// </summary>
public sealed class PlaceOrderValidator : AbstractValidator<PlaceOrderCommand>
{
    public PlaceOrderValidator()
    {
        // An omitted CommandId binds as Guid.Empty, which is a single shared
        // key rather than an absent one — every caller of this command would
        // claim the same one, and the first success would be replayed to all
        // of them for a day. Validation is the OUTER behaviour (§6.3), so this
        // 400 is raised before any key is claimed.
        RuleFor(x => x.CommandId).NotEmpty();
        // NotEmpty first: Matches alone skips null, and a JSON "currency":
        // null would reach the domain as a 500 rather than this 400. Letters,
        // not just length — Money.Of refuses "1$?" as a bug; this refuses it
        // as input (§5.7's division). \z, not $: .NET's $ matches before a
        // trailing newline, and "EUR\n" must fail here, not in the domain.
        RuleFor(x => x.Currency).NotEmpty().Matches(@"^[A-Za-z]{3}\z");
        // A maximum as well as a minimum, and the ceiling is not cosmetic —
        // OrderLimits.MaxLines argues why, and owns the number. It is read
        // rather than repeated because Web.Bff's quote validator enforces the
        // same bound: a quote that accepts what this refuses prices a basket
        // the customer cannot buy. It fails as validation, which is where a
        // request the caller phrased wrongly belongs (§5.7).
        // Cascade(Stop) is load-bearing, not tidiness. FluentValidation runs
        // every validator in a rule by default, so on an explicit JSON
        // "items": null the NotEmpty below records its failure and then the
        // size predicate dereferences null — turning a malformed request into
        // a 500 rather than the 400 this rule exists to produce. Stopping at
        // the first failure means the predicate only ever sees a list.
        RuleFor(x => x.Items)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(items => items.Count <= OrderLimits.MaxLines)
            .WithMessage($"An order cannot contain more than {OrderLimits.MaxLines} items.");
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.ProductId).NotEmpty();

            // The same two numbers Web.Bff's quote validator reads, from the
            // same constants — see OrderLimits. GreaterThanOrEqualTo rather
            // than GreaterThan, because the bound the constant names is the
            // smallest quantity a line may carry and not the largest it may
            // not: phrased as GreaterThan(MinQuantity) this rule would refuse
            // a line for one of something.
            item.RuleFor(i => i.Quantity)
                .GreaterThanOrEqualTo(OrderLimits.MinQuantity)
                .LessThanOrEqualTo(OrderLimits.MaxQuantity);
        });

        // And again over the MERGED quantity, because the rule above is not a
        // bound on the order. A repeated product is legitimate here — the
        // handler says so and Order.AddLine merges the lines — so two items of
        // MaxQuantity each passed every rule and placed an order for twice it.
        // The per-item rule still earns its place: it names the offending
        // item, where this one can only name the collection.
        //
        // Found by Copilot on the pull request that gave these bounds an owner,
        // and it is the sharper half of that finding: a constant claiming to be
        // the order's quantity bound has to actually be one (ADR-045).
        // Summed as long, because Enumerable.Sum over int is CHECKED and this
        // rule runs before RuleForEach reports either quantity: two items at
        // int.MaxValue threw OverflowException and turned a malformed order
        // into a 500 rather than the 400 validation exists to produce.
        RuleFor(x => x.Items)
            .Must(items => items
                .GroupBy(i => i.ProductId)
                .All(product => product.Sum(i => (long)i.Quantity) <= OrderLimits.MaxQuantity))
            .WithMessage($"An order cannot contain more than {OrderLimits.MaxQuantity} of one product.")
            .When(x => x.Items is not null && x.Items.All(i => i is not null));

        // The address is required as a whole before its parts are worth
        // checking: a null body member would otherwise produce five failures
        // about members of nothing.
        RuleFor(x => x.ShippingAddress).NotNull();
        When(x => x.ShippingAddress is not null, () =>
        {
            RuleFor(x => x.ShippingAddress.Line1).NotEmpty().MaximumLength(200);
            RuleFor(x => x.ShippingAddress.Line2).MaximumLength(200);
            RuleFor(x => x.ShippingAddress.City).NotEmpty().MaximumLength(100);
            RuleFor(x => x.ShippingAddress.PostalCode).NotEmpty().MaximumLength(20);

            // The same \z as the currency, for the same reason.
            RuleFor(x => x.ShippingAddress.Country).NotEmpty().Matches(@"^[A-Za-z]{2}\z");
        });
    }
}
