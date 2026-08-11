using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>
/// What a policy built by <c>RequirePermission</c> actually demands (§11.4).
/// </summary>
/// <remarks>
/// The interesting case is the one a caller cannot see: `RequireClaim` reads
/// the claims on <c>HttpContext.User</c> and asks nothing about whether
/// anything authenticated it, so a policy built from the claim alone succeeds
/// for a principal <c>IsAuthenticated</c> denies. Catalog never reaches that
/// state — its route group adds <c>RequireAuthorization()</c> and the two
/// policies combine — but that is a property of one caller, and this is the
/// method every service will use.
/// </remarks>
public class PermissionPolicyTests
{
    private const string Permission = "catalog:write";

    private static AuthorizationPolicy Policy()
    {
        ServiceCollection services = new();

        services.AddAuthorizationBuilder()
            .AddPolicy(Permission, policy => policy.RequirePermission(Permission));

        return services.BuildServiceProvider()
            .GetRequiredService<IAuthorizationPolicyProvider>()
            .GetPolicyAsync(Permission)
            .GetAwaiter()
            .GetResult()!;
    }

    private static Task<AuthorizationResult> EvaluateAsync(ClaimsPrincipal user)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddAuthorization();

        return services.BuildServiceProvider()
            .GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(user, resource: null, Policy());
    }

    [Fact]
    public async Task A_caller_holding_the_permission_is_authorised()
    {
        ClaimsPrincipal user = new(
            new ClaimsIdentity([new Claim(PermissionClaim.Type, Permission)], "Test"));

        (await EvaluateAsync(user)).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task A_caller_holding_another_permission_is_not()
    {
        ClaimsPrincipal user = new(
            new ClaimsIdentity([new Claim(PermissionClaim.Type, "catalog:read")], "Test"));

        (await EvaluateAsync(user)).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task An_unauthenticated_principal_carrying_the_claim_is_not()
    {
        // The finding, and the reason RequirePermission calls
        // RequireAuthenticatedUser rather than trusting its callers. This is
        // the identity shape HttpContextCurrentUserTests already refuses to
        // answer for: claims present, authentication type absent, so
        // IsAuthenticated is false while every claim is readable.
        //
        // Without the authenticated requirement this policy succeeds — the
        // claim is there, and ClaimsAuthorizationRequirement asks nothing else.
        ClaimsPrincipal user = new(
            new ClaimsIdentity([new Claim(PermissionClaim.Type, Permission)]));

        user.Identity!.IsAuthenticated.ShouldBeFalse("the premise of this test");
        (await EvaluateAsync(user)).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public void The_policy_states_both_requirements()
    {
        // Read off the built policy rather than inferred from behaviour, so a
        // reader can see that authentication is part of the contract this
        // method offers rather than something a caller supplies.
        AuthorizationPolicy policy = Policy();

        policy.Requirements.OfType<DenyAnonymousAuthorizationRequirement>().ShouldHaveSingleItem();
        policy.Requirements.OfType<ClaimsAuthorizationRequirement>().ShouldHaveSingleItem();
    }
}
