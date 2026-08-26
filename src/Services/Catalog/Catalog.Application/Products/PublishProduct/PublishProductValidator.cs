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
        // An omitted CommandId binds as Guid.Empty, which is a single shared
        // key rather than an absent one — every caller of this command would
        // claim the same one, and the first success would be replayed to all
        // of them for a day. Validation is the OUTER behaviour (§6.3), so this
        // 400 is raised before any key is claimed.
        RuleFor(x => x.CommandId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);

        // The scheme is the point, not the length. This value is persisted and
        // served to every reader of the catalogue (§6.5), and a renderer
        // binding it into an href acts on whatever scheme it carries —
        // javascript: is stored XSS and data:text/html is the same by another
        // route. The consuming renderer cannot know the value was
        // caller-supplied, which is why the check belongs at the boundary that
        // does (§5.7's division: input, not a bug).
        //
        // Absolute, because a relative URI has no scheme to refuse and this
        // platform serves no origin the catalogue's images would be relative
        // to. The length rule stays: it is the column's bound (§7.2's 400-char
        // string convention) and a 400-character https URL is legal.
        //
        // Not an allow-list of image hosts. That is the stronger form and it
        // needs a list nothing in this repository has: §14.1 has no image host
        // and §4.1 plans no media service, so the list would be empty or
        // invented.
        RuleFor(x => x.ThumbnailUrl)
            .MaximumLength(400)
            .Must(BeAnHttpUrl)
            .WithMessage("'{PropertyName}' must be an absolute http or https URL.")
            .When(x => x.ThumbnailUrl is not null);

        // The upper bound is storage's: PriceAmount is decimal(19,4), fifteen
        // integer digits, and an amount past it would reach the transaction
        // and fail at SaveChanges as a 500 — the wrong statement about whose
        // fault a too-large price is. The bound excludes .995 exactly:
        // Money.Of rounds half-to-even at two places, so 999….995 becomes
        // the sixteen-digit 1e15 and overflows despite passing a bare < 1e15.
        RuleFor(x => x.Amount).NotNull().GreaterThanOrEqualTo(0).LessThan(999_999_999_999_999.995m);

        // NotEmpty first: Matches alone skips null, and a JSON "currency":
        // null would then reach Money.Of and turn malformed input into a 500.
        // Letters, not just length — "1$?" is three characters and no
        // currency, and Money.Of refuses it as a bug where this refuses it
        // as input (§5.7's division). \z, not $: .NET's $ matches before a
        // trailing newline, and "EUR\n" must fail here, not in the domain.
        RuleFor(x => x.Currency).NotEmpty().Matches(@"^[A-Za-z]{3}\z");
    }

    // A named method rather than an inline lambda: TryCreate's out parameter
    // does not fit the chain without wrapping it past the 120-column budget,
    // and the rule above already carries the argument for what it refuses.
    private static bool BeAnHttpUrl(string? candidate) =>
        Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
