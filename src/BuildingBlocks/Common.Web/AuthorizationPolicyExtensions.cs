using Microsoft.AspNetCore.Authorization;

namespace Common.Web;

/// <summary>
/// The one way a service registers a permission policy (§11.4). A host names
/// the permission and never the claim type, so an endpoint policy and the
/// resource-level check behind it cannot drift apart.
/// </summary>
public static class AuthorizationPolicyExtensions
{
    /// <summary>
    /// Requires the caller to carry <paramref name="permission"/> in the
    /// <see cref="PermissionClaim.Type"/> claim.
    /// </summary>
    /// <remarks>
    /// Permissions rather than roles, for §11.4's reason: role checks scattered
    /// through endpoints become unmaintainable once roles multiply, and the
    /// mapping from role to permission belongs in one place — here that place
    /// is the realm's claim mapper (§11.5), so the platform never sees a role
    /// at all.
    /// </remarks>
    public static AuthorizationPolicyBuilder RequirePermission(
        this AuthorizationPolicyBuilder builder,
        string permission) =>
        builder.RequireClaim(PermissionClaim.Type, permission);
}
