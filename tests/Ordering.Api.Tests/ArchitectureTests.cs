using NetArchTest.Rules;
using Shouldly;
using Xunit;
using TestResult = NetArchTest.Rules.TestResult;

namespace Ordering.Api.Tests;

/// <summary>
/// §4.2's composition-root rule: only <c>Program.cs</c> may reference
/// Infrastructure, and everything else in the host holds to Application and
/// Domain contracts.
/// This arrived vacuously green — the scaffold emits it before a service
/// has an endpoint, because a rule introduced before the violations exist is
/// a constraint rather than a backlog item. <c>OrderEndpoints</c> gives it
/// real types to judge.
/// </summary>
/// <remarks>
/// <b>This gate selected a NAMESPACE until PR-19, and twice that was wrong in
/// the same way.</b> It began as <c>.ResideInNamespaceContaining(".Endpoints")</c>,
/// which silently stopped covering the transport surface when
/// <c>PricingService</c> arrived in Catalog's <c>.Grpc</c>; widening it to a pattern
/// fixed that instance and kept the defect, because a third adapter under some
/// future <c>.GraphQL</c> would be outside the pattern and outside the rule
/// again — and a companion test naming the known adapters could not see that
/// either, since the set it inspects is unchanged by a type the pattern never
/// selected.
/// <para>
/// So the selector is gone. The rule now covers the <b>whole assembly</b>
/// except the composition root, which is what §4.2's prose says in the first
/// place, and namespace choice stops being load-bearing. There is nothing left
/// for a new namespace to escape.
/// </para>
/// </remarks>
public class ArchitectureTests
{
    /// <summary>
    /// The two exclusions, and both are narrow on purpose.
    /// </summary>
    /// <remarks>
    /// <c>Program</c> is the composition root — the one place §4.2 permits an
    /// Infrastructure reference — and naming it is what makes the exemption
    /// visible. Renaming the root would drop the exclusion and fail this test
    /// loudly, which is the right direction to fail in.
    /// <para>
    /// The <c>&lt;</c> prefix is the compiler's: closures and
    /// <c>&lt;PrivateImplementationDetails&gt;</c> emitted for
    /// <c>Program.cs</c>'s own statements inherit its references, so they are
    /// the root's shadow rather than code anybody wrote.
    /// </para>
    /// </remarks>
    private static PredicateList HostTypesOutsideTheCompositionRoot() => Types
        .InAssembly(typeof(Program).Assembly)
        .That().DoNotHaveName("Program")
        .And().DoNotHaveNameStartingWith("<");

    [Fact]
    public void Nothing_but_the_composition_root_depends_on_infrastructure()
    {
        // Not the service's Infrastructure namespace alone: §4.2's rule is
        // "Application and Domain contracts only", and the concrete types it
        // bans — DbContext, IPublishEndpoint, IConnectionMultiplexer — reach a
        // type transitively without any Catalog.Infrastructure dependency to
        // trip on.
        TestResult result = HostTypesOutsideTheCompositionRoot()
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
    public void The_gate_above_is_judging_this_host_at_all()
    {
        string[] judged = [.. HostTypesOutsideTheCompositionRoot().GetTypes().Select(t => t.Name)];

        // The only vacuity left to guard. The rule above covers everything but
        // the root, so no namespace can escape it — what could still make it
        // meaningless is selecting nothing, which is what a wrongly-anchored
        // assembly reference would produce.
        //
        // Named rather than counted: a count goes stale on every new type and
        // gets "fixed" by editing the number. These two are the host's
        // transport surface, and they are what the rule exists for.
        judged.ShouldContain(nameof(Endpoints.OrderEndpoints));
    }
}
