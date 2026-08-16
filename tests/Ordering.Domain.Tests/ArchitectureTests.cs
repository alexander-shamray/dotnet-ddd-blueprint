using Ordering.Domain.Orders;
using Shouldly;
using Xunit;

namespace Ordering.Domain.Tests;

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
        // nothing else" — so the gate is one too, and an exact one: a
        // blacklist only bans what someone thought to name, and a System.*
        // prefix still passes System.Data.SqlClient or a serialiser. Each
        // BCL assembly Domain starts using earns its line here on purpose —
        // extending this list is the decision the gate exists to force, and
        // System.Text.Json is the extension the table forbids by name.
        //
        // Four entries now, and the two beyond the empty domain's pair are
        // exactly the two this comment predicted before there was a model to
        // judge. System.Collections arrived with the first domain event,
        // whose generated record equality goes through EqualityComparer<T>;
        // System.Linq with Money.Of's char.IsAsciiLetter scan and Order's
        // Aggregate over its lines — domain work over owned values, not an
        // I/O dependency. Catalog's list reads the same way for the same two
        // reasons, which is the allow-list behaving as a shared rule rather
        // than as one service's accident.
        string[] allowed = ["Common.Domain", "System.Runtime", "System.Collections", "System.Linq"];

        IEnumerable<string> referenced = typeof(Order).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!);

        referenced.ShouldAllBe(name => allowed.Contains(name));
    }
}
