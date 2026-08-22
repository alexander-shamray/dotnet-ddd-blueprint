using FluentValidation.Results;
using Ordering.Application.Orders.PlaceOrder;
using Shouldly;
using Xunit;

namespace Ordering.Application.Tests;

/// <summary>
/// §6.4's validator, unit-tested — no container and no database, because
/// everything it decides is about the shape of the request (§5.7's division).
/// </summary>
public class PlaceOrderValidatorTests
{
    private static readonly PlaceOrderValidator Validator = new();

    private static AddressDto AnAddress() =>
        new("1 Test Street", null, "Almaty", "050000", "KZ");

    private static PlaceOrderCommand WithItems(int count) =>
        new(
            Guid.CreateVersion7(),
            [.. Enumerable.Range(0, count).Select(_ => new PlaceOrderItem(Guid.CreateVersion7(), 1))],
            AnAddress(),
            "EUR");

    [Fact]
    public void An_order_at_the_item_ceiling_is_accepted()
    {
        // The boundary from below. Without this the rule could be off by one
        // in the strict direction and only the rejection test would notice —
        // which it would not, because it asserts a failure either way.
        Validator.Validate(WithItems(PlaceOrderValidator.MaxItems)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void An_order_past_the_item_ceiling_is_a_validation_failure()
    {
        // The defect this rule closes is not a business one. ProjectedPriceReader
        // expands each product id into a SQL parameter and adds @Currency
        // beside them, and SQL Server's limit is 2,100 — so before the ceiling
        // existed, an authenticated caller sending enough items turned a
        // well-formed request into a 500 rather than a 400. Found by Copilot.
        ValidationResult result = Validator.Validate(WithItems(PlaceOrderValidator.MaxItems + 1));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == nameof(PlaceOrderCommand.Items));
    }

    [Fact]
    public void The_ceiling_is_well_inside_what_the_price_query_can_ask_for()
    {
        // The rule's reason, asserted rather than left in a comment: the
        // ceiling is only correct while it stays under SQL Server's parameter
        // limit with room for @Currency. Raising MaxItems past this fails
        // here, which is the moment to batch the query instead.
        const int sqlServerParameterLimit = 2100;

        PlaceOrderValidator.MaxItems.ShouldBeLessThan(sqlServerParameterLimit - 1);
    }

    [Fact]
    public void A_null_item_list_is_a_400_and_not_a_500()
    {
        // An explicit JSON "items": null binds as null. FluentValidation runs
        // every validator in a rule by default, so without Cascade(Stop) the
        // size predicate dereferences it after NotEmpty has already recorded
        // the failure — and a malformed request arrives as a 500. The
        // assertion is that Validate returns rather than throws; IsValid
        // being false is the easy half.
        PlaceOrderCommand command = new(Guid.CreateVersion7(), null!, AnAddress(), "EUR");

        ValidationResult result = Should.NotThrow(() => Validator.Validate(command));

        result.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void An_empty_order_is_refused_at_the_edge_as_well_as_in_the_domain()
    {
        // Order.Place also refuses this, and both are wanted: the domain rule
        // is the invariant, and this is the 400 that keeps a well-formed
        // refusal from arriving as a 500 (§5.7).
        Validator.Validate(WithItems(0)).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("EURO")]
    [InlineData("EU")]
    [InlineData("E1R")]
    [InlineData("EUR\n")]
    public void A_currency_that_is_not_three_letters_is_refused(string currency)
    {
        // "EUR\n" is the case \z catches and $ does not: .NET's $ matches
        // before a trailing newline, so the domain would have seen it.
        PlaceOrderCommand command = WithItems(1) with { Currency = currency };

        Validator.Validate(command).IsValid.ShouldBeFalse();
    }
}
