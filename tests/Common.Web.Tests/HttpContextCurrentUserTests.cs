using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>
/// §11.4's port over a principal. The subject rule rests entirely on this
/// type: a handler asks it "whose record is this" instead of reading a field
/// off the request, so every one of its answers is a security decision.
/// </summary>
/// <remarks>
/// Over <see cref="IHttpContextAccessor"/> directly rather than through a
/// server, because what is under test is how a principal is read and not how
/// one is issued — the second is <c>TestAuthHandler</c>'s job and
/// <c>Catalog.Api.Tests</c> exercises it over the wire. The claims here are
/// spelt with the same types the JWT handler produces (§11.3), which is what
/// keeps the two ends of that agreement together.
/// </remarks>
public class HttpContextCurrentUserTests
{
    private static HttpContextCurrentUser For(params Claim[] claims)
    {
        // An identity constructed with an authentication type is authenticated;
        // one without is not, and that is the distinction the anonymous case
        // below turns on rather than an absent context.
        DefaultHttpContext context = new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"))
        };

        return new HttpContextCurrentUser(new HttpContextAccessor { HttpContext = context });
    }

    [Fact]
    public void Reads_the_subject_from_the_name_identifier_claim()
    {
        Guid subject = Guid.CreateVersion7();

        HttpContextCurrentUser user = For(new Claim(ClaimTypes.NameIdentifier, subject.ToString()));

        user.IsAuthenticated.ShouldBeTrue();
        user.Id.ShouldBe(subject);
    }

    [Fact]
    public void A_display_name_is_not_the_subject()
    {
        // §11.3 sets NameClaimType to preferred_username, and reading
        // Identity.Name as the key to a record would work in every test and
        // break the first time somebody changed their username. The subject
        // claim is the only one this type will answer with, so a principal
        // carrying a name and no NameIdentifier has no subject at all.
        HttpContextCurrentUser user = For(new Claim("preferred_username", "ada"));

        Should.Throw<InvalidOperationException>(() => user.Id);
    }

    [Fact]
    public void No_principal_is_anonymous_and_has_no_subject()
    {
        // The message-borne path (§9.4): a handler reached by a consumer has no
        // HttpContext at all. It throws rather than answering Guid.Empty,
        // because an empty subject compares unequal to every real one — so a
        // forgotten guard becomes a refusal nobody can explain instead of an
        // exception naming the mistake. The direction a mistake should go is
        // loud, not quiet.
        HttpContextCurrentUser user = new(new HttpContextAccessor());

        user.IsAuthenticated.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => user.Id);
    }

    [Fact]
    public void An_unauthenticated_identity_is_not_a_caller()
    {
        // The other half, and the one that is easy to miss: a context exists
        // and User is non-null, but nothing authenticated it. ASP.NET Core puts
        // exactly this on every anonymous request, so a check reading
        // "User is not null" would treat every caller as signed in.
        DefaultHttpContext context = new();

        HttpContextCurrentUser user = new(new HttpContextAccessor { HttpContext = context });

        user.IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public void An_unauthenticated_identity_carrying_claims_answers_none_of_them()
    {
        // The sharp version of the test above, and the one that fails if any
        // member reads HttpContext.User directly. Claims and authentication are
        // independent: a ClaimsIdentity with no authentication type holds
        // whatever claims it was built with, quite happily, and IsAuthenticated
        // is still false. So a member that reads the claim without asking the
        // question answers an identity the interface says is not a caller —
        // which is a permission granted to nobody in particular.
        DefaultHttpContext context = new()
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, Guid.CreateVersion7().ToString()),
                    new Claim(PermissionClaim.Type, "catalog:write")
                ]))
        };

        HttpContextCurrentUser user = new(new HttpContextAccessor { HttpContext = context });

        user.IsAuthenticated.ShouldBeFalse();
        user.HasPermission("catalog:write").ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => user.Id);
    }

    [Fact]
    public void A_second_unauthenticated_identity_contributes_nothing()
    {
        // ClaimsPrincipal.Identity is the *primary* identity; FindFirst and
        // HasClaim search every identity the principal holds. So a check that
        // tests the principal and then reads its claims is testing one thing
        // and reading another — and a host authenticating over two schemes
        // produces exactly this shape. The authenticated identity here carries
        // the subject and no permission; the unauthenticated one carries the
        // permission, and must not be able to grant it.
        Guid subject = Guid.CreateVersion7();

        ClaimsPrincipal principal = new(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, subject.ToString())],
                authenticationType: "Test"));

        principal.AddIdentity(
            new ClaimsIdentity([new Claim(PermissionClaim.Type, "catalog:write")]));

        DefaultHttpContext context = new() { User = principal };

        HttpContextCurrentUser user = new(new HttpContextAccessor { HttpContext = context });

        user.IsAuthenticated.ShouldBeTrue();
        user.Id.ShouldBe(subject);
        user.HasPermission("catalog:write").ShouldBeFalse();
    }

    [Fact]
    public void A_permission_is_the_claim_the_policies_require()
    {
        // The same claim type AuthorizationPolicyExtensions.RequirePermission
        // registers, which is what stops an endpoint policy and a
        // resource-level check disagreeing about what a permission is (§11.4).
        // The negative half is the one that matters: a HasPermission that
        // answered true for everything would pass every ownership test in the
        // platform and grant every override in it.
        HttpContextCurrentUser user = For(
            new Claim(ClaimTypes.NameIdentifier, Guid.CreateVersion7().ToString()),
            new Claim(PermissionClaim.Type, "orders:admin"));

        user.HasPermission("orders:admin").ShouldBeTrue();
        user.HasPermission("orders:cancel").ShouldBeFalse();

        // Values are matched whole. A prefix match would make "orders:admin"
        // satisfy a check for "orders:ad", and a claim holding a
        // space-separated list would satisfy checks it never granted.
        user.HasPermission("orders").ShouldBeFalse();
    }

    [Fact]
    public void A_permission_in_another_claim_type_grants_nothing()
    {
        // A realm mapper writing the platform's permissions into "roles" or
        // "scope" instead of the claim §11.4 requires is exactly the defect
        // RealmImportTests guards against on the configuration side. This is
        // the code side of the same agreement: whatever else the token
        // carries, only this claim type is a permission.
        HttpContextCurrentUser user = For(
            new Claim(ClaimTypes.NameIdentifier, Guid.CreateVersion7().ToString()),
            new Claim("roles", "orders:admin"),
            new Claim("scope", "orders:admin"));

        user.HasPermission("orders:admin").ShouldBeFalse();
    }
}
