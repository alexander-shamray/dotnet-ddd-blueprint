namespace Ordering.Api;

/// <summary>
/// Ordering's permission vocabulary (§11.4). The strings are the contract with
/// the realm's claim mapper (§11.5); the policies registered from them in
/// <c>Program.cs</c> are how ASP.NET Core checks them.
/// </summary>
/// <remarks>
/// A constant rather than the literal, because the name is written twice —
/// once where the policy is registered and once where an endpoint names it —
/// and §11.4's callout is that nothing checks the second against the first.
/// This closes the misspelling half at compile time; the
/// not-registered-at-all half needs <c>AuthorizationPolicyTests</c>, which is
/// why both exist.
/// <para>
/// <b>Two entries, for two endpoints.</b> A third, <c>orders:read</c>, is
/// deliberately absent until there is a read endpoint to require it:
/// a service's vocabulary holds what its endpoints require and nothing else,
/// and a permission nothing requires is a dead name in the realm. §6.5's
/// query slice is not PR-20's — that one consumed Catalog's events into
/// <c>ordering.ProductPrices</c> and added no endpoint — so <c>orders:read</c>
/// still arrives with whichever PR gives this service a read endpoint.
/// </para>
/// <para>
/// <b><c>orders:admin</c> is not here, and that is the chapter's point rather
/// than an omission.</b> It is a <em>claim</em> that <c>CancelOrderHandler</c>
/// checks against a loaded aggregate, not a <em>policy</em> an endpoint names
/// — a question no endpoint could answer, because the order is not loaded when
/// the policy runs. The handler spells it as a literal for the same reason:
/// putting it in this class would make it look like the two policies beside
/// it, which is exactly the collapse §11.4 spends three paragraphs refusing.
/// </para>
/// </remarks>
public static class OrderingPermissions
{
    public const string Write = "orders:write";
    public const string Cancel = "orders:cancel";
}
