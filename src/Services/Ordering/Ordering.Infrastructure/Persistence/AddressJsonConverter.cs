using System.Text.Json;
using System.Text.Json.Serialization;
using Ordering.Domain.Common;

namespace Ordering.Infrastructure.Persistence;

/// <summary>
/// §5.3's <c>Address</c> on the outbox's <c>Local</c> lane, carried by
/// <c>OrderConfirmedDomainEvent</c>.
/// </summary>
/// <remarks>
/// <b>The failure mode differs from <see cref="MoneyJsonConverter"/>'s, and is
/// louder by luck rather than by design.</b> <c>Address</c> is a sealed record
/// <em>class</em> with a private constructor, so there is no parameterless
/// constructor to fall back to and <c>System.Text.Json</c> throws on read
/// instead of silently producing a default. That makes it the better of the
/// two failures and still a failure — a domain event that cannot be read back
/// is an outbox row that can never be dispatched, and the dispatcher retries
/// it to its attempt cap before giving up loudly.
/// <para>
/// Reading goes through <see cref="Address.Of"/> for the reason the money
/// converter gives: a payload is input, and the always-valid principle has no
/// exemption for input this service wrote itself.
/// </para>
/// </remarks>
internal sealed class AddressJsonConverter : JsonConverter<Address>
{
    public override Address Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"Expected an object for {nameof(Address)}, found {reader.TokenType}.");

        string? line1 = null;
        string? line2 = null;
        string? city = null;
        string? postalCode = null;
        string? country = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            string property = reader.GetString()!;
            reader.Read();

            // Ordinal and case-sensitive, and the whole unknown value skipped
            // rather than its first token — both for the reasons spelled out
            // on the money converter beside this one.
            if (property == nameof(Address.Line1))
                line1 = reader.GetString();
            else if (property == nameof(Address.Line2))
                line2 = reader.GetString();
            else if (property == nameof(Address.City))
                city = reader.GetString();
            else if (property == nameof(Address.PostalCode))
                postalCode = reader.GetString();
            else if (property == nameof(Address.Country))
                country = reader.GetString();
            else
                reader.Skip();
        }

        // Line2 is absent from this list deliberately: it is optional on the
        // domain type, so a payload without it is complete rather than partial.
        if (line1 is null || city is null || postalCode is null || country is null)
        {
            throw new JsonException(
                $"An {nameof(Address)} payload needs {nameof(Address.Line1)}, {nameof(Address.City)}, " +
                $"{nameof(Address.PostalCode)} and {nameof(Address.Country)}. A row written before a " +
                "rename is the likely cause (§9.4).");
        }

        return Address.Of(line1, line2, city, postalCode, country);
    }

    public override void Write(Utf8JsonWriter writer, Address value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString(nameof(Address.Line1), value.Line1);

        // Written even when null, so the payload states the absence rather
        // than leaving a reader to infer it from a missing member — the two
        // are different facts once §9.2 makes an added member ordinary.
        writer.WriteString(nameof(Address.Line2), value.Line2);
        writer.WriteString(nameof(Address.City), value.City);
        writer.WriteString(nameof(Address.PostalCode), value.PostalCode);
        writer.WriteString(nameof(Address.Country), value.Country);
        writer.WriteEndObject();
    }
}
