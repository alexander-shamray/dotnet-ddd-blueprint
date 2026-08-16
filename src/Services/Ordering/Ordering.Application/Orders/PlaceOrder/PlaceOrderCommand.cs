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
/// §6.4 prints this record with a <c>CommandId</c> and
/// <c>IIdempotentCommand</c>. Both are omitted here on
/// <c>PublishProductCommand</c>'s terms: the chapter itself warns that the
/// field without the behaviour is unprotected, and <c>IdempotencyBehavior</c>
/// (§8.5) is the one seat still empty in the pipeline. The two join together.
/// </para>
/// </remarks>
public sealed record PlaceOrderCommand(
    IReadOnlyList<PlaceOrderItem> Items,
    AddressDto ShippingAddress,
    string Currency) : ICommand<Result<Guid>>;

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
