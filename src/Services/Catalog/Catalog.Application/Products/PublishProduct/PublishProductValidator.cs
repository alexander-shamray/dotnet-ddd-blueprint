using FluentValidation;

namespace Catalog.Application.Products.PublishProduct;

/// <summary>
/// The user-input half of the boundary: everything here is a 400 with a
/// field-keyed error before any handler runs (§6.3, §10.5). The domain's own
/// guards stay — they signal bugs, not input (§5.7).
/// </summary>
public sealed class PublishProductValidator : AbstractValidator<PublishProductCommand>
{
    public PublishProductValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.ThumbnailUrl).MaximumLength(400);

        // The upper bound is storage's: PriceAmount is decimal(19,4), fifteen
        // integer digits, and an amount past it would reach the transaction
        // and fail at SaveChanges as a 500 — the wrong statement about whose
        // fault a too-large price is. The bound excludes .995 exactly:
        // Money.Of rounds half-to-even at two places, so 999….995 becomes
        // the sixteen-digit 1e15 and overflows despite passing a bare < 1e15.
        RuleFor(x => x.Amount).GreaterThanOrEqualTo(0).LessThan(999_999_999_999_999.995m);

        // NotEmpty first: Matches alone skips null, and a JSON "currency":
        // null would then reach Money.Of and turn malformed input into a 500.
        // Letters, not just length — "1$?" is three characters and no
        // currency, and Money.Of refuses it as a bug where this refuses it
        // as input (§5.7's division).
        RuleFor(x => x.Currency).NotEmpty().Matches("^[A-Za-z]{3}$");
    }
}
