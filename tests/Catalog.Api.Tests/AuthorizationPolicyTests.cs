using Common.Application;
using System.Reflection;
using Catalog.TestSupport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>
/// §11.4's callout, executed: "enumerate the endpoint policy names from
/// <c>EndpointDataSource</c> in a test and require each to resolve through
/// <c>IAuthorizationPolicyProvider</c>."
/// </summary>
/// <remarks>
/// A policy name is a reference and nothing checks it.
/// <c>RequireAuthorization("catalog:write")</c> takes a string — misspell it,
/// or register the policy in a helper the host never calls, and there is no
/// compiler error, no <c>ValidateOnBuild</c> failure and no startup warning.
/// The endpoint throws <c>InvalidOperationException</c> the first time somebody
/// publishes a product, which is to say in production, on the path that matters.
///
/// This reads the names off the built endpoints rather than from a list beside
/// the registrations, which is the whole point: a list would be a third place
/// to keep in step, and it would agree with itself while disagreeing with the
/// host. The unreachable-infrastructure factory is enough — routing and
/// authorization are built from registrations, and neither asks the database
/// anything.
/// </remarks>
public class AuthorizationPolicyTests(HostSmokeTests.UnreachableInfrastructureFactory factory)
    : IClassFixture<HostSmokeTests.UnreachableInfrastructureFactory>
{
    private IEnumerable<Endpoint> Endpoints =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

    [Fact]
    public async Task Every_policy_an_endpoint_names_resolves()
    {
        IAuthorizationPolicyProvider policies =
            factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        string[] named =
        [
            .. Endpoints
                .SelectMany(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>())
                .Select(a => a.Policy)
                .OfType<string>()
                .Distinct()
        ];

        // Not vacuous: the assertion below passes trivially over an empty set,
        // and an endpoint file that lost its RequireAuthorization line would
        // produce exactly that.
        named.ShouldContain(CatalogPermissions.Write);

        foreach (string policy in named)
        {
            AuthorizationPolicy? resolved = await policies.GetPolicyAsync(policy);

            resolved.ShouldNotBeNull(
                $"'{policy}' is named by an endpoint and registered nowhere — the endpoint " +
                "throws on the first request that reaches it, never at startup (§11.4)");
        }
    }

    [Fact]
    public async Task The_shared_authenticated_policy_is_registered_by_common_web()
    {
        // Not named by any Catalog endpoint — the group uses the default policy
        // — so the test above cannot see it. It exists for the gateway's route
        // file (§10.2), which resolves it through this same provider when YARP
        // loads the configuration, and refuses to start when it cannot: the
        // load throws out of MapReverseProxy() naming the policy and the route,
        // so the process does not come up at all.
        //
        // This comment said the opposite until PR-17 measured it — a silent
        // per-route drop leaving the gateway healthy — which is what four
        // blueprint sites also said. The correction runs the reassuring way and
        // does not weaken the reason for this test: the gateway fails at
        // deployment rather than in production, and a name it cannot resolve
        // still costs a deployment. PR-17 binds it; asserting it here is what
        // makes that binding safe to write.
        IAuthorizationPolicyProvider policies =
            factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        (await policies.GetPolicyAsync("authenticated")).ShouldNotBeNull();
    }

    [Fact]
    public void The_listing_is_anonymous_and_the_write_path_is_not()
    {
        // §10.2's catalog-public route is GET-only with no AuthorizationPolicy,
        // so the listing is public by design rather than by omission — and the
        // pairing is what makes that readable. One endpoint carrying
        // IAllowAnonymous and the other carrying a policy is the whole of
        // PR-16's decision about this service, asserted where a future edit to
        // either line would be caught.
        Endpoint listing = Single("GetProducts");
        Endpoint publish = Single("PublishProduct");

        listing.Metadata.GetMetadata<IAllowAnonymous>().ShouldNotBeNull();
        publish.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull(
            "an AllowAnonymous anywhere on the write path defeats every policy on it");

        publish.Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Select(a => a.Policy)
            .ShouldContain(CatalogPermissions.Write);
    }

    [Fact]
    public void Every_idempotent_command_reaches_this_service_through_an_authenticated_endpoint()
    {
        // §8.5's rule, and until this test it was a claim IIdempotentCommand's
        // own remarks made about a gate that did not exist: "a test asserts it
        // of every command declaring this". Nothing did. The two
        // IdempotencyOptInTests suites read command SHAPE — the interface, the
        // result type, the operation name — and none of them can see an
        // endpoint.
        //
        // What the rule protects: ICurrentUser.IsAuthenticated is false for an
        // anonymous HTTP request, so its subject falls back to the shared
        // "system" segment. Two anonymous callers reusing one CommandId then
        // collide inside the mechanism that exists to keep them apart, and the
        // second is served the first's stored result. An endpoint marked
        // AllowAnonymous is all it takes.
        //
        // Read off the built endpoints for AuthorizationPolicyTests' own
        // reason: a list beside the registrations would agree with itself while
        // disagreeing with the host.
        Endpoint[] idempotent =
        [
            .. Endpoints.Where(e => e.Metadata
                .GetMetadata<MethodInfo>()?
                .GetParameters()
                .Any(p => typeof(IIdempotentCommand).IsAssignableFrom(p.ParameterType)) == true)
        ];

        // The gate's own subject. Both assertions below are over a sequence, so
        // an empty one passes them — and this selector depends on minimal APIs
        // putting the handler's MethodInfo in endpoint metadata, which is a
        // framework detail rather than a contract.
        idempotent.ShouldNotBeEmpty(
            "this service declares an idempotent command bound to an endpoint; the selector found none");

        foreach (Endpoint endpoint in idempotent)
        {
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull(
                $"{endpoint.DisplayName} takes an idempotent command and allows anonymous callers, " +
                "so every one of them claims under the shared system subject (§8.5)");

            endpoint.Metadata
                .GetOrderedMetadata<IAuthorizeData>()
                .ShouldNotBeEmpty(
                    $"{endpoint.DisplayName} takes an idempotent command and requires no authorization, " +
                    "so the caller has no subject to key on (§8.5)");
        }
    }

    private Endpoint Single(string name) =>
        Endpoints.Single(e => e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);
}
