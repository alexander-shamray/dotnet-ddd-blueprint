using System.Security.Claims;
using Common.Application;
using Microsoft.AspNetCore.Http;

namespace Common.Web;

/// <summary>
/// §11.4's one implementation of <see cref="ICurrentUser"/>, reading the
/// principal <c>UseAuthentication</c> put on the context. Registered scoped by
/// <c>AddCommonWebDefaults</c> — it is per request — beside the accessor it
/// depends on, because ASP.NET Core registers no
/// <see cref="IHttpContextAccessor"/> by default and omitting that line fails
/// <c>ValidateOnBuild</c> rather than the first ownership check.
/// </summary>
/// <remarks>
/// <c>Common.Web</c> rather than a service's Infrastructure, and the chapter
/// was amended: the type names no service, and it cannot live in
/// <c>Common.Infrastructure</c> at all — that project takes no
/// <c>FrameworkReference</c>, and <see cref="IHttpContextAccessor"/> arrives
/// with one. This is the only building block that has it.
/// </remarks>
public sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    /// <summary>
    /// The caller's authenticated identities and nothing else — every member
    /// below reads this rather than <c>HttpContext.User</c>, so a claim can
    /// never be answered from an identity <see cref="IsAuthenticated"/> denies.
    /// A <see cref="ClaimsIdentity"/> with no authentication type carries
    /// claims perfectly happily; the two are independent, and reading the
    /// claims without the check is what turns the interface's fail-closed
    /// contract into a suggestion.
    /// </summary>
    /// <remarks>
    /// The identities are filtered rather than the principal tested, because
    /// <see cref="ClaimsPrincipal.Identity"/> is the <em>primary</em> identity
    /// while <c>FindFirst</c> and <c>HasClaim</c> search every one of them. A
    /// principal whose first identity is authenticated and whose second is not
    /// would otherwise answer with the second's subject and grant the second's
    /// permissions — the same defect as reading <c>HttpContext.User</c>
    /// directly, one layer in, and a shape any host authenticating over two
    /// schemes can produce.
    /// </remarks>
    private ClaimsPrincipal? Caller
    {
        get
        {
            ClaimsIdentity[] authenticated =
                [.. accessor.HttpContext?.User.Identities.Where(i => i.IsAuthenticated) ?? []];

            return authenticated.Length == 0 ? null : new ClaimsPrincipal(authenticated);
        }
    }

    public bool IsAuthenticated => Caller is not null;

    /// <summary>
    /// <c>ClaimTypes.NameIdentifier</c>, which is where Keycloak's <c>sub</c>
    /// lands under the inbound claim mapping <c>JwtBearerOptions</c> leaves on
    /// (§11.3). §10.3's rate-limit partition key and the test scheme read the
    /// same claim, so all three agree on which claim identifies a caller.
    /// </summary>
    public Guid Id => Guid.Parse(
        Caller?.FindFirstValue(ClaimTypes.NameIdentifier) ??
            throw new InvalidOperationException(
                $"No subject: either there is no authenticated caller — guard with " +
                $"IsAuthenticated, since a handler reached by a consumer (§9.4) has no " +
                $"HttpContext — or the principal carries no '{ClaimTypes.NameIdentifier}' " +
                "claim, which means the identity provider is not issuing 'sub' (§11.5)."));

    public bool HasPermission(string permission) =>
        Caller?.HasClaim(PermissionClaim.Type, permission) == true;
}
