using System.Text.Json;
using System.Text.Json.Serialization;
using Ordering.Domain.Common;

namespace Ordering.Infrastructure.Persistence;

/// <summary>
/// §5.3's <c>Money</c> on the outbox's <c>Local</c> lane. The same job
/// <see cref="OrderConfiguration"/>'s <c>ComplexProperty</c> does for columns,
/// for the other persisted format — and in the same assembly, for the same
/// reason: the domain type must not know about either.
/// </summary>
/// <remarks>
/// <b>Without this the failure is silent.</b> <c>Money</c> is a readonly
/// record struct with a private constructor and two get-only properties.
/// <c>System.Text.Json</c> does not refuse that shape: a struct always has a
/// parameterless constructor, so it builds the default, finds no setter to
/// call, and hands back <c>Amount = 0</c> with a null <c>Currency</c>. A
/// projection then runs on a price of zero and nothing anywhere says so.
/// <para>
/// Reading goes through <see cref="Money.Of"/>, not around it. A payload is
/// input like any other, and the always-valid principle does not get an
/// exemption for input this service wrote itself — a row hand-edited during an
/// incident is exactly the case where the guard should fire.
/// </para>
/// </remarks>
internal sealed class MoneyJsonConverter : JsonConverter<Money>
{
    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"Expected an object for {nameof(Money)}, found {reader.TokenType}.");

        decimal? amount = null;
        string? currency = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            string property = reader.GetString()!;
            reader.Read();

            // Ordinal and case-sensitive, matching the options this converter
            // is registered on: a payload that only round-trips because
            // matching is lenient will not survive a rename (§9.4).
            if (property == nameof(Money.Amount))
                amount = reader.GetDecimal();
            else if (property == nameof(Money.Currency))
                currency = reader.GetString();
            else
                // Skip the whole value, not just its first token. Without
                // this an unknown property whose value is an object or an
                // array leaves the reader *inside* it: a nested `Amount`
                // would be taken for this one, and the nested `EndObject`
                // would end the loop early — so a payload written by a later
                // version, which §9.2 makes an ordinary thing to meet,
                // deserialises to the wrong money without anything throwing.
                reader.Skip();
        }

        if (amount is null || currency is null)
            throw new JsonException(
                $"A {nameof(Money)} payload needs both {nameof(Money.Amount)} and " +
                $"{nameof(Money.Currency)}. A row written before a rename is the likely cause (§9.4).");

        return Money.Of(amount.Value, currency);
    }

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber(nameof(Money.Amount), value.Amount);
        writer.WriteString(nameof(Money.Currency), value.Currency);
        writer.WriteEndObject();
    }
}
