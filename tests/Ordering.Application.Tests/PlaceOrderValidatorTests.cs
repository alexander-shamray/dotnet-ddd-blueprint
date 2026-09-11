using Common.Contracts.Ordering.V1;
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

    /// <summary>One product, so two lines can name the same one.</summary>
    private static readonly Guid Product = Guid.CreateVersion7();

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
        Validator.Validate(WithItems(OrderLimits.MaxLines)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void An_order_past_the_item_ceiling_is_a_validation_failure()
    {
        // The defect this rule closes is not a business one. ProjectedPriceReader
        // expands each product id into a SQL parameter and adds @Currency
        // beside them, and SQL Server's limit is 2,100 — so before the ceiling
        // existed, an authenticated caller sending enough items turned a
        // well-formed request into a 500 rather than a 400. Found by Copilot.
        ValidationResult result = Validator.Validate(WithItems(OrderLimits.MaxLines + 1));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == nameof(PlaceOrderCommand.Items));
    }

    [Fact]
    public void The_ceiling_is_well_inside_what_the_price_query_can_ask_for()
    {
        // The rule's reason, asserted rather than left in a comment: the
        // ceiling is only correct while it stays under SQL Server's parameter
        // limit with room for @Currency. Raising MaxLines past this fails
        // here, which is the moment to batch the query instead.
        const int sqlServerParameterLimit = 2100;

        OrderLimits.MaxLines.ShouldBeLessThan(sqlServerParameterLimit - 1);
    }

    [Fact]
    public void Two_lines_for_one_product_may_not_exceed_the_quantity_ceiling_between_them()
    {
        // Order.AddLine merges two lines for one product, and PlaceOrderHandler
        // calls that legitimate — so the per-item rule is not a bound on the
        // order at all: both items below are inside it and the order they place
        // is for twice the ceiling. Found by Copilot, on the pull request that
        // gave this bound an owner and claimed it was the order's.
        PlaceOrderCommand command = new(
            Guid.CreateVersion7(),
            [new PlaceOrderItem(Product, OrderLimits.MaxQuantity), new PlaceOrderItem(Product, 1)],
            AnAddress(),
            "EUR");

        ValidationResult result = Validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == nameof(PlaceOrderCommand.Items));
    }

    [Fact]
    public void Two_lines_for_one_product_at_the_ceiling_between_them_are_accepted()
    {
        // The boundary from below, and it is the assertion that keeps the rule
        // above from being a ban on repeating a product at all.
        PlaceOrderCommand command = new(
            Guid.CreateVersion7(),
            [
                new PlaceOrderItem(Product, OrderLimits.MaxQuantity - 1),
                new PlaceOrderItem(Product, 1)
            ],
            AnAddress(),
            "EUR");

        Validator.Validate(command).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_quantity_that_overflows_the_merge_is_a_400_and_not_a_500()
    {
        // Enumerable.Sum over int is checked, so the merged-quantity rule threw
        // OverflowException on two int.MaxValue lines before RuleForEach could
        // report either as invalid. The assertion is that Validate returns
        // rather than throws; IsValid being false is the easy half — the same
        // shape as the null-item-list test below. Found by Copilot.
        PlaceOrderCommand command = new(
            Guid.CreateVersion7(),
            [new PlaceOrderItem(Product, int.MaxValue), new PlaceOrderItem(Product, int.MaxValue)],
            AnAddress(),
            "EUR");

        ValidationResult result = Should.NotThrow(() => Validator.Validate(command));

        result.IsValid.ShouldBeFalse();
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
    public void An_omitted_CommandId_is_refused_before_any_key_is_claimed()
    {
        // The guard is load-bearing and nothing else pins it: an omitted
        // CommandId binds as Guid.Empty, which is not an ABSENT key but a
        // single SHARED one, so every caller omitting it claims the same
        // Redis key and the first success is replayed to all of them for a
        // day (§8.5).
        //
        // Every other case in this file builds its command through
        // WithItems, which mints a fresh id — so deleting the NotEmpty rule
        // left the whole suite green. A rule whose only protection is that
        // nobody deletes it is not protected.
        PlaceOrderCommand command = WithItems(1) with { CommandId = Guid.Empty };

        ValidationResult result = Validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == nameof(PlaceOrderCommand.CommandId));
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
