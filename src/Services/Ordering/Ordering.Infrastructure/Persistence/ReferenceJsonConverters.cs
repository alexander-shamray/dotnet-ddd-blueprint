using System.Text.Json;
using System.Text.Json.Serialization;
using Ordering.Domain.Orders;

namespace Ordering.Infrastructure.Persistence;

/// <summary>
/// <see cref="PaymentReference"/> on the outbox's <c>Local</c> lane, carried by
/// <c>OrderConfirmedDomainEvent</c>.
/// </summary>
/// <remarks>
/// The silent shape <see cref="MoneyJsonConverter"/> describes, one property
/// wide: a readonly record struct with a private constructor deserialises to
/// its default rather than throwing, so a payment reference would come back
/// null and the incident would be a support question nobody could answer.
/// <para>
/// Written as a bare JSON string rather than an object wrapping a
/// <c>Value</c> member. The type exists to stop a raw string being passed
/// where a reference belongs; it is still one string, and a payload saying so
/// is what a human reading an outbox row during an incident needs.
/// </para>
/// </remarks>
internal sealed class PaymentReferenceJsonConverter : JsonConverter<PaymentReference>
{
    public override PaymentReference Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        PaymentReference.Of(
            reader.TokenType == JsonTokenType.String
                ? reader.GetString()!
                : throw new JsonException(
                    $"Expected a string for {nameof(PaymentReference)}, found {reader.TokenType}."));

    public override void Write(Utf8JsonWriter writer, PaymentReference value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

/// <summary>
/// <see cref="TrackingNumber"/> on the <c>Local</c> lane, carried by
/// <c>OrderShippedDomainEvent</c>. The converter above's argument, for the
/// other single-string value object.
/// </summary>
internal sealed class TrackingNumberJsonConverter : JsonConverter<TrackingNumber>
{
    public override TrackingNumber Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        TrackingNumber.Of(
            reader.TokenType == JsonTokenType.String
                ? reader.GetString()!
                : throw new JsonException(
                    $"Expected a string for {nameof(TrackingNumber)}, found {reader.TokenType}."));

    public override void Write(Utf8JsonWriter writer, TrackingNumber value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
