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
        // NotEmpty first: Matches alone skips null, and a JSON "currency":
        // null would reach the domain as a 500 rather than this 400. Letters,
        // not just length — Money.Of refuses "1$?" as a bug; this refuses it
        // as input (§5.7's division). \z, not $: .NET's $ matches before a
        // trailing newline, and "EUR\n" must fail here, not in the domain.
        RuleFor(x => x.Currency).NotEmpty().Matches(@"^[A-Za-z]{3}\z");
        RuleFor(x => x.Items).NotEmpty();
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
