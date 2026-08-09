using Catalog.Domain.Common;
using Common.Domain;
using Shouldly;
using Xunit;

namespace Catalog.Domain.Tests;

/// <summary>
/// §5.3's always-valid value object: an invalid <see cref="Money"/> cannot be
/// constructed, so nothing downstream checks for one.
/// </summary>
public class MoneyTests
{
    [Fact]
    public void Of_normalises_the_currency_and_rounds_to_two_places_banker_style()
    {
        Money money = Money.Of(10.005m, "eur");

        money.Amount.ShouldBe(10.00m, "MidpointRounding.ToEven — 10.005 rounds down");
        money.Currency.ShouldBe("EUR");
    }

    [Fact]
    public void Of_refuses_a_negative_amount()
    {
        Should.Throw<DomainException>(() => Money.Of(-0.01m, "EUR"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("EU")]
    [InlineData("EURO")]
    public void Of_refuses_anything_but_a_three_letter_code(string currency)
    {
        Should.Throw<DomainException>(() => Money.Of(1m, currency));
    }

    [Fact]
    public void Zero_is_a_valid_amount()
    {
        Money.Zero("EUR").Amount.ShouldBe(0m);
    }

    [Fact]
    public void Addition_requires_the_same_currency()
    {
        Money euros = Money.Of(1m, "EUR");
        Money dollars = Money.Of(1m, "USD");

        Should.Throw<DomainException>(() => euros + dollars);
        (euros + Money.Of(2m, "EUR")).Amount.ShouldBe(3m);
    }

    [Fact]
    public void Multiplication_scales_the_amount_and_keeps_the_currency()
    {
        Money total = Money.Of(2.50m, "EUR") * 3;

        total.Amount.ShouldBe(7.50m);
        total.Currency.ShouldBe("EUR");
    }

    [Fact]
    public void Multiplication_refuses_a_negative_quantity()
    {
        // The operator must not be a back door past Of — a negative quantity
        // would construct the negative Money the factory refuses.
        Should.Throw<DomainException>(() => Money.Of(2.50m, "EUR") * -1);
    }

    [Fact]
    public void Two_instances_with_equal_values_are_interchangeable()
    {
        // §5.1's value-object test, on the record struct's own equality.
        Money.Of(5m, "EUR").ShouldBe(Money.Of(5m, "eur"));
    }
}
