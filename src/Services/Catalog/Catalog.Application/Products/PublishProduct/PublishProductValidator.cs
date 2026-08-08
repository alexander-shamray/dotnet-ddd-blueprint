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
        RuleFor(x => x.Amount).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Currency).Length(3);
    }
}
