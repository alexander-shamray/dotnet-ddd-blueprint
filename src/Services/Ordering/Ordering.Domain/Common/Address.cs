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
/// hold an address database. <c>Country</c> is the partial exception: the check
/// is on the code's <em>shape</em>, and two letters is what separates an
/// ISO 3166-1 alpha-2 code from a caller reaching for alpha-3 or writing the
/// country's name.
/// </remarks>
/// <remarks>
/// <b>Shape, not membership — <c>ZZ</c> constructs.</b> The guard does not
/// check the code against the assigned set, and saying it did would be the
/// claim this type is least able to keep: the assigned set is data, it changes
/// without this code changing, and holding it here is the address database the
/// remark above refuses. <c>System.Globalization</c> is not the way out
/// either — <c>RegionInfo</c> answers from the container's ICU data, so the
/// same string would construct on one image and throw on another, and an
/// invariant that depends on which base image a service was built from is not
/// an invariant. A wrong-but-well-formed code is caught downstream by whoever
/// ships the parcel, which is the layer that knows.
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

        // Shape only — see the second remark. The message says what to send
        // rather than what was checked, because "two letters" is the fix a
        // caller sending "KAZ" or "Kazakhstan" needs to read.
        if (country is not { Length: 2 } || !country.All(char.IsAsciiLetter))
            throw new DomainException("Country must be a two-letter code (ISO 3166-1 alpha-2).");

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
