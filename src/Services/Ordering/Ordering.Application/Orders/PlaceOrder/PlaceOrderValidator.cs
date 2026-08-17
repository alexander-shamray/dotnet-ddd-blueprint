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
    /// <summary>
    /// The most lines one order may carry. Public because the test that pins
    /// the boundary reads it rather than repeating the number — a literal in
    /// both places is the second table this repository keeps removing.
    /// </summary>
    public const int MaxItems = 100;

    public PlaceOrderValidator()
    {
        // NotEmpty first: Matches alone skips null, and a JSON "currency":
        // null would reach the domain as a 500 rather than this 400. Letters,
        // not just length — Money.Of refuses "1$?" as a bug; this refuses it
        // as input (§5.7's division). \z, not $: .NET's $ matches before a
        // trailing newline, and "EUR\n" must fail here, not in the domain.
        RuleFor(x => x.Currency).NotEmpty().Matches(@"^[A-Za-z]{3}\z");
        // A maximum as well as a minimum, and the ceiling is not cosmetic.
        // ProjectedPriceReader expands the product ids into one SQL parameter
        // each and adds @Currency beside them; SQL Server's limit is 2,100, so
        // an authenticated caller sending 2,100 items turned a well-formed
        // request into a 500 rather than a 400. 100 is a business-shaped bound
        // well inside that — an order with more lines than this is a data
        // import, not a checkout — and it fails as validation, which is where
        // a request the caller phrased wrongly belongs (§5.7).
        // Cascade(Stop) is load-bearing, not tidiness. FluentValidation runs
        // every validator in a rule by default, so on an explicit JSON
        // "items": null the NotEmpty below records its failure and then the
        // size predicate dereferences null — turning a malformed request into
        // a 500 rather than the 400 this rule exists to produce. Stopping at
        // the first failure means the predicate only ever sees a list.
        RuleFor(x => x.Items)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(items => items.Count <= MaxItems)
            .WithMessage($"An order cannot contain more than {MaxItems} items.");
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.ProductId).NotEmpty();
            item.RuleFor(i => i.Quantity).GreaterThan(0).LessThanOrEqualTo(999);
        });

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
