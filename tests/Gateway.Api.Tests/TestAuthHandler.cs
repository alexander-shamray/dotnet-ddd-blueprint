using System.Security.Claims;
using System.Text.Encodings.Web;
using Common.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Gateway.Api.Tests;

/// <summary>
/// §12.4's test scheme, for the gateway. Tests state who they are in headers,
/// so the route policies of §10.2 are exercised against a real principal
/// rather than switched off.
/// </summary>
/// <remarks>
/// A second copy of <c>Catalog.TestSupport.TestAuthHandler</c>, and the
/// duplication is the design rather than an oversight: §4.3 permits exactly
/// one assembly to cross a service boundary and it holds integration event
/// records, so a gateway suite referencing Catalog's test library would be the
/// shared-kernel trap in test clothing. What the two copies share is the thing
/// that matters — <see cref="PermissionClaim.Type"/>, read from
/// <c>Common.Web</c>, so a handler that granted itself a permission under some
/// other claim name would grant nothing while looking like it granted
/// everything.
/// </remarks>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    // SchemeName, not Scheme. AuthenticationHandler<T> already declares a
    // protected Scheme, so the constant §12.4 prints hides it and CS0108 is an
    // error under ADR-019.
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";
    public const string PermissionsHeader = "X-Test-Permissions";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // No header means anonymous, not "authenticated as nobody" — otherwise
        // every 401 test silently passes.
        if (!Request.Headers.TryGetValue(UserHeader, out StringValues userId))
            return Task.FromResult(AuthenticateResult.NoResult());

        List<Claim> claims = [new(ClaimTypes.NameIdentifier, userId.ToString())];

        if (Request.Headers.TryGetValue(PermissionsHeader, out StringValues granted))
        {
            claims.AddRange(
                granted
                    .ToString()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => new Claim(PermissionClaim.Type, p)));
        }

        ClaimsPrincipal principal = new(new ClaimsIdentity(claims, SchemeName));

        return Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
