namespace Gateway.Api;

/// <summary>
/// The permissions the gateway itself names in a route (§10.2), as opposed to
/// the ones a service registers at its own composition root (§11.4). There is
/// exactly one, and it is here rather than in <c>Inventory.Api</c> because the
/// route that names it is the gateway's.
/// </summary>
/// <remarks>
/// <para>
/// A permission, not a role, and §10.2 says why the distinction is fragile
/// here in particular: a route file reads as infrastructure rather than as
/// authorization code, so <c>admin</c> — the role — is what creeps back in.
/// The name is <c>inventory:admin</c> and the shape is the platform's.
/// </para>
/// <para>
/// Constants rather than literals for §11.4's reason: the string is written
/// twice, in the policy registration and in <c>appsettings.json</c>, and only
/// one of those two sites is something the compiler can check. The other is
/// checked by <c>RouteConfigurationTests</c>, which is the half a constant
/// cannot close.
/// </para>
/// </remarks>
public static class GatewayPermissions
{
    public const string InventoryAdmin = "inventory:admin";
}
