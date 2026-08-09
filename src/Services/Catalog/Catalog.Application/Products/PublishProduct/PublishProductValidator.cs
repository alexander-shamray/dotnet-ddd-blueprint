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
        // fault a too-large price is.
        RuleFor(x => x.Amount).GreaterThanOrEqualTo(0).LessThan(1_000_000_000_000_000m);

        // NotEmpty first: Length alone skips null, and a JSON "currency":
        // null would then reach Money.Of and turn malformed input into a 500.
        RuleFor(x => x.Currency).NotEmpty().Length(3);
    }
}
