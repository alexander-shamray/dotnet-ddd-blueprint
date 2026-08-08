using Catalog.Application.Products.PublishProduct;
using FluentValidation.Results;
using Shouldly;
using Xunit;

namespace Catalog.Application.Tests;

/// <summary>
/// The user-input boundary, tested without the pipeline: the behaviour that
/// runs validators is Common.Application's and already covered there; what is
/// this service's is which requests these rules refuse.
/// </summary>
public class PublishProductValidatorTests
{
    private static readonly PublishProductValidator Validator = new();

    private static PublishProductCommand Valid() =>
        new("Walnut desk", "https://cdn.example/desk.jpg", 19.99m, "EUR");

    [Fact]
    public void A_valid_command_passes()
    {
        Validator.Validate(Valid()).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_missing_thumbnail_is_valid()
    {
        Validator.Validate(Valid() with { ThumbnailUrl = null }).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_zero_amount_is_valid()
    {
        // Free is a price; Money.Of agrees. The refused half is negative.
        Validator.Validate(Valid() with { Amount = 0m }).IsValid.ShouldBeTrue();
    }

    public static TheoryData<string, PublishProductCommand> Invalid() => new()
    {
        { nameof(PublishProductCommand.Name), Valid() with { Name = "" } },
        { nameof(PublishProductCommand.Name), Valid() with { Name = new string('x', 201) } },
        { nameof(PublishProductCommand.ThumbnailUrl), Valid() with { ThumbnailUrl = new string('x', 401) } },
        { nameof(PublishProductCommand.Amount), Valid() with { Amount = -0.01m } },
        { nameof(PublishProductCommand.Currency), Valid() with { Currency = "EURO" } },
        { nameof(PublishProductCommand.Currency), Valid() with { Currency = "" } }
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void An_invalid_field_fails_naming_the_field(string field, PublishProductCommand command)
    {
        ValidationResult result = Validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(
            f => f.PropertyName == field,
            "the 400's errors extension is field-keyed (§10.5), so the name is the contract");
    }
}
