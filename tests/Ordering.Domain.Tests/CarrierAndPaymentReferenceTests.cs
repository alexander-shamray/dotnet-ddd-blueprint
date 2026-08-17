using Common.Domain;
using Ordering.Domain.Orders;
using Shouldly;
using Xunit;

namespace Ordering.Domain.Tests;

/// <summary>
/// <see cref="PaymentReference"/> and <see cref="TrackingNumber"/>, whose
/// guards are deliberately the same pair — presence and length, and nothing
/// about format, because both values are minted by somebody else. One file
/// covers the two so the symmetry is visible: a guard added to one and not
/// the other reads as an omission here rather than as a difference nobody
/// wrote down.
/// </summary>
/// <remarks>
/// Until this file existed both types appeared in <c>OrderTests</c> only as
/// valid values passed to <c>ConfirmPayment</c> and <c>MarkShipped</c>, so
/// every guard in them was carried by inspection. The lengths are written as
/// <c>MaxLength</c> arithmetic rather than as literals: the constant is the
/// column width (§7.2), and a test spelling 100 would keep passing against a
/// widened column while the mapping and the guard disagreed.
/// </remarks>
public class CarrierAndPaymentReferenceTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_payment_reference_cannot_be_blank(string value)
    {
        Should.Throw<DomainException>(() => PaymentReference.Of(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_tracking_number_cannot_be_blank(string value)
    {
        Should.Throw<DomainException>(() => TrackingNumber.Of(value));
    }

    [Fact]
    public void A_payment_reference_of_exactly_the_column_width_is_accepted()
    {
        string value = new('p', PaymentReference.MaxLength);

        PaymentReference.Of(value).Value.ShouldBe(value);
    }

    [Fact]
    public void A_tracking_number_of_exactly_the_column_width_is_accepted()
    {
        string value = new('t', TrackingNumber.MaxLength);

        TrackingNumber.Of(value).Value.ShouldBe(value);
    }

    [Fact]
    public void A_payment_reference_one_character_past_the_column_width_is_refused()
    {
        // The boundary in both directions, because a guard written with the
        // wrong comparison passes every test that only ever exceeds it.
        Should.Throw<DomainException>(() =>
            PaymentReference.Of(new string('p', PaymentReference.MaxLength + 1)));
    }

    [Fact]
    public void A_tracking_number_one_character_past_the_column_width_is_refused()
    {
        Should.Throw<DomainException>(() =>
            TrackingNumber.Of(new string('t', TrackingNumber.MaxLength + 1)));
    }

    [Fact]
    public void Both_trim_what_they_accept()
    {
        // The trim runs after the length check, so a value that is only within
        // the width once trimmed is still refused — asserted by the pair above
        // rather than here. What this one fixes is that the stored value never
        // carries the whitespace a caller sent.
        PaymentReference.Of(" pay_123 ").Value.ShouldBe("pay_123");
        TrackingNumber.Of(" TRK-1 ").Value.ShouldBe("TRK-1");
    }
}
