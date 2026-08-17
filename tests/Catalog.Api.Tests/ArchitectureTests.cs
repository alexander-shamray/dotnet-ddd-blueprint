using NetArchTest.Rules;
using Shouldly;
using Xunit;
using TestResult = NetArchTest.Rules.TestResult;

namespace Catalog.Api.Tests;

/// <summary>
/// §4.2's composition-root rule: only <c>Program.cs</c> may reference
/// Infrastructure, and the transport surface holds to Application and Domain
/// contracts.
/// Vacuously green from PR-07 until PR-10's first endpoint — a rule
/// introduced before the violations exist is a constraint, not a backlog
/// item — and judging real types since.
/// </summary>
public class ArchitectureTests
{
    /// <summary>
    /// Every namespace holding a transport adapter.
    /// </summary>
    /// <remarks>
    /// <b>It was <c>.Endpoints</c> alone, and PR-19 made that a hole rather
    /// than a rule.</b> <c>PricingService</c> is an endpoint in every sense
    /// §4.2 cares about — it is mapped into the pipeline, it is reachable from
    /// outside the process, and it must hold to Application contracts — and it
    /// lives in <c>.Grpc</c>, so the gate selected nothing of it and stayed
    /// green. A gate that silently stops covering the newest surface is the
    /// failure mode this repository keeps naming, so the fix comes with the
    /// test below that makes the selection itself an assertion.
    /// </remarks>
    private const string TransportNamespaces = @"\.(Endpoints|Grpc)$";

    [Fact]
    public void The_transport_surface_does_not_depend_on_infrastructure()
    {
        // Not the service's Infrastructure namespace alone: §4.2's rule is
        // "Application and Domain contracts only", and the concrete types it
        // bans — DbContext, IPublishEndpoint, IConnectionMultiplexer — reach an
        // adapter transitively without any Catalog.Infrastructure dependency to
        // trip on.
        TestResult result = Types
            .InAssembly(typeof(Program).Assembly)
            .That().ResideInNamespaceMatching(TransportNamespaces)
            .ShouldNot().HaveDependencyOnAny(
                "Catalog.Infrastructure",
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
        // opposite of what this test is for: it asserts that the two adapters
        // this host has are both inside the rule, so a third arriving in a
        // namespace the pattern misses fails HERE rather than passing
        // silently there.
        selected.ShouldContain(nameof(Endpoints.ProductEndpoints));
        selected.ShouldContain("PricingService");
    }
}
