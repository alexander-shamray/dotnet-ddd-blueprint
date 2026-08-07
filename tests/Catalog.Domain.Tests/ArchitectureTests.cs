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
    public void Domain_references_only_common_domain_and_the_framework()
    {
        // The dependency table's rule is an allow-list — "Common.Domain and
        // nothing else" — so the gate is one too: a blacklist can only ban
        // the libraries someone thought to name, and Common.Application,
        // another service's Domain or a new package all slip past it.
        // System.Text.Json is the one framework assembly the table bans by
        // name: a domain type must not serialise itself.
        IEnumerable<string> referenced = typeof(AssemblyMarker).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!);

        referenced.ShouldAllBe(name =>
            name == "Common.Domain" ||
            (name.StartsWith("System.") && !name.StartsWith("System.Text.Json")));
    }
}
