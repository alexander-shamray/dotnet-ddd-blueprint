namespace Ordering.Domain.Orders;

/// <summary>
/// The order's owner, and the only thing §11.4's ownership check compares
/// against. A customer is another context's aggregate, so it is referenced by
/// identifier and never by object (§5.4).
/// </summary>
/// <remarks>
/// No <c>New()</c>, deliberately, and the omission is a boundary rather than
/// an oversight: Ordering never mints a customer. The value always arrives —
/// from <c>ICurrentUser.Id</c> on the write path (§11.4), and from the
/// aggregate on every read of one. A factory here would be a way to invent a
/// subject, which is exactly what the subject rule exists to prevent.
/// </remarks>
public readonly record struct CustomerId(Guid Value)
{
    public override string ToString() => Value.ToString();
}
