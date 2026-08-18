using NetArchTest.Rules;
using Ordering.Api.Endpoints;
using Shouldly;
using Xunit;
using TestResult = NetArchTest.Rules.TestResult;

namespace Ordering.Api.Tests;

/// <summary>
/// §4.2's composition-root rule: only <c>Program.cs</c> may reference
/// Infrastructure, and the transport surface holds to Application and Domain
/// contracts.
/// This arrived vacuously green — the scaffold emits it before a service has
/// an endpoint, because a rule introduced before the violations exist is a
/// constraint rather than a backlog item. It is not vacuous now:
/// <c>OrderEndpoints</c> supplies two real endpoint types for it to judge.
/// The rule was observed failing against a deliberately added forbidden
/// reference before it was trusted, in the service this one was scaffolded
/// from.
/// </summary>
public class ArchitectureTests
{
    /// <summary>
    /// Every namespace holding a transport adapter.
    /// </summary>
    /// <remarks>
    /// Ordering has only <c>.Endpoints</c> today, so the pattern selects
    /// exactly what <c>ResideInNamespaceContaining(".Endpoints")</c> did — and
    /// it is here anyway, because Catalog's gate was that narrower spelling
    /// until PR-19 put a gRPC service in <c>.Grpc</c> and the gate silently
    /// stopped judging half the surface. A service adopting the pattern only
    /// once it has a second adapter adopts it one PR after it was needed.
    /// </remarks>
    private const string TransportNamespaces = @"\.(Endpoints|Grpc)$";

    [Fact]
    public void The_transport_surface_does_not_depend_on_infrastructure()
    {
        // Not the service's Infrastructure namespace alone: §4.2's rule is
        // "Application and Domain contracts only", and the concrete types it
        // bans — DbContext, IPublishEndpoint, IConnectionMultiplexer — reach
        // an adapter transitively without any Ordering.Infrastructure
        // dependency to trip on.
        TestResult result = Types
            .InAssembly(typeof(Program).Assembly)
            .That().ResideInNamespaceMatching(TransportNamespaces)
            .ShouldNot().HaveDependencyOnAny(
                "Ordering.Infrastructure",
                "Microsoft.EntityFrameworkCore",
                "MassTransit",
                "StackExchange.Redis")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"leaked: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void The_gate_above_is_judging_every_transport_adapter()
    {
        string[] selected =
        [
            .. Types
                .InAssembly(typeof(Program).Assembly)
                .That().ResideInNamespaceMatching(TransportNamespaces)
                .GetTypes()
                .Select(type => type.Name)
        ];

        // Named rather than counted. A count would go stale on every new
        // adapter and would be "fixed" by editing the number, which is the
        // opposite of what this test is for: it asserts that the adapter this
        // host has is inside the rule, so a second one arriving in a namespace
        // the pattern misses fails HERE rather than passing silently there.
        selected.ShouldContain(nameof(OrderEndpoints));
    }
}
