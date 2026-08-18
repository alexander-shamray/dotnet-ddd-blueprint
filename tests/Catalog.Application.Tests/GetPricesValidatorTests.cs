using Catalog.Application.Products.GetPrices;
using FluentValidation.Results;
using Shouldly;
using Xunit;

namespace Catalog.Application.Tests;

/// <summary>
/// The input half of §9.7's hop. A query is validated exactly as a command is —
/// <c>ValidationBehavior</c> is unconstrained on purpose (§6.3) — and the
/// ceiling here is what keeps a caller-supplied id list from becoming an
/// unbounded read.
/// </summary>
public class GetPricesValidatorTests
{
    private static readonly GetPricesValidator Validator = new();

    private static ValidationResult Validate(int productCount, string currency) =>
        Validator.Validate(new GetPricesQuery(
            [.. Enumerable.Range(0, productCount).Select(_ => Guid.CreateVersion7())],
            currency));

    [Fact]
    public void An_empty_id_list_is_valid()
    {
        // Not NotEmpty, deliberately: asking for no prices is a legal request
        // with an empty answer, which the handler returns without touching the
        // database. Making this invalid would push the decision to the caller
        // for no gain and would make the handler's own guard unreachable.
        Validate(0, "GBP").IsValid.ShouldBeTrue();
    }

    [Fact]
    public void The_ceiling_is_inclusive()
    {
        Validate(GetPricesValidator.MaxProductIds, "GBP").IsValid.ShouldBeTrue();
    }

    [Fact]
    public void One_past_the_ceiling_is_refused()
    {
        // The pair above and below the boundary, rather than a number far past
        // it: an off-by-one in either direction passes one of these two and
        // fails the other, where "1000 is refused" passes against every
        // ceiling from 1 to 999.
        ValidationResult result = Validate(GetPricesValidator.MaxProductIds + 1, "GBP");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(f => f.PropertyName.Contains("ProductIds", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("GB")]
    [InlineData("GBPX")]
    [InlineData("12A")]
    [InlineData("GBP\n")]
    public void A_currency_that_is_not_three_letters_is_refused(string currency)
    {
        // The same rule PublishProductValidator applies to the currency it
        // stores. The last case is why the pattern ends \z rather than $:
        // .NET's $ matches before a trailing newline, so "GBP\n" would pass a
        // regex that looked correct.
        Validate(1, currency).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_three_letter_currency_is_accepted()
    {
        Validate(1, "gbp").IsValid.ShouldBeTrue();
    }
}
