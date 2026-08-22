using Common.Application;
using Ordering.Domain.Common;

namespace Ordering.Application.Orders.PlaceOrder;

/// <summary>
/// Place an order (§6.4). Returns the new order's id.
/// </summary>
/// <remarks>
/// <b>There is no <c>CustomerId</c> here, and the omission is the control.</b>
/// The subject of a write is bound from the principal and never from the
/// request (§11.4) — a field carrying it is one any authenticated caller sets
/// to somebody else's GUID, creating an order attributed to them and shipped
/// where the caller chose. A validator does not catch that: <c>NotEmpty()</c>
/// is true of a stranger's subject. The absence is the mechanism; checking a
/// field in the handler is sound where it is written and one omission away
/// from an IDOR in every slice copied from it.
/// <para>
/// <b><c>CommandId</c> is first, and it arrived with the behaviour rather than
/// before it.</b> §6.4 warns that the field without
/// <c>IdempotencyBehavior</c> (§8.5) is unprotected — a client that sends one
/// and retries would be told, by the field's presence, that the retry was
/// safe. The two joined in the same PR for that reason.
/// </para>
/// <para>
/// This is the platform's worst duplicate to suffer, which is why it is one of
/// the first two commands to opt in: a second dispatch does not overwrite
/// anything, it creates a second order, reserves stock for it and authorises a
/// second payment (§9.6).
/// </para>
/// </remarks>
public sealed record PlaceOrderCommand(
    Guid CommandId,
    IReadOnlyList<PlaceOrderItem> Items,
    AddressDto ShippingAddress,
    string Currency) : ICommand<Result<Guid>>, IIdempotentCommand
{
    /// <summary>
    /// Declared, never derived from the type name — a rename must not be able
    /// to change a live key (§8.5). Spelled in the domain's vocabulary rather
    /// than the CLR's, so that copying the type name back in reads as the
    /// mistake it is.
    /// </summary>
    public static string OperationName => "ordering.order.place";
}

public sealed record PlaceOrderItem(Guid ProductId, int Quantity);

/// <summary>
/// The wire shape of an address. A DTO rather than the domain type, because
/// <see cref="Address"/> has a private constructor and a factory that throws —
/// binding a request body straight onto it would turn a malformed address into
/// a 500 before any validator ran.
/// </summary>
public sealed record AddressDto(
    string Line1,
    string? Line2,
    string City,
    string PostalCode,
    string Country)
{
    /// <summary>
    /// Converts to the domain type. Called after validation, so the factory's
    /// guards are a backstop here rather than the primary check — they still
    /// run, because §5.3's always-valid principle does not take the validator's
    /// word for it.
    /// </summary>
    public Address ToDomain() => Address.Of(Line1, Line2, City, PostalCode, Country);
}
