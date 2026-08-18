using NetArchTest.Rules;
using Shouldly;
using Xunit;
using TestResult = NetArchTest.Rules.TestResult;

namespace Catalog.Api.Tests;

/// <summary>
/// §4.2's composition-root rule: only <c>Program.cs</c> may reference
/// Infrastructure, and everything else in the host holds to Application and
/// Domain contracts.
/// Vacuously green from PR-07 until PR-10's first endpoint — a rule
/// introduced before the violations exist is a constraint, not a backlog
/// item — and judging real types since.
/// </summary>
/// <remarks>
/// <b>This gate has been wrong four times, and every one was the same mistake:
/// selecting less than it claimed.</b> A namespace
/// (<c>.ResideInNamespaceContaining(".Endpoints")</c>) stopped covering the
/// transport surface when <c>PricingService</c> arrived in <c>.Grpc</c>; a
/// namespace <i>pattern</i> moved the hole one namespace further out; excluding
/// compiler-generated types by name exempted **endpoint lambdas**, because a
/// closure is generated code; and filtering candidates through
/// <c>HaveName(...)</c> exempted them again, because that predicate selects
/// nothing for a nested async state machine and an empty selection reports
/// success.
/// <para>
/// So this version selects <b>nothing at all</b> and filters the
/// <i>result</i>. There is no candidate set to be narrow, no predicate to miss
/// a name, and no empty selection to pass vacuously — the assembly is judged
/// whole, and the composition root is subtracted from the failures afterwards,
/// where full names are available to subtract it by.
/// </para>
/// <para>
/// <b>A residual survives all four shapes, and it is the tool's rather than
/// the rule's.</b> NetArchTest does not analyse compiler-generated nested
/// types, so a forbidden reference used <i>only</i> inside an endpoint lambda
/// is invisible to it. Measured both ways, which is the only reason this is
/// stated rather than guessed: a <c>DbContextOptionsBuilder</c> in
/// an endpoint method's own body fails this gate and names that endpoint
/// class; the identical line inside one of its lambdas leaves it green —
/// including with no selector at all, which is what rules out a narrowing
/// predicate as the cause.
/// </para>
/// <para>
/// So the shape below is the strongest available and not a complete one. It
/// catches every reference written in a method body, which is where they are
/// written; the lambda case is named here rather than papered over, because a
/// gate believed to be total is worse than one whose gap is written down.
/// </para>
/// </remarks>
public class ArchitectureTests
{
    private static readonly string[] Forbidden =
    [
        "Catalog.Infrastructure",
        "Microsoft.EntityFrameworkCore",
        "MassTransit",
        "StackExchange.Redis"
    ];

    /// <summary>
    /// Whether a failing type is the composition root or code the compiler
    /// emitted <i>for it</i> — the one exemption §4.2 grants.
    /// </summary>
    /// <remarks>
    /// Top-level statements put <c>Program</c> and its helpers
    /// (<c>&lt;PrivateImplementationDetails&gt;</c>, the anonymous delegate
    /// types its lambdas need) in the <b>global namespace</b>, so they carry no
    /// dot. Everything an endpoint generates is nested inside the endpoint
    /// class and keeps its namespace — which is exactly the distinction the
    /// name-prefix exclusion could not draw.
    /// </remarks>
    private static bool IsCompositionRoot(string fullName) =>
        fullName == "Program" || (!fullName.Contains('.') && fullName.StartsWith('<'));

    [Fact]
    public void Nothing_but_the_composition_root_depends_on_infrastructure()
    {
        // Not the service's Infrastructure namespace alone: §4.2's rule is
        // "Application and Domain contracts only", and the concrete types it
        // bans — DbContext, IPublishEndpoint, IConnectionMultiplexer — reach a
        // type transitively without any Catalog.Infrastructure dependency to
        // trip on.
        TestResult result = Types
            .InAssembly(typeof(Program).Assembly)
            .ShouldNot().HaveDependencyOnAny(Forbidden)
            .GetResult();

        string[] leaked = [.. (result.FailingTypeNames ?? []).Where(name => !IsCompositionRoot(name))];

        leaked.ShouldBeEmpty($"leaked: {string.Join(", ", leaked)}");
    }

    [Fact]
    public void The_composition_root_is_the_only_thing_exempted()
    {
        string[] exempted =
        [
            .. typeof(Program).Assembly
                .GetTypes()
                .Select(type => type.FullName ?? type.Name)
                .Where(IsCompositionRoot)
        ];

        // The exemption is the whole of this gate's trust, so it is asserted
        // rather than assumed: it must cover the root and must not quietly
        // grow. `Program` being present is what says the rule is looking at
        // this host at all — a wrongly-anchored assembly reference would
        // produce an empty list here and a vacuously green rule above.
        exempted.ShouldContain("Program");
        exempted.Length.ShouldBeLessThanOrEqualTo(
            4,
            "the exemption should cover Program and its own generated helpers, nothing more");
    }
}
