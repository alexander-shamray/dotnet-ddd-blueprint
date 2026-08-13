using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>
/// What YARP actually does with a route naming a policy nobody registered.
/// </summary>
/// <remarks>
/// <para>
/// <b>It refuses to start, and the blueprint said the opposite in four
/// places.</b> §10.2, §4.2's gateway sample, §11.4's callout and Appendix C's
/// PR-17 row all described a silent drop — "the path simply stops existing,
/// and the gateway comes up healthy serving whichever routes happened to
/// validate". At the pinned YARP, <c>ProxyConfigManager.InitialLoadAsync</c>
/// throws out of <c>MapReverseProxy()</c> instead, with an
/// <c>InvalidOperationException</c> whose inner message names the policy and
/// the route. All four were amended in this change; the failure is loud, not
/// quiet, and the gateway fails better than a service does — §11.4's
/// unregistered service policy still throws on the first request that reaches
/// the endpoint, which is to say in production.
/// </para>
/// <para>
/// This is why the suite is written as tests over a live host rather than over
/// the JSON: every test in this project builds the gateway, so any route file
/// the gateway would refuse fails all of them, and the two tests here are what
/// prove that guard is a real filter rather than a tautology.
/// </para>
/// </remarks>
public sealed class UnresolvablePolicyTests
{
    [Fact]
    public void A_route_naming_an_unregistered_authorization_policy_refuses_to_start()
    {
        using UnresolvableAuthorizationPolicyFactory factory = new();

        InvalidOperationException thrown =
            Should.Throw<InvalidOperationException>(() => _ = factory.Services);

        // ToString rather than Message: the naming is done by the
        // ArgumentException two levels in, and what a deployment reads is the
        // whole chain.
        thrown.ToString().ShouldContain("no-such-policy");
        thrown.ToString().ShouldContain("broken");
    }

    /// <summary>
    /// The same question of the other registry, because §10.2 keeps two apart
    /// under one word and they are validated by different code.
    /// </summary>
    [Fact]
    public void A_route_naming_an_unregistered_rate_limiter_policy_refuses_to_start()
    {
        using UnresolvableRateLimiterPolicyFactory factory = new();

        InvalidOperationException thrown =
            Should.Throw<InvalidOperationException>(() => _ = factory.Services);

        thrown.ToString().ShouldContain("no-such-policy");
        thrown.ToString().ShouldContain("broken");
    }

    private sealed class UnresolvableAuthorizationPolicyFactory : GatewayFactory
    {
        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
        [
            new("ReverseProxy:Routes:broken:ClusterId", "catalog"),
            new("ReverseProxy:Routes:broken:Match:Path", "/api/v1/broken/{**catch-all}"),
            new("ReverseProxy:Routes:broken:AuthorizationPolicy", "no-such-policy"),
            new("ReverseProxy:Routes:broken:RateLimiterPolicy", GatewayRateLimiterPolicies.Anonymous)
        ];
    }

    private sealed class UnresolvableRateLimiterPolicyFactory : GatewayFactory
    {
        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
        [
            new("ReverseProxy:Routes:broken:ClusterId", "catalog"),
            new("ReverseProxy:Routes:broken:Match:Path", "/api/v1/broken/{**catch-all}"),
            new("ReverseProxy:Routes:broken:RateLimiterPolicy", "no-such-policy")
        ];
    }
}
