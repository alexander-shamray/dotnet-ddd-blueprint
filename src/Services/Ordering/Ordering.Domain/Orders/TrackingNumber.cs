using Common.Domain;

namespace Ordering.Domain.Orders;

/// <summary>
/// The carrier's handle on a shipment, carried on
/// <c>OrderShippedDomainEvent</c> so a customer can be told where the parcel
/// is without Ordering knowing anything about carriers.
/// </summary>
/// <remarks>
/// Presence and length only, on <see cref="PaymentReference"/>'s terms: the
/// value is minted by whichever carrier Shipping chose, and every carrier has
/// its own format.
/// </remarks>
public readonly record struct TrackingNumber
{
    // The column width the number is stored in (§7.2), stated once so the
    // guard and the mapping cannot disagree.
    public const int MaxLength = 100;

    public string Value { get; }

    private TrackingNumber(string value) => Value = value;

    public static TrackingNumber Of(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException("A tracking number cannot be blank.");
        if (value.Length > MaxLength)
            throw new DomainException($"A tracking number cannot exceed {MaxLength} characters.");

        return new TrackingNumber(value.Trim());
    }

    public override string ToString() => Value;
}
