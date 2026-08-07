using Shouldly;
using Xunit;

namespace Catalog.Domain.Tests;

/// <summary>
/// §4.2's first gate, a CI failure from the first template commit rather than
/// a review convention. Plain reflection, no NetArchTest: the rule is about
/// assembly references, not type dependencies, and
/// <c>GetReferencedAssemblies</c> asks exactly that question.
/// </summary>
public class ArchitectureTests
{
    [Fact]
    public void Domain_has_no_infrastructure_dependencies()
    {
        string[] forbidden =
        [
            "Microsoft.EntityFrameworkCore",
            "MassTransit",
            "StackExchange.Redis",
            "Microsoft.AspNetCore"
        ];

        IEnumerable<string> referenced = typeof(AssemblyMarker).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!);

        referenced.ShouldNotContain(name => forbidden.Any(name.StartsWith));
    }
}
