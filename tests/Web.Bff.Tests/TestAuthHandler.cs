using System.Security.Claims;
using System.Text.Encodings.Web;
using Common.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Web.Bff.Tests;

/// <summary>
/// §12.4's test scheme, for the BFF. Tests state who they are in headers, so
/// the endpoint group's <c>RequireAuthorization()</c> is exercised against a
/// real principal rather than switched off.
/// </summary>
/// <remarks>
/// The <i>third</i> copy of this handler — Catalog.TestSupport has one and
/// Gateway.Api.Tests has the second — and the duplication is still the design
/// rather than an oversight: §4.3 permits exactly one assembly to cross a
/// service boundary and it holds integration event records, so a BFF suite
/// referencing Catalog's test library would be the shared-kernel trap in test
/// clothing. What the copies share is the thing that matters —
/// <see cref="PermissionClaim.Type"/>, read from <c>Common.Web</c>, so a
/// handler that granted itself a permission under some other claim name would
/// grant nothing while looking like it granted everything.
/// <para>
/// Three copies is the point at which a reader should ask whether §4.1 wants a
/// test library for hosts. It does not, yet: a <c>Platform.TestSupport</c>
/// would be referenced by three suites that share nothing else, and §4.1's own
/// rule for <c>Catalog.TestSupport</c> is that a shared fixture earns a project
/// when two suites need <i>it</i> — not when two suites happen to need the same
/// forty lines.
/// </para>
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
            claims.AddRange(
                granted
                    .ToString()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => new Claim(PermissionClaim.Type, p)));

        ClaimsPrincipal principal = new(new ClaimsIdentity(claims, SchemeName));

        return Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
