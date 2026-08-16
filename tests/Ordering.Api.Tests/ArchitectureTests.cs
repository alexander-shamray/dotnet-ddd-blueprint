using NetArchTest.Rules;
using Shouldly;
using Xunit;
using TestResult = NetArchTest.Rules.TestResult;

namespace Ordering.Api.Tests;

/// <summary>
/// §4.2's composition-root rule: only <c>Program.cs</c> may reference
/// Infrastructure, and endpoints hold to Application and Domain contracts.
/// Vacuously green until this service maps its first endpoint: a rule
/// introduced before the violations exist is a constraint, not a backlog
/// item. The rule was observed failing against a deliberately added
/// forbidden reference before it was trusted — in the service this one
/// was scaffolded from, not here, where there is nothing yet to judge.
/// </summary>
public class ArchitectureTests
{
    [Fact]
    public void Endpoints_do_not_depend_on_infrastructure()
    {
        // Not the service's Infrastructure namespace alone: §4.2's rule is
        // "Application and Domain contracts only", and the concrete types it
        // bans — DbContext, IPublishEndpoint, IConnectionMultiplexer — reach
        // an endpoint transitively without any Ordering.Infrastructure
        // dependency to trip on.
        TestResult result = Types
            .InAssembly(typeof(Program).Assembly)
            .That().ResideInNamespaceContaining(".Endpoints")
            .ShouldNot().HaveDependencyOnAny(
                "Ordering.Infrastructure",
                "Microsoft.EntityFrameworkCore",
                "MassTransit",
                "StackExchange.Redis")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"leaked: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }
}
