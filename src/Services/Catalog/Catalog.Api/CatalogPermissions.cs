namespace Catalog.Api;

/// <summary>
/// Catalog's permission vocabulary (§11.4). The strings are the contract with
/// the realm's claim mapper (§11.5); the policies registered from them in
/// <c>Program.cs</c> are how ASP.NET Core checks them.
/// </summary>
/// <remarks>
/// A constant rather than the literal §11.4 prints, because the name is written
/// twice — once where the policy is registered and once where an endpoint
/// names it — and §11.4's callout is that nothing checks the second against
/// the first. This closes the misspelling half at compile time; the
/// not-registered-at-all half still needs the test that callout asks for, and
/// <c>AuthorizationPolicyTests</c> is it. One constant catches one of the two,
/// which is a reason to have both and not a reason to skip either.
///
/// One entry, for one endpoint. Reading is anonymous — §10.2's
/// <c>catalog-public</c> route names YARP's reserved <c>anonymous</c> and the
/// listing itself says <c>AllowAnonymous</c> (§11.4) — so there is
/// no <c>catalog:read</c> here: a permission nothing requires is a name in a
/// realm nobody can act on.
/// </remarks>
public static class CatalogPermissions
{
    public const string Write = "catalog:write";
}
