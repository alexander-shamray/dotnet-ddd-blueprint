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
    ///
    /// <para>
    /// <b>Authentication is required here rather than assumed from the group.</b>
    /// <c>RequireClaim</c> alone evaluates the claims on
    /// <c>HttpContext.User</c> and asks nothing about whether anything
    /// authenticated it — the same independence that made
    /// <see cref="HttpContextCurrentUser"/> read only authenticated identities.
    /// Catalog is safe today because its route group adds
    /// <c>RequireAuthorization()</c> and the two policies combine, but that is
    /// a property of one caller rather than of this method, and the next
    /// service to map an endpoint with the named policy alone would authorize
    /// an unauthenticated principal that happens to carry the claim. A
    /// building block cannot rely on every caller remembering the other half.
    /// </para>
    /// </remarks>
    public static AuthorizationPolicyBuilder RequirePermission(
        this AuthorizationPolicyBuilder builder,
        string permission) =>
        builder
            .RequireAuthenticatedUser()
            .RequireClaim(PermissionClaim.Type, permission);
}
