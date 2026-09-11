using FluentValidation;

namespace Catalog.Application.Products.GetPrices;

/// <summary>
/// The input half of §9.7's hop. A query is validated exactly as a command is —
/// <c>ValidationBehavior</c> is unconstrained on purpose (§6.3) — and here that
/// is what keeps a caller-supplied id list from becoming an unbounded read.
/// </summary>
public sealed class GetPricesValidator : AbstractValidator<GetPricesQuery>
{
    /// <summary>
    /// The most ids one request may carry. A constant and not configuration,
    /// on §15.4's test: it would be the same number in Compose, in the fixture
    /// and in production, so binding it to a section would give it a deployment
    /// obligation and nothing to validate.
    /// </summary>
    /// <remarks>
    /// What the ceiling protects is the parameter budget: Dapper expands
    /// <c>IN @ProductIds</c> into one parameter per id, and SQL Server refuses
    /// a batch past 2,100 of them with an error naming neither this query nor
    /// its caller. A value well inside that is also a screen's worth of
    /// products, which is what makes it a comfortable bound rather than a
    /// tight one.
    /// <para>
    /// <b>It is not the order's bound and must not be cited as one.</b>
    /// <c>OrderLimits.MaxLines</c> bounds how many lines an order may carry and
    /// this bounds how many ids one price query may name; they are equal today
    /// and nothing holds them together (ADR-045). Deriving either from the
    /// other is how a change to one silently becomes a claim about the other.
    /// </para>
    /// </remarks>
    public const int MaxProductIds = 100;

    public GetPricesValidator()
    {
        // NotNull rather than NotEmpty: an empty id list is a legal request
        // with an empty answer, which the handler returns without touching the
        // database. Only a null collection is malformed.
        RuleFor(x => x.ProductIds).NotNull();
        RuleFor(x => x.ProductIds.Count).LessThanOrEqualTo(MaxProductIds).When(x => x.ProductIds is not null);

        // The same rule PublishProductValidator applies to the currency it
        // stores, for the same reasons — NotEmpty ahead of Matches because
        // Matches alone skips null, and \z rather than $ because .NET's $
        // matches before a trailing newline.
        RuleFor(x => x.Currency).NotEmpty().Matches(@"^[A-Za-z]{3}\z");
    }
}
