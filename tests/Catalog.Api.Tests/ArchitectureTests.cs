using NetArchTest.Rules;
using Shouldly;
using Xunit;
using TestResult = NetArchTest.Rules.TestResult;

namespace Catalog.Api.Tests;

/// <summary>
/// §4.2's composition-root rule: only <c>Program.cs</c> may reference
/// Infrastructure, and endpoints hold to Application and Domain contracts.
/// Vacuously green until PR-10 adds the first endpoint — which is the point:
/// a rule introduced before the violations exist is a constraint, not a
/// backlog item.
/// </summary>
public class ArchitectureTests
{
    [Fact]
    public void Endpoints_do_not_depend_on_infrastructure()
    {
        TestResult result = Types
            .InAssembly(typeof(Program).Assembly)
            .That().ResideInNamespaceContaining(".Endpoints")
            .ShouldNot().HaveDependencyOn("Catalog.Infrastructure")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"leaked: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }
}
