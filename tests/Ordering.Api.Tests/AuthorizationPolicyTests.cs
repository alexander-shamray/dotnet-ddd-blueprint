using Common.Application;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Application.Orders;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>
/// §11.4's callout, executed: "enumerate the endpoint policy names from
/// <c>EndpointDataSource</c> in a test and require each to resolve through
/// <c>IAuthorizationPolicyProvider</c>."
/// </summary>
/// <remarks>
/// A policy name is a reference and nothing checks it.
/// <c>RequireAuthorization("orders:write")</c> takes a string — misspell it,
/// or register the policy in a helper the host never calls, and there is no
/// compiler error, no <c>ValidateOnBuild</c> failure and no startup warning.
/// The endpoint throws <c>InvalidOperationException</c> the first time somebody
/// places an order, which is to say in production, on the path that matters.
///
/// This reads the names off the built endpoints rather than from a list beside
/// the registrations, which is the whole point: a list would be a third place
/// to keep in step, and it would agree with itself while disagreeing with the
/// host.
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
        named.ShouldContain(OrderingPermissions.Write);
        named.ShouldContain(OrderingPermissions.Cancel);

        foreach (string policy in named)
        {
            AuthorizationPolicy? resolved = await policies.GetPolicyAsync(policy);

            resolved.ShouldNotBeNull(
                $"'{policy}' is named by an endpoint and registered nowhere — the endpoint " +
                "throws on the first request that reaches it, never at startup (§11.4)");
        }
    }

    [Fact]
    public async Task Orders_admin_is_a_claim_and_is_deliberately_not_a_policy()
    {
        // §11.4 is emphatic that the two are different things, and the names
        // in this service's vocabulary look alike enough that registering a
        // third policy would read as consistency. CancelOrderHandler reads
        // orders:admin through ICurrentUser.HasPermission against a loaded
        // aggregate — a question no endpoint policy could answer, because the
        // order is not loaded when the policy runs.
        //
        // A literal here for the same reason CancelOrderHandler spells one:
        // naming it from OrderingPermissions would put it in the vocabulary
        // this test exists to keep it out of, and §11.4 says that class holds
        // policies alone.
        //
        // Asserted rather than left to the comment above: this fails the day
        // somebody adds the policy, which is the moment to reread §11.4 and
        // decide deliberately rather than by symmetry.
        IAuthorizationPolicyProvider policies =
            factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        (await policies.GetPolicyAsync("orders:admin")).ShouldBeNull();

        Endpoints
            .SelectMany(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>())
            .Select(a => a.Policy)
            .ShouldNotContain("orders:admin");
    }

    [Fact]
    public async Task The_shared_authenticated_policy_is_registered_by_common_web()
    {
        // Not named by any Ordering endpoint — the group uses the default
        // policy — so the test above cannot see it. It exists for the
        // gateway's route file (§10.2), whose `ordering` route names it, and
        // YARP resolves it through this same provider when it loads the
        // configuration.
        IAuthorizationPolicyProvider policies =
            factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        (await policies.GetPolicyAsync("authenticated")).ShouldNotBeNull();
    }

    [Fact]
    public void No_ordering_endpoint_is_anonymous()
    {
        // The whole difference from Catalog, asserted where an edit to either
        // endpoint would be caught: a product listing is public by design,
        // and an order belongs to somebody. An AllowAnonymous anywhere here
        // defeats every policy on the path it sits on.
        foreach (Endpoint endpoint in Endpoints.Where(e => Name(e) is "PlaceOrder" or "CancelOrder"))
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull(Name(endpoint));

        // Not vacuous — the loop above passes over an empty set, which is what
        // a renamed endpoint would produce.
        Endpoints.Count(e => Name(e) is "PlaceOrder" or "CancelOrder").ShouldBe(2);
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

    private static string? Name(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
}
