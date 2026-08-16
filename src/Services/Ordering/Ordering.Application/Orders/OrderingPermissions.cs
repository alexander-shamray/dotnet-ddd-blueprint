namespace Ordering.Application.Orders;

/// <summary>
/// Ordering's permission vocabulary (§11.4). The strings are the contract with
/// the realm's claim mapper (§11.5); the policies registered from them in
/// <c>Program.cs</c> are how ASP.NET Core checks them at an endpoint, and
/// <c>ICurrentUser.HasPermission</c> is how a handler checks one against a
/// loaded aggregate.
/// </summary>
/// <remarks>
/// <b>In Application, where <c>CatalogPermissions</c> is in Api, and the
/// difference is a consequence rather than a divergence.</b> Catalog's
/// vocabulary is read only by endpoints, so it lives beside them. Ordering's
/// is read by <c>CancelOrderHandler</c> as well, and Application cannot
/// reference Api — so the constants sit at the layer that both callers can
/// reach. Putting them in Api and letting the handler spell the literal would
/// reintroduce exactly the misspelling this type closes.
/// <para>
/// <c>OrdersAdmin</c> is a <b>claim</b>, not one of the registered policies,
/// and §11.4 is emphatic about the distinction because the names look alike. A
/// policy is a registered rule that gates an endpoint; a claim is what the
/// token carries, and only the second is what <c>HasPermission</c> reads.
/// Nothing forbids a policy requiring this claim when the administrative
/// command lands — what is ruled out is treating the claim as though a policy
/// of that name already existed, because a policy nobody registered resolves
/// to nothing.
/// </para>
/// </remarks>
public static class OrderingPermissions
{
    /// <summary>Placing an order. Registered as a policy on the write endpoint.</summary>
    public const string Write = "orders:write";

    /// <summary>Cancelling one. A separate policy, because §11.4's vocabulary distinguishes them.</summary>
    public const string Cancel = "orders:cancel";

    /// <summary>
    /// Acting on an order the caller does not own. A claim read by
    /// <c>CancelOrderHandler</c> and by no endpoint policy — see the remark
    /// above before registering one.
    /// </summary>
    public const string OrdersAdmin = "orders:admin";
}
