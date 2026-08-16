using Common.Domain;

namespace Ordering.Domain.Common;

/// <summary>
/// Where an order ships. A value object on §5.3's terms — no identity, always
/// valid, and compared by its parts, which is what makes two orders to the
/// same address genuinely equal rather than coincidentally so.
/// </summary>
/// <remarks>
/// Deliberately not validated beyond presence. Postal codes and country
/// subdivisions differ by jurisdiction, and a guard that encodes one country's
/// format refuses valid addresses in every other — the always-valid principle
/// says an invalid instance must be unconstructible, not that this type should
/// hold an address database. <c>Country</c> is the exception, because ISO 3166-1
/// alpha-2 is a closed two-letter set and a three-letter code here is a caller
/// using the wrong standard rather than an unusual address.
/// </remarks>
public sealed record Address
{
    public string Line1 { get; }
    public string? Line2 { get; }
    public string City { get; }
    public string PostalCode { get; }
    public string Country { get; }

    private Address(string line1, string? line2, string city, string postalCode, string country)
    {
        Line1 = line1;
        Line2 = line2;
        City = city;
        PostalCode = postalCode;
        Country = country;
    }

    public static Address Of(string line1, string? line2, string city, string postalCode, string country)
    {
        EnsurePresent(line1, nameof(line1));
        EnsurePresent(city, nameof(city));
        EnsurePresent(postalCode, nameof(postalCode));

        if (country is not { Length: 2 } || !country.All(char.IsAsciiLetter))
            throw new DomainException("Country must be a 2-letter ISO 3166-1 code.");

        // Line2 is optional, and an empty string is not a second line — it is
        // the absence of one arriving through a JSON body that spelt it "".
        // Normalising here means every consumer sees one representation.
        return new Address(
            line1.Trim(),
            string.IsNullOrWhiteSpace(line2) ? null : line2.Trim(),
            city.Trim(),
            postalCode.Trim(),
            country.ToUpperInvariant());
    }

    private static void EnsurePresent(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException($"An address needs a {field}.");
    }
}
