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
    private ClaimsPrincipal? User => accessor.HttpContext?.User;

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated == true;

    /// <summary>
    /// <c>ClaimTypes.NameIdentifier</c>, which is where Keycloak's <c>sub</c>
    /// lands under the inbound claim mapping <c>JwtBearerOptions</c> leaves on
    /// (§11.3). §10.3's rate-limit partition key and the test scheme read the
    /// same claim, so all three agree on which claim identifies a caller.
    /// </summary>
    public Guid Id => Guid.Parse(
        User?.FindFirstValue(ClaimTypes.NameIdentifier) ??
            throw new InvalidOperationException(
                "No authenticated caller. Guard with IsAuthenticated — a handler " +
                "reached by a consumer (§9.4) has no HttpContext."));

    public bool HasPermission(string permission) =>
        User?.HasClaim(PermissionClaim.Type, permission) == true;
}
