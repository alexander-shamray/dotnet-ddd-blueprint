namespace Gateway.Api;

/// <summary>
/// The rate-limiter policy names of §10.3, which every route in §10.2 must
/// name one of.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>Authenticated</c> is not the authorization policy of the same
/// name.</b> §10.2 keeps two registries apart under one word:
/// <c>AuthorizationPolicy</c> resolves through
/// <see cref="Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider"/>
/// and <c>RateLimiterPolicy</c> through the rate limiter's own map. They agree
/// on the string because both mean "a signed-in caller", and each has to be
/// registered separately — a route naming one of them and not the other is
/// not half-configured, it is configured for one thing and unlimited for the
/// other.
/// </para>
/// <para>
/// A class of constants rather than two literals in <c>Program.cs</c> because
/// the rate limiter's policy map is internal to the framework: there is no
/// public way to ask a built host which rate-limiter policies it registered.
/// YARP covers one direction on its own — a route naming a policy that is not
/// registered refuses to load, measured in <c>UnresolvablePolicyTests</c> —
/// and this list covers the other, which nothing else can see: a policy
/// registered here and named by no route is a registration with no reader,
/// the defect §11.4 names for an unused authorization policy.
/// </para>
/// </remarks>
public static class GatewayRateLimiterPolicies
{
    /// <summary>Per-IP fixed window, for routes carrying no authorization policy.</summary>
    public const string Anonymous = "anonymous";

    /// <summary>Per-subject token bucket, falling back to the address (§10.3).</summary>
    public const string Authenticated = "authenticated";

    /// <summary>
    /// Every policy <c>Program.cs</c> registers, in one place a test can read.
    /// Both directions are asserted against the route file: a route may name
    /// nothing outside this set, and nothing in this set may go unnamed — an
    /// unused limiter policy is a registration with no reader, which is the
    /// same defect §11.4 names for an unused authorization policy.
    /// </summary>
    public static readonly IReadOnlyList<string> All = [Anonymous, Authenticated];
}
